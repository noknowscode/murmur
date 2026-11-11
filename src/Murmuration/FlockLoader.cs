using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.Foundation;

namespace Murmuration;

/// <summary>
/// A content host with a flock loading state. When <see cref="IsLoading"/> turns on, the current content lifts off
/// as birds and wheels. When it turns off, the birds land row by row as the new content, which then fades in.
/// </summary>
public sealed class FlockLoader : ContentControl, IFlockClient
{
    public static readonly DependencyProperty IsLoadingProperty = DependencyProperty.Register(nameof(IsLoading), typeof(bool), typeof(FlockLoader), new PropertyMetadata(false, (d, _) => ((FlockLoader)d).OnIsLoadingChanged()));
    public static readonly DependencyProperty BirdCountProperty = DependencyProperty.Register(nameof(BirdCount), typeof(int), typeof(FlockLoader), new PropertyMetadata(1600));
    public static readonly DependencyProperty ReduceMotionProperty = DependencyProperty.Register(nameof(ReduceMotion), typeof(bool), typeof(FlockLoader), new PropertyMetadata(false));

    public bool IsLoading { get => (bool)GetValue(IsLoadingProperty); set => SetValue(IsLoadingProperty, value); }
    public int BirdCount { get => (int)GetValue(BirdCountProperty); set => SetValue(BirdCountProperty, value); }
    /// <summary>Content crossfades with no birds.</summary>
    public bool ReduceMotion { get => (bool)GetValue(ReduceMotionProperty); set => SetValue(ReduceMotionProperty, value); }
    /// <summary>Raised when the new content is fully visible.</summary>
    public event EventHandler? Revealed;

    enum Phase { Idle, Lifting, Wheeling, Landing, Reveal, PlainOut, PlainIn }

    FlockHost? _host; Flock? _flock; bool _active; Phase _phase; Point _o;
    float _t, _phaseT, _beginT, _revealAt; int _seq, _nextChunk;
    readonly List<(float at, InkPoints pts)> _chunks = new();
    readonly Random _rng = new();

    Flock? IFlockClient.Flock => _flock;
    bool IFlockClient.IsFlockActive => _active;

    UIElement? Presenter => VisualTreeHelper.GetChildrenCount(this) > 0 ? VisualTreeHelper.GetChild(this, 0) as UIElement : null;
    // Capture the content itself and hide its parent, so the capture never includes the hidden state.
    UIElement? CaptureTarget => Content as UIElement ?? Presenter;

    public FlockLoader()
    {
        HorizontalContentAlignment = HorizontalAlignment.Stretch; VerticalContentAlignment = VerticalAlignment.Stretch;
        Loaded += (_, _) => { _host = FlockHost.Find(this); _host?.Attach(this); if (IsLoading) OnIsLoadingChanged(); };
        Unloaded += (_, _) => { _host?.Detach(this); _flock?.Dispose(); _flock = null; _host = null; _active = false; };
    }

    void SetContentOpacity(double o) { if (Presenter is { } p) p.Opacity = o; }
    void Enter(Phase p) { _phase = p; _phaseT = _t; _active = true; _host?.Wake(); }

    void OnIsLoadingChanged()
    {
        if (_host == null) { SetContentOpacity(IsLoading ? 0 : 1); if (!IsLoading) Revealed?.Invoke(this, EventArgs.Empty); return; }
        if (IsLoading) _ = BeginAsync(); else _ = EndAsync();
    }

    async Task BeginAsync()
    {
        int seq = ++_seq; var host = _host!;
        if (ReduceMotion || !host.IsSupported) { DropFlock(); Enter(Phase.PlainOut); return; }
        await FlockHost.EnsureLaidOutAsync(this);
        if (seq != _seq) return;
        var f = new Flock(Math.Max(60, BirdCount));
        f.ColorIndex(FlockTheme.InkOf(this, Foreground));
        bool visible = Presenter is { Opacity: > 0.5 } && CaptureTarget != null;
        var raw = visible ? await Ink.CaptureAsync(CaptureTarget!, host.Surface, f) : new InkRaw();
        if (seq != _seq) { f.Dispose(); return; }
        lock (host.Gate)
        {
            _flock?.Dispose();
            _o = host.OriginOf(this);
            float x = (float)_o.X, y = (float)_o.Y, w = (float)ActualWidth, h = (float)ActualHeight;
            f.SetWalls(x, y, x + w, y + h);
            if (!raw.IsEmpty)
            {
                // The stale content lifts off in a wave from the top-right corner.
                float ox = x + w, oy = y, diag = MathF.Max(1, MathF.Sqrt(w * w + h * h));
                f.SpawnFromInk(raw.Sample(f.N, _rng), (px, py) => MathF.Sqrt((px - ox) * (px - ox) + (py - oy) * (py - oy)) / diag * 0.5f + (float)_rng.NextDouble() * 0.06f);
            }
            else f.SpawnFromSky(x, x + w, y - 8);
            f.Aim(new Attractor(x + w / 2, y + h * 0.45f, w * 0.3f, h * 0.28f, 2.8f)); f.Snap();
            _flock = f; _chunks.Clear(); _nextChunk = 0; _beginT = _t;
        }
        Enter(Phase.Lifting);
    }

