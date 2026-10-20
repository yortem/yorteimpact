using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using YorteImpact.Models;

namespace YorteImpact.Services;

/// <summary>
/// CPU renderer for non-destructive photo adjustments. The input raster is
/// never modified; every render starts from the original pixels.
/// </summary>
public static class PhotoAdjustmentService
{
    private static BitmapSource? _placeholder;

    public static BitmapSource? Render(BitmapSource? source, PhotoAdjustments adjustments, int maxDimension = 0)
    {
        if (source == null || source.PixelWidth < 1 || source.PixelHeight < 1) return source;

        var bmp = ToBgra32(source, maxDimension);
        if (adjustments.IsDefault) return bmp;

        int w = bmp.PixelWidth;
        int h = bmp.PixelHeight;
        int stride = w * 4;
        var pixels = new byte[stride * h];
        bmp.CopyPixels(pixels, stride, 0);

        // The intensity scales the whole edit: the adjustments are applied in
        // full and the result is mixed back towards the original, so every
        // effect lands at the same fraction of its strength.
        double intensity = Math.Clamp(adjustments.Intensity / 100.0, 0, 1);
        if (intensity <= 0.001) return bmp; // the edit is switched off entirely
        byte[]? original = intensity < 0.999 ? (byte[])pixels.Clone() : null;

        Apply(pixels, w, h, adjustments);
        if (original != null) MixTowards(original, pixels, intensity);

        var result = BitmapSource.Create(
            w, h,
            Math.Max(1, source.DpiX), Math.Max(1, source.DpiY),
            PixelFormats.Bgra32, null, pixels, stride);
        result.Freeze();
        return result;
    }

    public static PhotoAdjustments BuildAuto(BitmapSource? source)
    {
        var result = new PhotoAdjustments();
        if (source == null || source.PixelWidth < 1 || source.PixelHeight < 1) return result;

        var bmp = ToBgra32(source, 256);
        int w = bmp.PixelWidth;
        int h = bmp.PixelHeight;
        int stride = w * 4;
        var px = new byte[stride * h];
        bmp.CopyPixels(px, stride, 0);

        double sum = 0, sumSq = 0;
        int count = 0;
        for (int i = 0; i < px.Length; i += 4)
        {
            if (px[i + 3] < 8) continue;
            double b = px[i] / 255.0;
            double g = px[i + 1] / 255.0;
            double r = px[i + 2] / 255.0;
            double luma = 0.2126 * r + 0.7152 * g + 0.0722 * b;
            sum += luma;
            sumSq += luma * luma;
            count++;
        }

        if (count == 0) return result;
        double mean = sum / count;
        double variance = Math.Max(0, sumSq / count - mean * mean);
        double std = Math.Sqrt(variance);

        result.Exposure = Math.Round(Math.Clamp(Math.Log(0.50 / Math.Max(0.02, mean), 2), -1.5, 1.5) * 20) / 20;
        result.Contrast = Math.Round(Math.Clamp((0.20 - std) * 250, -15, 40));
        result.Shadows = mean < 0.42 ? Math.Round(Math.Clamp((0.42 - mean) * 180, 0, 35)) : 0;
        result.Highlights = mean > 0.64 ? -Math.Round(Math.Clamp((mean - 0.64) * 180, 0, 35)) : 0;
        result.Vibrance = 8;
        return result;
    }

