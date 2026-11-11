using System.Diagnostics;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using SkiaSharp;
using Uno.WinUI.Graphics2DSK;
using Windows.Foundation;

namespace Murmuration;

internal interface IFlockClient
{
    Flock? Flock { get; }
    bool IsFlockActive { get; }
    void FlockTick(float dt);
    /// <summary>Re-reads the control's position in the host and moves its flock with it.</summary>
    void Reposition();
}

/// <summary>
/// A Grid with one Skia overlay on top. Every flock control inside it draws its birds into that overlay,
/// so birds can travel between controls. Place it around the region the flock may cross.
/// </summary>
public sealed class FlockHost : Grid
{
    internal readonly object Gate = new();
    readonly List<IFlockClient> _clients = new();
    readonly FlockOverlay? _overlay;
    readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(16) };
    long _last;

    /// <summary>False where Skia rendering is unavailable. Controls then fall back to plain fades and bars.</summary>
    public bool IsSupported => _overlay != null;

    /// <summary>1 is real time. Use 0.25 to study the motion.</summary>
    public double TimeScale { get; set; } = 1;

    public FlockHost()
    {
        if (SKCanvasElement.IsSupportedOnCurrentPlatform())
        {
            _overlay = new FlockOverlay(this) { IsHitTestVisible = false };
            SetRowSpan(_overlay, 1000); SetColumnSpan(_overlay, 1000); Canvas.SetZIndex(_overlay, 10000);
            Children.Add(_overlay);
        }
        _timer.Tick += OnTick;
        Unloaded += (_, _) => _timer.Stop();
        // Fires for any layout change below the host, including a tab or panel being shown or collapsed.
        LayoutUpdated += (_, _) => SyncLayout();
    }

    public static FlockHost? Find(DependencyObject? d)
    {
        while (d != null) { if (d is FlockHost h) return h; d = VisualTreeHelper.GetParent(d); }
        return null;
    }

    internal void Attach(IFlockClient c) { lock (Gate) if (!_clients.Contains(c)) _clients.Add(c); }
    internal void Detach(IFlockClient c) { lock (Gate) _clients.Remove(c); _overlay?.Invalidate(); }

    // A control inside a collapsed ancestor keeps its flock but stops drawing and stepping it.
    bool IsShown(IFlockClient c)
    {
        if (c is not FrameworkElement fe || fe.ActualWidth < 1) return false;
        for (DependencyObject? d = fe; d != null && !ReferenceEquals(d, this); d = VisualTreeHelper.GetParent(d))
            if (d is UIElement { Visibility: not Visibility.Visible }) return false;
        return true;
    }

    void SyncLayout()
    {
        bool changed = false, wake = false;
        lock (Gate)
        {
            foreach (var c in _clients)
            {
                bool shown = IsShown(c);
                if (c.Flock is { } f && f.Hidden == shown) { f.Hidden = !shown; changed = true; }
                if (shown) { c.Reposition(); if (c.IsFlockActive) wake = true; }
            }
        }
        if (changed) _overlay?.Invalidate();
        if (wake) Wake();
    }

    internal static async Task EnsureLaidOutAsync(FrameworkElement e)
    {
        for (int k = 0; k < 20 && e.ActualWidth < 1; k++) await Task.Delay(16);
    }

    /// <summary>Starts the frame timer. It stops by itself when no control is animating.</summary>
    internal void Wake()
    {
        if (_timer.IsEnabled) return;
        _last = Stopwatch.GetTimestamp(); _timer.Start();
    }

    /// <summary>The element whose coordinate space birds are drawn in: the overlay, which sits inside the host's padding.</summary>
    internal UIElement Surface => (UIElement?)_overlay ?? this;

    internal Point OriginOf(UIElement e) => e.TransformToVisual(Surface).TransformPoint(new Point(0, 0));

    void OnTick(object? sender, object e)
    {
        long now = Stopwatch.GetTimestamp();
        float dt = Math.Min(0.033f, (float)((now - _last) / (double)Stopwatch.Frequency)) * (float)TimeScale;
        _last = now; bool any = false;
        lock (Gate)
        {
            foreach (var c in _clients.ToArray())
            {
                if (!c.IsFlockActive || !IsShown(c)) continue;
                any = true; c.FlockTick(dt); c.Flock?.Step(dt);
            }
        }
        _overlay?.Invalidate();
        if (!any) _timer.Stop();
    }

    sealed class FlockOverlay : SKCanvasElement
    {
        readonly FlockHost _host;
        public FlockOverlay(FlockHost host) => _host = host;

        protected override void RenderOverride(SKCanvas canvas, Size area)
        {
            lock (_host.Gate) foreach (var c in _host._clients) c.Flock?.Draw(canvas);
        }
    }
}
