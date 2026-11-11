using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using SkiaSharp;
using Windows.Foundation;

namespace Murmuration;

public enum FlockThinkingState
{
    /// <summary>No flock.</summary>
    Idle,
    /// <summary>A tight, slow orbit.</summary>
    Reading,
    /// <summary>Two lobes, one per parallel call.</summary>
    Tools,
    /// <summary>A wide, faster sweep, entered with a startle.</summary>
    Synthesizing
}

/// <summary>
/// A thinking indicator. The flock wheels inside the control's bounds and its shape follows <see cref="State"/>.
/// Start it from a piece of UI (the typed prompt) and those marks become the birds.
/// </summary>
public sealed class FlockThinkingIndicator : FrameworkElement, IFlockClient
{
    public static readonly DependencyProperty StateProperty = DependencyProperty.Register(nameof(State), typeof(FlockThinkingState), typeof(FlockThinkingIndicator), new PropertyMetadata(FlockThinkingState.Idle, (d, _) => ((FlockThinkingIndicator)d).OnStateChanged()));
    public static readonly DependencyProperty BirdCountProperty = DependencyProperty.Register(nameof(BirdCount), typeof(int), typeof(FlockThinkingIndicator), new PropertyMetadata(900));
    public static readonly DependencyProperty ForegroundProperty = DependencyProperty.Register(nameof(Foreground), typeof(Brush), typeof(FlockThinkingIndicator), new PropertyMetadata(null));

    public FlockThinkingState State { get => (FlockThinkingState)GetValue(StateProperty); set => SetValue(StateProperty, value); }
    public int BirdCount { get => (int)GetValue(BirdCountProperty); set => SetValue(BirdCountProperty, value); }
    public Brush? Foreground { get => (Brush?)GetValue(ForegroundProperty); set => SetValue(ForegroundProperty, value); }

    FlockHost? _host; Flock? _flock; bool _active; float _fadeFrom = -1; Point _o;
    FlockThinkingState _shape = FlockThinkingState.Idle;

    Flock? IFlockClient.Flock => _flock;
    bool IFlockClient.IsFlockActive => _active;

    /// <summary>True when the platform can draw the flock. When false, show your own fallback indicator.</summary>
    public bool IsFlockAvailable => _host is { IsSupported: true };

    public FlockThinkingIndicator()
    {
        Loaded += (_, _) => { _host = FlockHost.Find(this); _host?.Attach(this); if (State != FlockThinkingState.Idle) _ = StartAsync(); };
        Unloaded += (_, _) => { _host?.Detach(this); _flock?.Dispose(); _flock = null; _host = null; _active = false; };
    }

    protected override Size MeasureOverride(Size availableSize)
        => new(double.IsInfinity(availableSize.Width) ? 240 : availableSize.Width, double.IsInfinity(availableSize.Height) ? 120 : availableSize.Height);

    /// <summary>
    /// Starts the flock. With a <paramref name="source"/> element, its visible marks lift off as the birds;
    /// clear or hide that element as soon as this returns. Without one, the birds drift in from the top edge.
    /// </summary>
    public async Task StartAsync(UIElement? source = null)
    {
        if (_host is not { IsSupported: true } host) return;
        await FlockHost.EnsureLaidOutAsync(this);
        var f = new Flock(Math.Max(60, BirdCount)) { SepK = 1.6f, Pull = 9 };
        f.ColorIndex(FlockTheme.InkOf(this, Foreground));
        var raw = source != null ? await Ink.CaptureAsync(source, host.Surface, f) : null;
        lock (host.Gate)
        {
            _flock?.Dispose();
            _o = host.OriginOf(this);
            float x = (float)_o.X, y = (float)_o.Y, w = (float)ActualWidth, h = (float)ActualHeight;
            if (raw is { IsEmpty: false })
            {
                // Unzip the source left to right. Walls reach down to it so the birds can rise into the indicator.
                float x0 = raw.X0, span = Math.Max(1, raw.W);
                var rng = new Random();
                f.SetWalls(MathF.Min(x, raw.X0), MathF.Min(y, raw.Y0), MathF.Max(x + w, raw.X0 + raw.W), MathF.Max(y + h, raw.Y0 + raw.H));
                f.SpawnFromInk(raw.Sample(f.N, rng), (px, _) => (px - x0) / span * 0.45f + (float)rng.NextDouble() * 0.1f);
            }
            else
            {
                f.SetWalls(x, y, x + w, y + h);
                f.SpawnFromSky(x, x + w, y - 8);
            }
            _flock = f; _fadeFrom = -1; _shape = FlockThinkingState.Idle;
            if (State == FlockThinkingState.Idle) State = FlockThinkingState.Reading;
            ApplyShape(snap: true);
            f.Snap();
            _active = true;
        }
        host.Wake();
    }

    /// <summary>Lets the flock thin out and disappear.</summary>
    public void Stop()
    {
        if (_flock == null || _host == null) { State = FlockThinkingState.Idle; return; }
        lock (_host.Gate) _fadeFrom = _flock.T;
        State = FlockThinkingState.Idle;
    }

    /// <summary>
    /// Passes the live flock to a streaming text control, where it becomes the write head.
    /// This is the thinking-to-answer handoff; both controls must share one <see cref="FlockHost"/>.
    /// </summary>
    public void HandOffTo(FlockStreamingText target)
    {
        if (_host == null || _flock == null) { State = FlockThinkingState.Idle; return; }
        Flock f;
        lock (_host.Gate) { f = _flock; _flock = null; _active = false; }
        State = FlockThinkingState.Idle;
        target.Adopt(f);
    }

    void OnStateChanged()
    {
        if (_host == null) return;
        if (_flock == null) { if (State != FlockThinkingState.Idle) _ = StartAsync(); return; }
        lock (_host.Gate) ApplyShape(snap: false);
    }

    void ApplyShape(bool snap)
    {
        var f = _flock; if (f == null || State == FlockThinkingState.Idle || State == _shape) return;
        _shape = State;
        float x = (float)_o.X, y = (float)_o.Y, w = (float)ActualWidth, h = (float)ActualHeight, cy = y + h / 2;
        switch (State)
        {
            case FlockThinkingState.Reading:
                f.Merge(); f.Aim(new Attractor(x + w / 2, cy, w * 0.12f, MathF.Min(24, h * 0.2f), 2.4f)); f.VMin = 110; f.VMax = 300; break;
            case FlockThinkingState.Tools:
                f.Aim(0, new Attractor(x + w * 0.29f, cy, w * 0.1f, MathF.Min(34, h * 0.26f), 3.4f));
                f.Aim(1, new Attractor(x + w * 0.71f, cy, w * 0.1f, MathF.Min(34, h * 0.26f), 3.4f)); f.Split(); f.VMin = 140; f.VMax = 260; break;
            case FlockThinkingState.Synthesizing:
                f.Merge(); f.Aim(new Attractor(x + w / 2, cy, w * 0.3f, MathF.Min(56, h * 0.34f), 3.6f)); f.VMin = 190; f.VMax = 340; if (!snap) f.Kick(); break;
        }
        if (snap) f.Snap();
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
        var f = _flock; if (f == null) { _active = false; return; }
        Reposition();
        if (_fadeFrom >= 0)
        {
            f.Fade = 1 - Math.Clamp((f.T - _fadeFrom) / 0.6f, 0, 1);
            if (f.Fade <= 0) { f.Dispose(); _flock = null; _active = false; }
        }
    }
}
