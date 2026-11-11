using System.Runtime.InteropServices.WindowsRuntime;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media.Imaging;
using SkiaSharp;
using Windows.Foundation;

namespace Murmuration;

/// <summary>A fixed number of points sampled from ink, in host coordinates, with a colour slot each.</summary>
public sealed class InkPoints
{
    public float[] X = Array.Empty<float>(), Y = Array.Empty<float>();
    public byte[] C = Array.Empty<byte>();
    public int Count;
    public float X0, Y0, W = 1, H = 1, LineH = 24;
}

/// <summary>Every inked pixel of a piece of UI, weighted, before it is sampled down to a bird count.</summary>
public sealed class InkRaw
{
    public readonly List<float> Px = new(), Py = new(), Pw = new();
    public readonly List<byte> Pc = new();
    public float Sum, X0, Y0, W = 1, H = 1, LineH = 24;
    public bool IsEmpty => Px.Count == 0;

    public void Add(float x, float y, byte c, float w) { Px.Add(x); Py.Add(y); Pc.Add(c); Pw.Add(w); Sum += w; }

    public InkPoints Sample(int n, Random rng)
    {
        var o = new InkPoints { X0 = X0, Y0 = Y0, W = Math.Max(1, W), H = Math.Max(1, H), LineH = LineH };
        if (n <= 0 || IsEmpty) return o;
        var xs = new List<float>(n + 16); var ys = new List<float>(n + 16); var cs = new List<byte>(n + 16);
        for (int k = 0; k < Px.Count; k++)
        {
            float p = n * Pw[k] / Sum; int c = (int)p + (rng.NextDouble() < p - (int)p ? 1 : 0);
            while (c-- > 0) { xs.Add(Px[k] + (float)rng.NextDouble()); ys.Add(Py[k] + (float)rng.NextDouble()); cs.Add(Pc[k]); }
        }
        while (xs.Count < n) { int k = rng.Next(Px.Count); xs.Add(Px[k] + (float)rng.NextDouble()); ys.Add(Py[k] + (float)rng.NextDouble()); cs.Add(Pc[k]); }
        for (int k = xs.Count - 1; k > 0; k--)
        {
            int j = rng.Next(k + 1);
            (xs[k], xs[j]) = (xs[j], xs[k]); (ys[k], ys[j]) = (ys[j], ys[k]); (cs[k], cs[j]) = (cs[j], cs[k]);
        }
        o.X = xs.GetRange(0, n).ToArray(); o.Y = ys.GetRange(0, n).ToArray(); o.C = cs.GetRange(0, n).ToArray(); o.Count = n;
        return o;
    }

    /// <summary>Splits the ink into rows wherever there is a clear horizontal gap, top to bottom.</summary>
    public List<InkRaw> SplitRows(int maxChunks = 6, float minGap = 8)
    {
        var result = new List<InkRaw>();
        if (IsEmpty) return result;
        int h = (int)MathF.Ceiling(H) + 2; var filled = new bool[h + 1];
        for (int k = 0; k < Py.Count; k++) { int r = (int)(Py[k] - Y0); if (r >= 0 && r <= h) filled[r] = true; }
        var bands = new List<(int a, int b)>(); int start = -1, gap = 0;
        for (int r = 0; r <= h; r++)
        {
            if (filled[r]) { if (start < 0) start = r; gap = 0; }
            else if (start >= 0 && ++gap >= minGap) { bands.Add((start, r - gap)); start = -1; gap = 0; }
        }
        if (start >= 0) bands.Add((start, h));
        while (bands.Count > maxChunks)
        {
            int best = 0; int bestGap = int.MaxValue;
            for (int k = 0; k < bands.Count - 1; k++) { int g = bands[k + 1].a - bands[k].b; if (g < bestGap) { bestGap = g; best = k; } }
            bands[best] = (bands[best].a, bands[best + 1].b); bands.RemoveAt(best + 1);
        }
        foreach (var (a, b) in bands)
        {
            var part = new InkRaw { X0 = X0, Y0 = Y0 + a, W = W, H = Math.Max(1, b - a + 1), LineH = LineH };
            for (int k = 0; k < Py.Count; k++) { float r = Py[k] - Y0; if (r >= a && r <= b + 1) part.Add(Px[k], Py[k], Pc[k], Pw[k]); }
            if (!part.IsEmpty) result.Add(part);
        }
        return result;
    }
}

