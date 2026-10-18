using System.Windows.Media;
using System.Windows.Media.Imaging;
using YorteImpact.Models;

namespace YorteImpact.Services;

/// <summary>
/// Smart background removal for raster elements. Two modes:
///  • magic wand — erase the connected region of the color under the cursor;
///  • auto — erase whatever background is connected to the image border.
/// Matched pixels become fully transparent; everything else is untouched.
/// </summary>
public static class BackgroundRemoverService
{
    private const double MaxDistance = 441.673; // sqrt(3 * 255^2)

    private static double Threshold(double tolerance) => Math.Clamp(tolerance, 0, 100) / 100.0 * MaxDistance;

    /// <summary>Erase the region of the pixel (seedX, seedY). Null when nothing changed.</summary>
    public static BitmapSource? EraseColor(ImageElement el, int seedX, int seedY, double tolerance, bool contiguous)
    {
        if (!TryRead(el.Source, out var px, out int w, out int h, out double dx, out double dy)) return null;
        if (seedX < 0 || seedY < 0 || seedX >= w || seedY >= h) return null;

        int si = seedY * w + seedX;
        if (px[si * 4 + 3] == 0) return null; // already transparent
        var seed = (px[si * 4 + 2], px[si * 4 + 1], px[si * 4]); // r, g, b
        if (!Apply(px, w, h, seed, Threshold(tolerance), contiguous, new[] { si })) return null;
        return Build(px, w, h, dx, dy);
    }

    /// <summary>Auto: pick the dominant border color and erase what is connected to the edges.</summary>
    public static BitmapSource? AutoErase(ImageElement el, double tolerance, bool contiguous)
    {
        if (!TryRead(el.Source, out var px, out int w, out int h, out double dx, out double dy)) return null;
        if (BorderColor(px, w, h) is not { } seed) return null;
        if (!Apply(px, w, h, seed, Threshold(tolerance), contiguous, BorderIndices(w, h))) return null;
        return Build(px, w, h, dx, dy);
    }

    // ---------------- matching ----------------

    private static bool Apply(byte[] px, int w, int h, (byte R, byte G, byte B) seed, double thr,
                              bool contiguous, IReadOnlyList<int> seeds) =>
        contiguous
            ? FloodErase(px, w, h, seed, thr, seeds)
            : GlobalErase(px, w, h, seed, thr);

    private static bool Match(byte[] px, int idx, (byte R, byte G, byte B) c, double thr)
    {
        int i = idx * 4;
        if (px[i + 3] == 0) return false;
        double dr = px[i + 2] - c.R, dg = px[i + 1] - c.G, db = px[i] - c.B;
        return Math.Sqrt(dr * dr + dg * dg + db * db) <= thr;
    }

    private static bool FloodErase(byte[] px, int w, int h, (byte R, byte G, byte B) c, double thr, IReadOnlyList<int> seeds)
    {
        bool changed = false;
        var visited = new bool[w * h];
        var stack = new Stack<int>();

        void TryPush(int idx)
        {
            if (idx < 0 || idx >= w * h || visited[idx]) return;
            visited[idx] = true;
            if (Match(px, idx, c, thr)) stack.Push(idx);
        }

        foreach (var s in seeds) TryPush(s);

        while (stack.Count > 0)
        {
            int idx = stack.Pop();
            px[idx * 4 + 3] = 0;
            changed = true;
            int x = idx % w;
            if (x > 0) TryPush(idx - 1);
            if (x < w - 1) TryPush(idx + 1);
            if (idx >= w) TryPush(idx - w);
            if (idx < w * (h - 1)) TryPush(idx + w);
        }
        return changed;
    }

    private static bool GlobalErase(byte[] px, int w, int h, (byte R, byte G, byte B) c, double thr)
    {
        bool changed = false;
        for (int idx = 0; idx < w * h; idx++)
            if (Match(px, idx, c, thr))
            {
                px[idx * 4 + 3] = 0;
                changed = true;
            }
        return changed;
    }

    // ---------------- helpers ----------------

    private static IReadOnlyList<int> BorderIndices(int w, int h)
    {
        var list = new List<int>();
        for (int x = 0; x < w; x++) { list.Add(x); list.Add((h - 1) * w + x); }
        for (int y = 1; y < h - 1; y++) { list.Add(y * w); list.Add(y * w + w - 1); }
        return list;
    }

    /// <summary>Most common opaque color along the 1px frame (bucketed to 16 levels).</summary>
    private static (byte R, byte G, byte B)? BorderColor(byte[] px, int w, int h)
    {
        var count = new Dictionary<int, int>();
        var sum = new Dictionary<int, (long R, long G, long B)>();

        void Add(int idx)
        {
            int i = idx * 4;
            if (px[i + 3] < 8) return;
            int r = px[i + 2], g = px[i + 1], b = px[i];
            int key = (r >> 4) << 8 | (g >> 4) << 4 | (b >> 4);
            count[key] = count.TryGetValue(key, out var n) ? n + 1 : 1;
            var acc = sum.TryGetValue(key, out var s) ? s : (0L, 0L, 0L);
            sum[key] = (acc.Item1 + r, acc.Item2 + g, acc.Item3 + b);
        }

        foreach (var idx in BorderIndices(w, h)) Add(idx);
        if (count.Count == 0) return null;

        int best = count.OrderByDescending(kv => kv.Value).First().Key;
        var t = sum[best];
        int c = count[best];
        return ((byte)(t.R / c), (byte)(t.G / c), (byte)(t.B / c));
    }

    private static bool TryRead(ImageSource? src, out byte[] px, out int w, out int h, out double dpiX, out double dpiY)
    {
        px = Array.Empty<byte>();
        w = h = 0;
        dpiX = dpiY = 96;
        if (src is not BitmapSource b || b.PixelWidth < 1 || b.PixelHeight < 1) return false;

        var conv = b.Format == PixelFormats.Bgra32 ? b : new FormatConvertedBitmap(b, PixelFormats.Bgra32, null, 0);
        w = conv.PixelWidth;
        h = conv.PixelHeight;
        px = new byte[w * 4 * h];
        conv.CopyPixels(px, w * 4, 0);
        dpiX = conv.DpiX <= 0 ? 96 : conv.DpiX;
        dpiY = conv.DpiY <= 0 ? 96 : conv.DpiY;
        return true;
    }

    private static BitmapSource Build(byte[] px, int w, int h, double dpiX, double dpiY)
    {
        var bmp = BitmapSource.Create(w, h, dpiX, dpiY, PixelFormats.Bgra32, null, px, w * 4);
        bmp.Freeze();
        return bmp;
    }
}
