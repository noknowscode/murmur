using System.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Media;
using SkiaSharp;
using Uno.WinUI.Graphics2DSK;
using Windows.Foundation;

namespace Murmuration;

/// <summary>
/// Streaming text with a flock as the write head. Feed it tokens with <see cref="Append"/>; each finished word
/// borrows a few birds to draw itself, the text fades in under them, and the birds rejoin the flock.
/// The control draws its own text with Skia so birds and glyphs register exactly.
/// </summary>
public sealed class FlockStreamingText : SKCanvasElement, IFlockClient
{
    public static readonly DependencyProperty FontSizeProperty = DependencyProperty.Register(nameof(FontSize), typeof(double), typeof(FlockStreamingText), new PropertyMetadata(16d, (d, _) => ((FlockStreamingText)d).RebuildFont()));
    public static readonly DependencyProperty LineHeightProperty = DependencyProperty.Register(nameof(LineHeight), typeof(double), typeof(FlockStreamingText), new PropertyMetadata(0d, (d, _) => ((FlockStreamingText)d).RebuildFont()));
    public static readonly DependencyProperty FontFamilyNameProperty = DependencyProperty.Register(nameof(FontFamilyName), typeof(string), typeof(FlockStreamingText), new PropertyMetadata("", (d, _) => ((FlockStreamingText)d).RebuildFont()));
    public static readonly DependencyProperty ForegroundProperty = DependencyProperty.Register(nameof(Foreground), typeof(Brush), typeof(FlockStreamingText), new PropertyMetadata(null, (d, _) => ((FlockStreamingText)d).ResolveColors()));
    public static readonly DependencyProperty BirdCountProperty = DependencyProperty.Register(nameof(BirdCount), typeof(int), typeof(FlockStreamingText), new PropertyMetadata(520));
    public static readonly DependencyProperty ReduceMotionProperty = DependencyProperty.Register(nameof(ReduceMotion), typeof(bool), typeof(FlockStreamingText), new PropertyMetadata(false));

    public double FontSize { get => (double)GetValue(FontSizeProperty); set => SetValue(FontSizeProperty, value); }
    /// <summary>Line height in pixels. 0 uses 1.45 times the font size.</summary>
    public double LineHeight { get => (double)GetValue(LineHeightProperty); set => SetValue(LineHeightProperty, value); }
    /// <summary>A font family Skia can resolve by name. Empty uses the platform default.</summary>
    public string FontFamilyName { get => (string)GetValue(FontFamilyNameProperty); set => SetValue(FontFamilyNameProperty, value); }
    public Brush? Foreground { get => (Brush?)GetValue(ForegroundProperty); set => SetValue(ForegroundProperty, value); }
    public int BirdCount { get => (int)GetValue(BirdCountProperty); set => SetValue(BirdCountProperty, value); }
    /// <summary>Words fade in with no birds.</summary>
    public bool ReduceMotion { get => (bool)GetValue(ReduceMotionProperty); set => SetValue(ReduceMotionProperty, value); }

    /// <summary>Where the flock goes when the stream completes, for example the composer. Null lets it thin out in place.</summary>
    public UIElement? ReturnTo { get; set; }
    public string Text => _text.ToString();
    /// <summary>Raised after <see cref="Complete"/> once the last word is readable and the flock has left.</summary>
    public event EventHandler? Completed;

    sealed class Word
    {
        public string Text = ""; public float X, Baseline, W, Alpha, T0 = -1; public bool Break, Birds, Released; public short Grp;
    }

    readonly object _gate = new();
    readonly List<Word> _words = new();
    readonly StringBuilder _text = new(), _pending = new();
    readonly Queue<string> _queued = new();
    readonly Random _rng = new();
    SKFont _font = new(SKTypeface.Default, 16), _drawFont = new(SKTypeface.Default, 16);
    readonly SKPaint _paint = new() { IsAntialias = true };
    SKColor _inkSk;
    float _lineH = 23, _space = 4, _asc = 12, _desc = 4, _cx, _y, _height, _w, _t, _doneAt = -1, _fadeFrom = -1;
    bool _pendingBreak, _completing, _active;
    FlockHost? _host; Flock? _flock; Point _o;