public static class Ink
{
    /// <summary>Rasterises one run of text with Skia and returns its inked pixels in host coordinates.</summary>
    public static InkRaw FromText(string text, SKFont font, float x, float baseline, float width, byte color)
    {
        var m = font.Metrics; float asc = -m.Ascent, desc = m.Descent;
        int w = Math.Max(1, (int)MathF.Ceiling(width) + 4), h = Math.Max(1, (int)MathF.Ceiling(asc + desc) + 4);
        var raw = new InkRaw { X0 = x, Y0 = baseline - asc, W = Math.Max(1, width), H = asc + desc, LineH = asc + desc };
        using var bmp = new SKBitmap(new SKImageInfo(w, h, SKColorType.Alpha8, SKAlphaType.Premul));
        using (var canvas = new SKCanvas(bmp))
        using (var paint = new SKPaint { IsAntialias = true, Color = SKColors.White })
        {
            canvas.Clear(SKColors.Transparent);
            canvas.DrawText(text, 2, 2 + asc, SKTextAlign.Left, font, paint);
        }
        var px = bmp.GetPixelSpan(); int stride = bmp.RowBytes;
        for (int r = 0; r < h; r++)
            for (int c = 0; c < w; c++)
                if (px[r * stride + c] > 100) raw.Add(x - 2 + c, baseline - asc - 2 + r, color, 1);
        return raw;
    }

    /// <summary>
    /// Renders a live element to pixels and returns its ink in <paramref name="relativeTo"/> coordinates.
    /// A dominant fill that covers most of the element is treated as background and skipped.
    /// </summary>
    public static async Task<InkRaw> CaptureAsync(UIElement element, UIElement relativeTo, Flock flock)
    {
        var raw = new InkRaw();
        try
        {
            var size = element.ActualSize;
            if (size.X < 1 || size.Y < 1) return raw;
            var origin = element.TransformToVisual(relativeTo).TransformPoint(new Point(0, 0));
            var rtb = new RenderTargetBitmap();
            await rtb.RenderAsync(element);
            int pw = rtb.PixelWidth, ph = rtb.PixelHeight;
            if (pw < 1 || ph < 1) return raw;
            var bytes = (await rtb.GetPixelsAsync()).ToArray();
            if (bytes.Length < pw * ph * 4) return raw;
            float scale = pw / size.X;
            raw.X0 = (float)origin.X; raw.Y0 = (float)origin.Y; raw.W = size.X; raw.H = size.Y;

            int step = Math.Max(1, (int)MathF.Round(scale));
            var counts = new Dictionary<int, int>(); int opaque = 0, total = 0;
            for (int y = 0; y < ph; y += step)
                for (int x = 0; x < pw; x += step)
                {
                    total++; int i = (y * pw + x) * 4; if (bytes[i + 3] <= 100) continue;
                    opaque++; int key = (bytes[i + 2] >> 4) << 8 | (bytes[i + 1] >> 4) << 4 | bytes[i] >> 4;
                    counts[key] = counts.TryGetValue(key, out int n) ? n + 1 : 1;
                }
            int skip = -1;
            if (total > 0 && opaque > total * 0.5)
                foreach (var kv in counts) if (kv.Value > opaque * 0.45) skip = kv.Key;

            for (int y = 0; y < ph; y += step)
                for (int x = 0; x < pw; x += step)
                {
                    int i = (y * pw + x) * 4; int a = bytes[i + 3]; if (a <= 100) continue;
                    int key = (bytes[i + 2] >> 4) << 8 | (bytes[i + 1] >> 4) << 4 | bytes[i] >> 4;
                    if (key == skip) continue;
                    // Pixels are premultiplied BGRA.
                    var color = new SKColor((byte)Math.Min(255, bytes[i + 2] * 255 / a), (byte)Math.Min(255, bytes[i + 1] * 255 / a), (byte)Math.Min(255, bytes[i] * 255 / a));
                    raw.Add(raw.X0 + x / scale, raw.Y0 + y / scale, flock.ColorIndex(color), 1);
                }
        }
        catch (Exception)
        {
            // Capture is a flourish. If the platform cannot render the element, callers fall back to a plain fade.
        }
        return raw;
    }

    public static SKColor ToSK(Microsoft.UI.Xaml.Media.Brush? brush, SKColor fallback)
        => brush is Microsoft.UI.Xaml.Media.SolidColorBrush s ? new SKColor(s.Color.R, s.Color.G, s.Color.B, s.Color.A) : fallback;

    public static SKColor Mix(SKColor a, SKColor b)
        => new((byte)((a.Red + b.Red) / 2), (byte)((a.Green + b.Green) / 2), (byte)((a.Blue + b.Blue) / 2));
}