    async Task EndAsync()
    {
        int seq = ++_seq; var host = _host!;
        if (_flock == null) { Enter(Phase.PlainIn); return; }
        await Task.Delay(60);   // let the new content lay out before it is captured
        if (seq != _seq || _flock == null) return;
        var f = _flock;
        var raw = CaptureTarget != null ? await Ink.CaptureAsync(CaptureTarget, host.Surface, f) : new InkRaw();
        if (seq != _seq || !ReferenceEquals(f, _flock)) return;
        if (raw.IsEmpty) { DropFlock(); Enter(Phase.PlainIn); return; }
        lock (host.Gate)
        {
            // Rows land top to bottom, each taking a share of the flock in proportion to its ink.
            var rows = raw.SplitRows();
            int birds = f.CountFlying(), used = 0; float start = MathF.Max(_t, _beginT + 0.9f);
            _chunks.Clear(); _nextChunk = 0;
            for (int k = 0; k < rows.Count; k++)
            {
                int n = k == rows.Count - 1 ? birds - used : (int)MathF.Round(birds * rows[k].Sum / raw.Sum);
                n = Math.Max(0, Math.Min(n, birds - used)); used += n;
                _chunks.Add((start + k * 0.3f, rows[k].Sample(n, _rng)));
            }
            _revealAt = start + (rows.Count - 1) * 0.3f + 1.25f;
        }
        Enter(Phase.Landing);
    }

    void DropFlock()
    {
        if (_flock == null || _host == null) return;
        lock (_host.Gate) { _flock.Dispose(); _flock = null; }
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
        _t += dt; float u = _t - _phaseT; var f = _flock;
        Reposition();
        switch (_phase)
        {
            case Phase.PlainOut:
                SetContentOpacity(1 - Math.Clamp(u / 0.18f, 0, 1)); if (u >= 0.18f) _active = false; break;
            case Phase.PlainIn:
                SetContentOpacity(Math.Clamp(u / 0.3f, 0, 1));
                if (u >= 0.3f) { _phase = Phase.Idle; _active = false; Revealed?.Invoke(this, EventArgs.Empty); }
                break;
            case Phase.Lifting:
                SetContentOpacity(1 - Math.Clamp(u / 0.18f, 0, 1)); if (u >= 0.3f) _phase = Phase.Wheeling; break;
            case Phase.Landing when f != null:
                while (_nextChunk < _chunks.Count && _t >= _chunks[_nextChunk].at)
                {
                    f.Land(_chunks[_nextChunk].pts, (short)_nextChunk, 0.25f, 0.3f, 0.08f); _nextChunk++;
                    float left = MathF.Sqrt(1 - (float)_nextChunk / _chunks.Count), x = (float)_o.X, y = (float)_o.Y, w = (float)ActualWidth, h = (float)ActualHeight;
                    f.Aim(new Attractor(x + w / 2, y + h * 0.45f, w * 0.3f * left + 10, h * 0.28f * left + 14, 2.8f));
                }
                if (_t >= _revealAt) Enter(Phase.Reveal);
                break;
            case Phase.Reveal when f != null:
                // The real content fades in under the perched birds as they fade out.
                SetContentOpacity(Math.Clamp(u / 0.35f, 0, 1));
                f.Fade = 1 - Math.Clamp((u - 0.1f) / 0.25f, 0, 1);
                if (u >= 0.35f) { f.Dispose(); _flock = null; _phase = Phase.Idle; _active = false; Revealed?.Invoke(this, EventArgs.Empty); }
                break;
        }
    }
}