    /// <summary>Normalized RGB histograms (common scale) built from a downscaled copy.</summary>
    public static (float[] R, float[] G, float[] B) Histogram(BitmapSource? source, int bins = 96)
    {
        var r = new float[bins];
        var g = new float[bins];
        var b = new float[bins];
        if (source == null || source.PixelWidth < 1 || source.PixelHeight < 1) return (r, g, b);

        var bmp = ToBgra32(source, 256);
        int w = bmp.PixelWidth;
        int h = bmp.PixelHeight;
        int stride = w * 4;
        var px = new byte[stride * h];
        bmp.CopyPixels(px, stride, 0);

        var counts = new int[bins, 3];
        for (int i = 0; i < px.Length; i += 4)
        {
            if (px[i + 3] < 8) continue;
            counts[Math.Min(bins - 1, px[i + 2] * bins / 256), 0]++;
            counts[Math.Min(bins - 1, px[i + 1] * bins / 256), 1]++;
            counts[Math.Min(bins - 1, px[i] * bins / 256), 2]++;
        }

        int max = 1;
        for (int i = 0; i < bins; i++)
        {
            max = Math.Max(max, counts[i, 0]);
            max = Math.Max(max, counts[i, 1]);
            max = Math.Max(max, counts[i, 2]);
        }

        for (int i = 0; i < bins; i++)
        {
            r[i] = (float)Math.Sqrt(counts[i, 0] / (double)max);
            g[i] = (float)Math.Sqrt(counts[i, 1] / (double)max);
            b[i] = (float)Math.Sqrt(counts[i, 2] / (double)max);
        }
        return (r, g, b);
    }

    public static BitmapSource Placeholder => _placeholder ??= CreatePlaceholder();

    private static BitmapSource ToBgra32(BitmapSource source, int maxDimension)
    {
        BitmapSource current = source;
        int w = current.PixelWidth;
        int h = current.PixelHeight;

        if (maxDimension > 0 && Math.Max(w, h) > maxDimension)
        {
            double scale = (double)maxDimension / Math.Max(w, h);
            current = new TransformedBitmap(current, new ScaleTransform(scale, scale));
        }

        if (current.Format != PixelFormats.Bgra32)
            current = new FormatConvertedBitmap(current, PixelFormats.Bgra32, null, 0);

        if (!current.IsFrozen)
        {
            // Freezing a wrapper also freezes the bitmap it wraps. Never freeze
            // a live WriteableBitmap (the paintbrush writes into it), so
            // materialize a standalone frozen copy instead.
            if (source is WriteableBitmap)
            {
                int cw = current.PixelWidth;
                int ch = current.PixelHeight;
                int stride = cw * 4;
                var pixels = new byte[stride * ch];
                current.CopyPixels(pixels, stride, 0);
                var copy = BitmapSource.Create(cw, ch, current.DpiX, current.DpiY,
                    PixelFormats.Bgra32, null, pixels, stride);
                copy.Freeze();
                return copy;
            }

            current.Freeze();
        }
        return current;
    }

    /// <summary>Mix the adjusted pixels back towards the original (alpha is kept).</summary>
    private static void MixTowards(byte[] original, byte[] adjusted, double amount)
    {
        for (int i = 0; i < adjusted.Length; i += 4)
        {
            adjusted[i] = ToByte(original[i] + (adjusted[i] - original[i]) * amount);
            adjusted[i + 1] = ToByte(original[i + 1] + (adjusted[i + 1] - original[i + 1]) * amount);
            adjusted[i + 2] = ToByte(original[i + 2] + (adjusted[i + 2] - original[i + 2]) * amount);
        }
    }

