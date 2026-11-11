using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using SkiaSharp;
using Uno.WinUI.Graphics2DSK;
using Windows.Foundation;

namespace Murmuration;

internal static class FlockTheme
{
    public static SKColor InkOf(FrameworkElement e, Brush? brush)
        => Ink.ToSK(brush, e.ActualTheme == ElementTheme.Dark ? new SKColor(0xE9, 0xE8, 0xF0) : new SKColor(0x17, 0x1A, 0x27));

    public static SKColor AccentOf(Brush? brush)
    {
        if (brush is SolidColorBrush) return Ink.ToSK(brush, default);
        if (Application.Current.Resources.TryGetValue("SystemAccentColor", out var o) && o is Windows.UI.Color c) return new SKColor(c.R, c.G, c.B);
        return new SKColor(0x6B, 0x3F, 0xA0);
    }
}

/// <summary>
/// A determinate progress bar made of perched birds. The flock wheels in the control's own height and
/// birds land on the wire at the bottom, left to right, as <see cref="Value"/> rises.
/// Set <see cref="Value"/> back to 0 and the perched birds lift off again.
/// </summary>
public sealed class FlockProgressBar : SKCanvasElement, IFlockClient
{
    const float Win = 0.6f;   // shown progress = raw progress averaged over this window, so pace changes ease in and out

    public static readonly DependencyProperty ValueProperty = DependencyProperty.Register(nameof(Value), typeof(double), typeof(FlockProgressBar), new PropertyMetadata(0d, (d, _) => ((FlockProgressBar)d).WakeUp()));
    public static readonly DependencyProperty BirdCountProperty = DependencyProperty.Register(nameof(BirdCount), typeof(int), typeof(FlockProgressBar), new PropertyMetadata(640));
    public static readonly DependencyProperty ForegroundProperty = DependencyProperty.Register(nameof(Foreground), typeof(Brush), typeof(FlockProgressBar), new PropertyMetadata(null, (d, _) => ((FlockProgressBar)d).ResolveColors()));
    public static readonly DependencyProperty AccentBrushProperty = DependencyProperty.Register(nameof(AccentBrush), typeof(Brush), typeof(FlockProgressBar), new PropertyMetadata(null, (d, _) => ((FlockProgressBar)d).ResolveColors()));
    public static readonly DependencyProperty ReduceMotionProperty = DependencyProperty.Register(nameof(ReduceMotion), typeof(bool), typeof(FlockProgressBar), new PropertyMetadata(false));

    /// <summary>Progress from 0 to 1.</summary>
    public double Value { get => (double)GetValue(ValueProperty); set => SetValue(ValueProperty, value); }
    public int BirdCount { get => (int)GetValue(BirdCountProperty); set => SetValue(BirdCountProperty, value); }
    public Brush? Foreground { get => (Brush?)GetValue(ForegroundProperty); set => SetValue(ForegroundProperty, value); }
    public Brush? AccentBrush { get => (Brush?)GetValue(AccentBrushProperty); set => SetValue(AccentBrushProperty, value); }
    /// <summary>Draws a plain eased bar with no birds.</summary>
    public bool ReduceMotion { get => (bool)GetValue(ReduceMotionProperty); set => SetValue(ReduceMotionProperty, value); }

    /// <summary>The eased value the bar is showing. Bind labels to this, not to <see cref="Value"/>, so text and birds agree.</summary>
    public double DisplayedValue { get; private set; }
    public event EventHandler<double>? DisplayedValueChanged;
    /// <summary>Raised once the last bird has landed.</summary>
    public event EventHandler? Completed;

    FlockHost? _host; Flock? _flock; bool _active, _plain, _completed;
    readonly List<(float t, float v)> _hist = new();
    float _t, _shown, _rate, _lastRaw, _lastLand, _tR = -1, _w, _h, _idle;
    int _landed; byte _accentIdx; Point _o;
    readonly Random _rng = new();

    // Render-thread snapshot.
    SKColor _inkSk, _accentSk; float _shownSnap; bool _plainSnap;
    readonly SKPaint _paint = new() { IsAntialias = true };

    Flock? IFlockClient.Flock => _flock;
    bool IFlockClient.IsFlockActive => _active;

    public FlockProgressBar()
    {
        Loaded += (_, _) => { _host = FlockHost.Find(this); _host?.Attach(this); ResolveColors(); WakeUp(); };
        Unloaded += (_, _) => { _host?.Detach(this); _flock?.Dispose(); _flock = null; _host = null; _active = false; };
        SizeChanged += (_, e) => { _w = (float)e.NewSize.Width; _h = (float)e.NewSize.Height; WakeUp(); };
        ActualThemeChanged += (_, _) => ResolveColors();
    }