    Flock? IFlockClient.Flock => _flock;
    bool IFlockClient.IsFlockActive => _active;
    bool CanFlock => !ReduceMotion && _host is { IsSupported: true };

    public FlockStreamingText()
    {
        RebuildFont();
        Loaded += (_, _) => { _host = FlockHost.Find(this); _host?.Attach(this); ResolveColors(); };
        Unloaded += (_, _) => { _host?.Detach(this); _flock?.Dispose(); _flock = null; _host = null; _active = false; };
        SizeChanged += (_, e) =>
        {
            if (Math.Abs(e.NewSize.Width - _w) < 0.5) return;
            _w = (float)e.NewSize.Width; Relayout();
            while (_queued.Count > 0) Append(_queued.Dequeue());
        };
        ActualThemeChanged += (_, _) => ResolveColors();
    }

    void ResolveColors()
    {
        _inkSk = FlockTheme.InkOf(this, Foreground);
        if (_flock != null && _host != null) lock (_host.Gate) if (_flock.Palette.Count > 0) _flock.Palette[0] = _inkSk;
        Invalidate();
    }

    void RebuildFont()
    {
        var face = string.IsNullOrEmpty(FontFamilyName) ? SKTypeface.Default : SKTypeface.FromFamilyName(FontFamilyName) ?? SKTypeface.Default;
        float size = (float)Math.Max(6, FontSize);
        var font = new SKFont(face, size) { Subpixel = true };
        var draw = new SKFont(face, size) { Subpixel = true };
        lock (_gate) { _font = font; _drawFont = draw; }
        var m = font.Metrics; _asc = -m.Ascent; _desc = m.Descent;
        _lineH = LineHeight > 0 ? (float)LineHeight : size * 1.45f; _space = font.MeasureText(" ");
        Relayout();
    }

    protected override Size MeasureOverride(Size availableSize)
        => new(double.IsInfinity(availableSize.Width) ? 320 : availableSize.Width, Math.Max(_height, _lineH));

    // Greedy wrap. A word never moves once placed, because wrapping only depends on what came before it.
    void Place(Word w)
    {
        if (w.Break && _words.Count > 0 && !ReferenceEquals(_words[0], w)) { _cx = 0; _y += _lineH * 1.5f; }
        else if (_cx > 0 && _cx + w.W > _w) { _cx = 0; _y += _lineH; }
        w.X = _cx; w.Baseline = _y + (_lineH - (_asc + _desc)) / 2 + _asc; _cx += w.W + _space;
        _height = _y + _lineH;
    }

    void Relayout()
    {
        lock (_gate)
        {
            _cx = 0; _y = 0; _height = 0;
            foreach (var w in _words) { w.W = _font.MeasureText(w.Text); Place(w); }
        }
        InvalidateMeasure(); Invalidate();
    }

    /// <summary>Adds streamed text. Tokens may split words anywhere; a word lands when the whitespace after it arrives.</summary>
    public void Append(string token)
    {
        if (string.IsNullOrEmpty(token)) return;
        if (_w < 1) { _queued.Enqueue(token); return; }
        _text.Append(token);
        foreach (char ch in token)
        {
            if (!char.IsWhiteSpace(ch)) { _pending.Append(ch); continue; }
            FlushWord();
            if (ch == '\n') _pendingBreak = true;
        }
    }