    private static void Apply(byte[] px, int w, int h, PhotoAdjustments a)
    {
        double exposure = Math.Pow(2, a.Exposure);
        double temp = a.Temperature / 100.0;
        double tint = a.Tint / 100.0;
        double gainR = 1 + temp * 0.18 + tint * 0.08;
        double gainG = 1 - tint * 0.14;
        double gainB = 1 - temp * 0.18 + tint * 0.08;

        var lutR = BuildToneLut(gainR * exposure, a);
        var lutG = BuildToneLut(gainG * exposure, a);
        var lutB = BuildToneLut(gainB * exposure, a);

        double satFactor = 1 + a.Saturation / 100.0;
        double vibrance = a.Vibrance / 100.0;

        for (int i = 0; i < px.Length; i += 4)
        {
            double b = lutB[px[i]];
            double g = lutG[px[i + 1]];
            double r = lutR[px[i + 2]];

            double max = Math.Max(r, Math.Max(g, b));
            double min = Math.Min(r, Math.Min(g, b));
            double sat = max <= 0.001 ? 0 : (max - min) / max;
            double factor = Math.Max(0, satFactor + vibrance * (1 - sat) * 0.75);
            double luma = 0.2126 * r + 0.7152 * g + 0.0722 * b;

            r = luma + (r - luma) * factor;
            g = luma + (g - luma) * factor;
            b = luma + (b - luma) * factor;

            px[i] = ToByte(b);
            px[i + 1] = ToByte(g);
            px[i + 2] = ToByte(r);
        }

        if (a.ToneAmount > 0) ApplyTone(px, a);
        if (a.RecolorAmount > 0) ApplyRecolor(px, a);
        if (a.Clarity != 0) ApplyClarity(px, w, h, a.Clarity);
        if (a.Sharpen > 0) ApplySharpen(px, w, h, a.Sharpen);
        if (a.Glow > 0) ApplyGlow(px, w, h, a.Glow);
        if (a.Blur > 0) ApplyBlur(px, w, h, a.Blur);

        double vignette = a.Vignette / 100.0;
        double grain = a.Grain / 100.0;
        for (int y = 0; y < h; y++)
        {
            double cy = (y + 0.5) / h * 2 - 1;
            int row = y * w * 4;
            for (int x = 0; x < w; x++)
            {
                int i = row + x * 4;
                if (vignette != 0)
                {
                    double cx = (x + 0.5) / w * 2 - 1;
                    double distSq = (cx * cx + cy * cy) * 0.5;
                    double mul = Math.Clamp(1 - vignette * 0.9 * distSq, 0, 2);
                    px[i] = ToByte(px[i] * mul);
                    px[i + 1] = ToByte(px[i + 1] * mul);
                    px[i + 2] = ToByte(px[i + 2] * mul);
                }

                if (grain > 0)
                {
                    uint hash = Hash(x, y);
                    double noise = ((hash & 0xFFFF) / 65535.0 - 0.5) * grain * 42.0;
                    px[i] = ToByte(px[i] + noise);
                    px[i + 1] = ToByte(px[i + 1] + noise);
                    px[i + 2] = ToByte(px[i + 2] + noise);
                }
            }
        }
    }

    private static byte[] BuildToneLut(double gain, PhotoAdjustments a)
    {
        var lut = new byte[256];
        double contrast = 1 + a.Contrast / 100.0 * 0.8;
        for (int i = 0; i < 256; i++)
        {
            double v = i / 255.0 * gain;
            v = Math.Clamp(v, 0, 1);

            double shadowMask = (1 - v) * (1 - v);
            double highlightMask = v * v;

            v += a.Blacks / 100.0 * 0.35 * Math.Pow(1 - v, 3);
            v += a.Shadows / 100.0 * 0.45 * shadowMask;
            v += a.Highlights / 100.0 * 0.45 * highlightMask;
            v += a.Whites / 100.0 * 0.35 * Math.Pow(v, 3);

            v = (v - 0.5) * contrast + 0.5;
            lut[i] = ToByte(v * 255.0);
        }
        return lut;
    }

    private static void ApplyTone(byte[] px, PhotoAdjustments a)
    {
        double amount = a.ToneAmount / 100.0;
        if (amount <= 0) return;

        double tr = a.ToneColor.R / 255.0;
        double tg = a.ToneColor.G / 255.0;
        double tb = a.ToneColor.B / 255.0;
        double darkR = tr * 0.12, darkG = tg * 0.12, darkB = tb * 0.12;
        double lightR = tr + (1 - tr) * 0.65;
        double lightG = tg + (1 - tg) * 0.65;
        double lightB = tb + (1 - tb) * 0.65;

        for (int i = 0; i < px.Length; i += 4)
        {
            double luma = (0.2126 * px[i + 2] + 0.7152 * px[i + 1] + 0.0722 * px[i]) / 255.0;
            double r = (darkR + (lightR - darkR) * luma) * 255.0;
            double g = (darkG + (lightG - darkG) * luma) * 255.0;
            double b = (darkB + (lightB - darkB) * luma) * 255.0;

            px[i] = ToByte(px[i] + (b - px[i]) * amount);
            px[i + 1] = ToByte(px[i + 1] + (g - px[i + 1]) * amount);
            px[i + 2] = ToByte(px[i + 2] + (r - px[i + 2]) * amount);
        }
    }