    protected override Size MeasureOverride(Size availableSize)
        => new(double.IsInfinity(availableSize.Width) ? 320 : availableSize.Width, double.IsInfinity(availableSize.Height) ? 180 : availableSize.Height);

    void ResolveColors()
    {
        _inkSk = FlockTheme.InkOf(this, Foreground); _accentSk = FlockTheme.AccentOf(AccentBrush);
        if (_flock != null && _host != null)
            lock (_host.Gate)
            {
                if (_flock.Palette.Count > 0) _flock.Palette[0] = _inkSk;
                if (_flock.Palette.Count > _accentIdx) _flock.Palette[_accentIdx] = _accentSk;
                if (_flock.Palette.Count > _flock.MixColor) _flock.Palette[_flock.MixColor] = Ink.Mix(_inkSk, _accentSk);
            }
        Invalidate();
    }

    void WakeUp()
    {
        if (_host == null || _w < 1) { Invalidate(); return; }
        _active = true; _host.Wake();
    }

    float WireY => (float)_o.Y + _h - 3;

    void CreateFlock()
    {
        _o = _host!.OriginOf(this);
        var f = new Flock(Math.Max(60, BirdCount)) { Perch = true, HRamp = 0.35f, HsDelay = 0.45f, HsDur = 0.5f, ArrK = 5, ArrMax = 380, VMin = 170, VMax = 300, Pull = 4.5f, SepK = 1.8f };
        f.ColorIndex(_inkSk); _accentIdx = f.ColorIndex(_accentSk); f.MixColor = f.ColorIndex(Ink.Mix(_inkSk, _accentSk));
        float x = (float)_o.X, y = (float)_o.Y;
        f.SetWalls(x, y, x + _w, y + _h);
        f.SpawnFromSky(x, x + _w, y - 8);
        for (int i = 0; i < f.N; i++) { f.Mode[i] = Flock.Assigned; f.Grp[i] = 0; f.Tc[i] = _accentIdx; }
        f.Aim(new Attractor(x + _w * 0.3f, y + (_h - 3) * 0.5f, _w * 0.2f, MathF.Min(50, _h * 0.22f), 2.6f)); f.Snap();
        // Completion: one small wave runs the length of the wire.
        f.Hop = (i, t) =>
        {
            if (_tR < 0) return 0;
            float u = (t - _tR - (f.TX[i] - (float)_o.X) / Math.Max(1, _w) * 0.5f) / 0.45f;
            if (u <= 0 || u >= 1) return 0;
            float s = MathF.Sin(MathF.PI * u); return 3.2f * s * s;
        };
        _flock = f; _landed = 0; _completed = false; _tR = -1;
    }