    /// <summary>Ends the stream: the last word lands and the flock leaves.</summary>
    public void Complete()
    {
        FlushWord();
        AutomationProperties.SetName(this, Text);
        _completing = true; _doneAt = -1;
        if (_host != null) { _active = true; _host.Wake(); }
        else Completed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Removes all text. A live flock stays, ready for the next stream.</summary>
    public void Clear()
    {
        lock (_gate) { _words.Clear(); _cx = 0; _y = 0; _height = 0; }
        _text.Clear(); _pending.Clear(); _queued.Clear(); _pendingBreak = false; _completing = false; _doneAt = -1; _fadeFrom = -1;
        InvalidateMeasure(); Invalidate();
    }

    /// <summary>
    /// Starts the write-head flock before the first token. With a <paramref name="source"/> element (the composer),
    /// its visible marks lift off as the birds; clear that element as soon as this returns.
    /// </summary>
    public async Task BeginAsync(UIElement? source = null)
    {
        if (!CanFlock) return;
        var host = _host!;
        await FlockHost.EnsureLaidOutAsync(this);
        if (_w < 1) _w = (float)ActualWidth;
        var f = new Flock(Math.Max(60, BirdCount));
        f.ColorIndex(_inkSk);
        var raw = source != null ? await Ink.CaptureAsync(source, host.Surface, f) : null;
        lock (host.Gate)
        {
            _flock?.Dispose();
            _o = host.OriginOf(this);
            SetWalls(f);
            if (raw is { IsEmpty: false })
            {
                float x0 = raw.X0, span = Math.Max(1, raw.W);
                f.SetWalls(MathF.Min(f.Left, raw.X0), MathF.Min(f.Top, raw.Y0), MathF.Max(f.Right, raw.X0 + raw.W), MathF.Max(f.Bottom, raw.Y0 + raw.H));
                f.SpawnFromInk(raw.Sample(f.N, _rng), (px, _) => (px - x0) / span * 0.45f + (float)_rng.NextDouble() * 0.1f);
            }
            else f.SpawnFromSky(f.Left, f.Right, (float)_o.Y - 8);
            Configure(f); f.Snap();
            _flock = f; _fadeFrom = -1; _active = true;
        }
        host.Wake();
    }

    /// <summary>Takes over a flock from <see cref="FlockThinkingIndicator.HandOffTo"/>.</summary>
    internal void Adopt(Flock f)
    {
        if (!CanFlock) { f.Dispose(); return; }
        lock (_host!.Gate)
        {
            _flock?.Dispose();
            _o = _host.OriginOf(this);
            if (f.Palette.Count == 0) f.ColorIndex(_inkSk); else f.Palette[0] = _inkSk;
            SetWalls(f); Configure(f);
            f.Fade = 1; _flock = f; _fadeFrom = -1; _active = true;
        }
        _host.Wake();
    }

    void SetWalls(Flock f)
    {
        float x = (float)_o.X, y = (float)_o.Y;
        f.SetWalls(x, y - 24, x + Math.Max(_w, 120), y + _height + _lineH + 90);
    }

    // A small, airy flock that hovers just past the last word.
    void Configure(Flock f)
    {
        f.Perch = false; f.Pull = 4; f.SepK = 1.5f; f.HRamp = 0.14f; f.HsDelay = 0.1f; f.HsDur = 0.25f; f.ArrK = 6; f.ArrMax = 560;
        f.VMin = 110; f.VMax = 300; f.Hop = null;
        Hover(f, _words.Count > 0 ? _words[^1] : null);
    }

    void Hover(Flock f, Word? w)
    {
        float x = (float)_o.X, y = (float)_o.Y, margin = MathF.Min(60, Math.Max(_w, 120) / 2);
        float cx = w == null ? x + margin : Math.Clamp(x + w.X + w.W + 30, x + margin, x + Math.Max(_w, 120) - margin);
        float cy = w == null ? y + _lineH + 28 : y + w.Baseline + _desc + 34;
        f.Aim(new Attractor(cx, cy, 58, 22, 3.2f));
    }

    void FlushWord()
    {
        if (_pending.Length == 0) return;
        var w = new Word { Text = _pending.ToString(), Break = _pendingBreak };
        _pending.Clear(); _pendingBreak = false;
        float before = _height;
        lock (_gate) { w.W = _font.MeasureText(w.Text); w.Grp = (short)(_words.Count % 30000); _words.Add(w); Place(w); }
        if (Math.Abs(_height - before) > 0.5f) InvalidateMeasure();

        if (_host == null) { w.Alpha = 1; Invalidate(); return; }   // no host: plain text, no animation clock
        w.T0 = _t;
        if (CanFlock)
        {
            lock (_host.Gate)
            {
                if (_flock == null)
                {
                    _o = _host.OriginOf(this);
                    var nf = new Flock(Math.Max(60, BirdCount)); nf.ColorIndex(_inkSk);
                    SetWalls(nf); nf.SpawnFromSky(nf.Left, nf.Right, (float)_o.Y - 8); Configure(nf); nf.Snap();
                    _flock = nf; _fadeFrom = -1;
                }
                var f = _flock;
                var raw = Ink.FromText(w.Text, _font, (float)_o.X + w.X, (float)_o.Y + w.Baseline, w.W, 0);
                int n = Math.Clamp((int)MathF.Round(raw.Sum * 0.16f), 8, 70);
                var pts = raw.Sample(n, _rng); pts.LineH = _lineH;
                w.Birds = f.Land(pts, w.Grp, 0, 0.1f, 0.05f) > 0;
                f.VMax = 240; Hover(f, w);
            }
        }
        _active = true; _host.Wake();
    }

    void IFlockClient.Reposition() => Reposition();
    void Reposition()
    {
        var f = _flock; if (f == null || _host == null) return;
        var o = _host.OriginOf(this);
        if (Math.Abs(o.X - _o.X) > 0.5 || Math.Abs(o.Y - _o.Y) > 0.5) { f.Translate((float)(o.X - _o.X), (float)(o.Y - _o.Y)); _o = o; }
    }

    void IFlockClient.FlockTick(float dt)
    {
        _t += dt;
        var f = _flock; var host = _host!;
        if (f != null) { Reposition(); if (_fadeFrom < 0) SetWalls(f); }

        bool settling = false;
        foreach (var w in _words)
        {
            if (w.T0 < 0 || w.Released) continue;
            // With birds the word waits for them to land, then fades in under them. Without, it simply fades in.
            w.Alpha = w.Birds ? Math.Clamp((_t - (w.T0 + 0.4f)) / 0.24f, 0, 1) : Math.Clamp((_t - w.T0) / 0.3f, 0, 1);
            if (w.Alpha >= 1) { w.Released = true; if (w.Birds) f?.Release(w.Grp, recycle: true); }
            else settling = true;
        }

        if (_completing && !settling)
        {
            if (_doneAt < 0) _doneAt = _t + 0.3f;
            if (_t >= _doneAt)
            {
                if (f == null) Finish();
                else
                {
                    if (_fadeFrom < 0)
                    {
                        _fadeFrom = _t + 0.35f;
                        if (ReturnTo is { } r)
                        {
                            var p = host.OriginOf(r); var s = r.ActualSize;
                            float cx = (float)p.X + s.X / 2, cy = (float)p.Y + s.Y / 2;
                            f.SetWalls(MathF.Min(f.Left, (float)p.X), MathF.Min(f.Top, (float)p.Y), MathF.Max(f.Right, (float)p.X + s.X), MathF.Max(f.Bottom, (float)p.Y + s.Y));
                            f.Aim(new Attractor(cx, cy, MathF.Min(40, s.X * 0.2f), 4, 3.2f));
                        }
                    }
                    f.Fade = 1 - Math.Clamp((_t - _fadeFrom) / 0.5f, 0, 1);
                    if (f.Fade <= 0) { f.Dispose(); _flock = null; Finish(); }
                }
            }
        }
        else if (f == null && !settling) _active = false;
        Invalidate();
    }

    void Finish()
    {
        _completing = false; _active = false; _doneAt = -1; _fadeFrom = -1;
        Completed?.Invoke(this, EventArgs.Empty);
    }

    protected override void RenderOverride(SKCanvas canvas, Size area)
    {
        lock (_gate)
        {
            foreach (var w in _words)
            {
                if (w.Alpha <= 0.01f) continue;
                _paint.Color = _inkSk.WithAlpha((byte)(_inkSk.Alpha * w.Alpha));
                canvas.DrawText(w.Text, w.X, w.Baseline, SKTextAlign.Left, _drawFont, _paint);
            }
        }
    }
}