    private static void ApplyRecolor(byte[] px, PhotoAdjustments a)
    {
        double amount = a.RecolorAmount / 100.0;
        if (amount <= 0) return;

        byte r = a.RecolorColor.R;
        byte g = a.RecolorColor.G;
        byte b = a.RecolorColor.B;
        for (int i = 0; i < px.Length; i += 4)
        {
            px[i] = ToByte(px[i] + (b - px[i]) * amount);
            px[i + 1] = ToByte(px[i + 1] + (g - px[i + 1]) * amount);
            px[i + 2] = ToByte(px[i + 2] + (r - px[i + 2]) * amount);
        }
    }

    private static void ApplySharpen(byte[] px, int w, int h, double sharpen)
    {
        var luma = new float[w * h];
        for (int y = 0; y < h; y++)
        {
            int row = y * w;
            int src = row * 4;
            for (int x = 0; x < w; x++)
            {
                int i = src + x * 4;
                luma[row + x] = (float)(0.2126 * px[i + 2] + 0.7152 * px[i + 1] + 0.0722 * px[i]);
            }
        }

        var blurred = new float[w * h];
        int radius = Math.Max(1, (int)Math.Round(Math.Min(w, h) * 0.0015));
        BoxBlur(luma, blurred, w, h, radius);

        double amount = sharpen / 100.0 * 1.6;
        for (int i = 0; i < luma.Length; i++)
        {
            double delta = (luma[i] - blurred[i]) * amount;
            int p = i * 4;
            px[p] = ToByte(px[p] + delta);
            px[p + 1] = ToByte(px[p + 1] + delta);
            px[p + 2] = ToByte(px[p + 2] + delta);
        }
    }

    private static void ApplyGlow(byte[] px, int w, int h, double glow)
    {
        const double threshold = 153; // start glowing at ~60% luminance
        var bright = new float[w * h];
        for (int y = 0; y < h; y++)
        {
            int row = y * w;
            int src = row * 4;
            for (int x = 0; x < w; x++)
            {
                int i = src + x * 4;
                double luma = 0.2126 * px[i + 2] + 0.7152 * px[i + 1] + 0.0722 * px[i];
                bright[row + x] = (float)Math.Max(0, luma - threshold);
            }
        }

        int radius = Math.Max(1, (int)Math.Round(Math.Min(w, h) * 0.02 * glow / 100.0));
        var blurred = new float[w * h];
        BoxBlur(bright, blurred, w, h, radius);

        double gain = glow / 100.0 * 1.3;
        for (int i = 0; i < blurred.Length; i++)
        {
            double add = blurred[i] * gain;
            int p = i * 4;
            px[p] = ToByte(px[p] + add);
            px[p + 1] = ToByte(px[p + 1] + add);
            px[p + 2] = ToByte(px[p + 2] + add);
        }
    }

    private static void ApplyBlur(byte[] px, int w, int h, double blur)
    {
        int radius = (int)Math.Round(Math.Min(w, h) * 0.025 * blur / 100.0);
        if (radius < 1) return;

        var src = new float[w * h];
        var dst = new float[w * h];
        for (int channel = 0; channel < 3; channel++)
        {
            for (int i = 0; i < src.Length; i++)
                src[i] = px[i * 4 + channel];

            BoxBlur(src, dst, w, h, radius);

            for (int i = 0; i < dst.Length; i++)
                px[i * 4 + channel] = ToByte(dst[i]);
        }
    }

    private static void ApplyClarity(byte[] px, int w, int h, double clarity)
    {
        var luma = new float[w * h];
        for (int y = 0; y < h; y++)
        {
            int row = y * w;
            int src = row * 4;
            for (int x = 0; x < w; x++)
            {
                int i = src + x * 4;
                luma[row + x] = (float)(0.2126 * px[i + 2] + 0.7152 * px[i + 1] + 0.0722 * px[i]);
            }
        }

        var blurred = new float[w * h];
        int radius = Math.Max(1, (int)Math.Round(Math.Min(w, h) * 0.012));
        BoxBlur(luma, blurred, w, h, radius);

        double amount = clarity / 100.0 * 1.15;
        for (int i = 0; i < luma.Length; i++)
        {
            double delta = (luma[i] - blurred[i]) * amount;
            int p = i * 4;
            px[p] = ToByte(px[p] + delta);
            px[p + 1] = ToByte(px[p + 1] + delta);
            px[p + 2] = ToByte(px[p + 2] + delta);
        }
    }