    // The value at an earlier moment, read from recent history. Before any history it is 0, so a first value eases in.
    float RawAt(float time)
    {
        for (int k = _hist.Count - 1; k >= 0; k--) if (_hist[k].t <= time) return _hist[k].v;
        return 0;
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
        float v = (float)Math.Clamp(Value, 0, 1);
        _plain = ReduceMotion || _host == null || !_host.IsSupported;
        // Birds arrive when progress starts. After a reset with nothing to do, they thin out and leave.
        if (!_plain && _flock == null && v > 0) CreateFlock();
        if (_plain && _flock != null) { _flock.Dispose(); _flock = null; }

        if (v < _lastRaw - 0.02f && _shown > 0.05f) Rewind();
        _lastRaw = v;
        _hist.Add((_t, v));
        while (_hist.Count > 2 && _hist[1].t < _t - Win) _hist.RemoveAt(0);

        float sum = 0, wsum = 0;
        for (int k = 0; k <= 16; k++) { float u = k / 16f, hann = 0.5f - 0.5f * MathF.Cos(MathF.Tau * u); sum += hann * RawAt(_t - Win * u); wsum += hann; }
        float shown = sum / wsum; if (shown > 0.9995f) shown = 1; else if (shown < 0.0005f) shown = 0;
        float rate = Math.Clamp((shown - _shown) / Math.Max(dt, 0.001f) / 0.3f, 0, 1);
        _rate += (rate - _rate) * 0.1f;
        if (shown != _shown) { _shown = shown; DisplayedValue = shown; DisplayedValueChanged?.Invoke(this, shown); }

        var f = _flock;
        if (f != null && v <= 0 && _shown <= 0)
        {
            _idle += dt; f.Fade = 1 - Math.Clamp((_idle - 2f) / 0.6f, 0, 1);
            if (f.Fade <= 0) { f.Dispose(); _flock = null; f = null; }
        }
        else if (f != null) { _idle = 0; f.Fade = 1; }
        if (f == null)
        {
            if (Math.Abs(_shown - v) < 0.001f) { if (v >= 0.999f && !_completed) { _completed = true; Completed?.Invoke(this, EventArgs.Empty); } _active = false; }
        }
        else
        {
            Reposition();
            float x0 = (float)_o.X, y0 = (float)_o.Y;
            f.SetWalls(x0, y0, x0 + _w, y0 + _h);

            // The next perch goes to the nearest bird still flying, so birds pour off the underside of the flock.
            int want = (int)MathF.Floor(_shown * f.N + 0.0001f);
            while (_landed < want)
            {
                float px = x0 + (_landed + 0.5f) / f.N * _w, py = WireY - 1.6f - (_landed % 3) * 2.3f;
                int best = -1, any = -1; float bd = float.MaxValue;
                for (int i = 0; i < f.N; i++)
                {
                    if (!float.IsPositiveInfinity(f.Home[i])) continue;
                    any = i; if (!f.Launched[i]) continue;
                    float dx = f.X[i] - px, dy = f.Y[i] - py, d = dx * dx + dy * dy; if (d < bd) { bd = d; best = i; }
                }
                if (best < 0) best = any; if (best < 0) break;
                f.TX[best] = px; f.TY[best] = py - (float)_rng.NextDouble() * 0.6f; f.Home[best] = f.T; _landed++; _lastLand = _t;
            }

            // The flock rides above the leading edge, sinks toward the wire and thins as it empties.
            float q = MathF.Sqrt(Math.Max(0, 1 - _shown)), mid = y0 + (_h - 3) * 0.5f, low = WireY - MathF.Min(58, _h * 0.3f);
            f.Aim(new Attractor(Math.Clamp(x0 + _shown * _w, x0 + _w * 0.3f, x0 + _w * 0.7f), mid + (low - mid) * _shown * _shown,
                _w * 0.2f * q + 8, MathF.Min(44, _h * 0.2f) * q + 10, 2.6f));
            // Energy follows the transfer rate: calmer through a stall, quicker when progress resumes.
            f.VMax += (250 + 110 * _rate - f.VMax) * 0.04f; f.VMin += (140 + 60 * _rate - f.VMin) * 0.04f;

            if (!_completed && _landed >= f.N && _shown >= 0.999f && _t - _lastLand > 0.95f)
            {
                _completed = true; _tR = f.T + 0.3f; Completed?.Invoke(this, EventArgs.Empty);
            }
            if (_completed && f.T > _tR + 1.1f) _active = false;
        }
        _shownSnap = _shown; _plainSnap = _plain;
        Invalidate();
    }

    // Progress went backwards: the perched birds lift off left to right and become the flock again.
    void Rewind()
    {
        _hist.Clear(); _shown = 0; DisplayedValue = 0; DisplayedValueChanged?.Invoke(this, 0);
        _landed = 0; _completed = false; _tR = -1;
        var f = _flock; if (f == null) return;
        for (int i = 0; i < f.N; i++)
        {
            if (float.IsPositiveInfinity(f.Home[i])) continue;
            f.Home[i] = float.PositiveInfinity; f.Launched[i] = false; f.Sc[i] = _accentIdx;
            f.X[i] = f.TX[i]; f.Y[i] = f.TY[i]; f.Z[i] = 0; f.VX[i] = f.VY[i] = f.VZ[i] = 0;
            f.Rel[i] = f.T + (f.TX[i] - (float)_o.X) / Math.Max(1, _w) * 0.55f + (float)_rng.NextDouble() * 0.1f;
        }
        f.InkOnLaunch = true; f.DrawWaiting = true; f.GatherStart = f.T;
    }

    protected override void RenderOverride(SKCanvas canvas, Size area)
    {
        float w = (float)area.Width, h = (float)area.Height;
        _paint.Color = _inkSk; canvas.DrawRect(0, h - 3, w, 2, _paint);
        if (_plainSnap) { _paint.Color = _accentSk; canvas.DrawRect(0, h - 11, w * _shownSnap, 6, _paint); }
    }
}