    private static void BoxBlur(float[] src, float[] dst, int w, int h, int radius)
    {
        var tmp = new float[w * h];
        int window = radius * 2 + 1;

        for (int y = 0; y < h; y++)
        {
            int row = y * w;
            double sum = 0;
            for (int k = -radius; k <= radius; k++)
                sum += src[row + Math.Clamp(k, 0, w - 1)];

            for (int x = 0; x < w; x++)
            {
                if (x > 0)
                {
                    int add = row + Math.Min(w - 1, x + radius);
                    int remove = row + Math.Max(0, x - radius - 1);
                    sum += src[add] - src[remove];
                }
                tmp[row + x] = (float)(sum / window);
            }
        }

        for (int x = 0; x < w; x++)
        {
            double sum = 0;
            for (int k = -radius; k <= radius; k++)
                sum += tmp[Math.Clamp(k, 0, h - 1) * w + x];

            for (int y = 0; y < h; y++)
            {
                if (y > 0)
                {
                    int add = Math.Min(h - 1, y + radius) * w + x;
                    int remove = Math.Max(0, y - radius - 1) * w + x;
                    sum += tmp[add] - tmp[remove];
                }
                dst[y * w + x] = (float)(sum / window);
            }
        }
    }

    private static byte ToByte(double value) =>
        (byte)Math.Clamp((int)Math.Round(value), 0, 255);

    private static uint Hash(int x, int y)
    {
        uint h = (uint)(x * 374761393 + y * 668265263);
        h = (h ^ (h >> 13)) * 1274126177;
        return h ^ (h >> 16);
    }

    private static BitmapSource CreatePlaceholder()
    {
        const int w = 240;
        const int h = 160;
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            var sky = new LinearGradientBrush(
                Color.FromRgb(0x10, 0x1D, 0x30),
                Color.FromRgb(0x3F, 0x5E, 0x7C), 90);
            dc.DrawRectangle(sky, null, new Rect(0, 0, w, h));

            dc.DrawEllipse(new SolidColorBrush(Color.FromRgb(0xF3, 0xAA, 0x5A)), null,
                new Point(w * 0.76, h * 0.30), 21, 21);

            var far = new StreamGeometry();
            using (var ctx = far.Open())
            {
                ctx.BeginFigure(new Point(0, h * 0.72), true, true);
                ctx.LineTo(new Point(w * 0.22, h * 0.38), true, false);
                ctx.LineTo(new Point(w * 0.46, h * 0.70), true, false);
                ctx.LineTo(new Point(w * 0.64, h * 0.48), true, false);
                ctx.LineTo(new Point(w, h * 0.76), true, false);
                ctx.LineTo(new Point(w, h), true, false);
                ctx.LineTo(new Point(0, h), true, false);
            }
            far.Freeze();
            dc.DrawGeometry(new SolidColorBrush(Color.FromRgb(0x1B, 0x30, 0x47)), null, far);

            var near = new StreamGeometry();
            using (var ctx = near.Open())
            {
                ctx.BeginFigure(new Point(0, h * 0.86), true, true);
                ctx.LineTo(new Point(w * 0.30, h * 0.62), true, false);
                ctx.LineTo(new Point(w * 0.55, h * 0.82), true, false);
                ctx.LineTo(new Point(w * 0.80, h * 0.66), true, false);
                ctx.LineTo(new Point(w, h * 0.88), true, false);
                ctx.LineTo(new Point(w, h), true, false);
                ctx.LineTo(new Point(0, h), true, false);
            }
            near.Freeze();
            dc.DrawGeometry(new SolidColorBrush(Color.FromRgb(0x0B, 0x18, 0x27)), null, near);
        }

        var rtb = new RenderTargetBitmap(w, h, 96, 96, PixelFormats.Pbgra32);
        rtb.Render(visual);
        rtb.Freeze();
        return rtb;
    }
}
