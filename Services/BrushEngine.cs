using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace YorteImpact.Services;

public enum BrushStyle
{
    Paint,
    Smudge,
    Watercolor,
    Eraser,
    Merge,
}

public enum BrushTargetMode
{
    Auto,
    Canvas,
    Selected,
}

/// <summary>Runtime brush settings (color is a WPF <see cref="Color"/>).</summary>
public sealed class BrushSettings
{
    public BrushStyle Style { get; set; } = BrushStyle.Paint;
    public BrushTargetMode Target { get; set; } = BrushTargetMode.Auto;
    public double Size { get; set; } = 36;
    public double Opacity { get; set; } = 100;
    public double Softness { get; set; } = 45;
    public double Smoothing { get; set; } = 35;
    public Color Color { get; set; } = Color.FromRgb(0x2E, 0x9B, 0xFF);

    public BrushSettings Copy() => new()
    {
        Style = Style,
        Target = Target,
        Size = Size,
        Opacity = Opacity,
        Softness = Softness,
        Smoothing = Smoothing,
        Color = Color,
    };
}

/// <summary>Mutable BGRA32 raster plus the scale between canvas units and its pixels.</summary>
public sealed class BrushSurface
{
    public WriteableBitmap Bitmap { get; }
    public byte[] Pixels { get; }
    public int Width => Bitmap.PixelWidth;
    public int Height => Bitmap.PixelHeight;
    public int Stride => Width * 4;
    public double PixelsPerUnit { get; }

    public BrushSurface(WriteableBitmap bitmap, byte[] pixels, double pixelsPerUnit)
    {
        Bitmap = bitmap;
        Pixels = pixels;
        PixelsPerUnit = Math.Max(0.0001, pixelsPerUnit);
    }
}

/// <summary>
/// Raster brush engine used by the paintbrush tool. All coordinates are in the
/// surface's pixel space; the host maps pasteboard points into that space.
/// Supports a normal brush, localized smudge/blur, watercolor build-up, a soft
/// eraser and a merge-to-background brush.
/// </summary>
public sealed class BrushEngine
{
    private BrushSurface? _surface;
    private BrushSettings _settings = new();
    private bool _active;
    private Point _lastRaw;
    private Point _lastSmooth;
    private Point _lastStamp;
    private double _lastPressure = 1;
    private int _seed;
    private Color _mergeColor = Colors.White;

    public bool IsActive => _active;

    public void Begin(BrushSurface surface, Point point, BrushSettings settings, Color mergeColor, double pressure)
    {
        _surface = surface;
        _settings = settings.Copy();
        _seed = Environment.TickCount;
        _lastRaw = _lastSmooth = _lastStamp = point;
        _lastPressure = ClampPressure(pressure);
        _mergeColor = mergeColor;
        _active = true;
        Stamp(point, 1.0, _lastPressure);
    }

    public void Move(Point point, double pressure, Color mergeColor)
    {
        if (!_active || _surface == null) return;

        _lastPressure = ClampPressure(pressure);
        _mergeColor = mergeColor;

        double smoothing = Math.Clamp(_settings.Smoothing / 100.0, 0, 1);
        double k = 1 - smoothing * 0.82;
        var smooth = smoothing < 0.02
            ? point
            : new Point(
                _lastSmooth.X + (point.X - _lastSmooth.X) * k,
                _lastSmooth.Y + (point.Y - _lastSmooth.Y) * k);

        double sizePx = Math.Max(1, _settings.Size * _surface.PixelsPerUnit);
        double spacing = Math.Max(0.75, sizePx * 0.10);
        double dx = smooth.X - _lastStamp.X;
        double dy = smooth.Y - _lastStamp.Y;
        double distance = Math.Sqrt(dx * dx + dy * dy);
        if (distance < 0.01) return;

        double velocity = 1.0;
        if (_settings.Style is BrushStyle.Paint or BrushStyle.Watercolor)
        {
            double speed = distance / Math.Max(1.5, sizePx * 0.65);
            velocity = Math.Clamp(1.0 - speed * 0.28, 0.68, 1.0);
        }

        int steps = Math.Max(1, (int)Math.Ceiling(distance / spacing));
        for (int i = 1; i <= steps; i++)
        {
            double t = (double)i / steps;
            var p = new Point(
                _lastStamp.X + (smooth.X - _lastStamp.X) * t,
                _lastStamp.Y + (smooth.Y - _lastStamp.Y) * t);
            Stamp(p, velocity, _lastPressure);
        }

        _lastRaw = point;
        _lastSmooth = smooth;
        _lastStamp = smooth;
    }

    public void End()
    {
        if (!_active) return;
        if (_surface != null)
        {
            double dx = _lastRaw.X - _lastStamp.X;
            double dy = _lastRaw.Y - _lastStamp.Y;
            if (dx * dx + dy * dy > 0.25)
                Stamp(_lastRaw, 1.0, _lastPressure);
        }
        _active = false;
        _surface = null;
    }

    /// <summary>Resume a stroke at a new point without painting across a gap.</summary>
    public void SkipTo(Point point)
    {
        if (!_active) return;
        _lastRaw = _lastSmooth = _lastStamp = point;
    }

    private static double ClampPressure(double pressure) =>
        double.IsFinite(pressure) ? Math.Clamp(pressure, 0.05, 1.0) : 1.0;

    private void Stamp(Point center, double velocityFactor, double pressure)
    {
        if (_surface == null) return;

        double baseRadius = Math.Max(0.6, _settings.Size * _surface.PixelsPerUnit / 2);
        double radius = baseRadius;
        if (_settings.Style is BrushStyle.Paint or BrushStyle.Watercolor)
            radius *= velocityFactor;
        radius *= 0.55 + 0.45 * pressure;
        radius = Math.Max(0.6, radius);

        switch (_settings.Style)
        {
            case BrushStyle.Smudge:
                StampSmudge(center, radius, pressure);
                break;
            case BrushStyle.Watercolor:
                StampWatercolor(center, radius, pressure);
                break;
            case BrushStyle.Eraser:
                StampEraser(center, radius, pressure);
                break;
            case BrushStyle.Merge:
                StampPaint(center, radius, pressure, _mergeColor, 0.72);
                break;
            default:
                StampPaint(center, radius, pressure, _settings.Color, 1.0);
                break;
        }
    }

    private void StampPaint(Point center, double radius, double pressure, Color color, double flow)
    {
        var surface = _surface!;
        int x0 = Math.Max(0, (int)Math.Floor(center.X - radius));
        int x1 = Math.Min(surface.Width - 1, (int)Math.Ceiling(center.X + radius));
        int y0 = Math.Max(0, (int)Math.Floor(center.Y - radius));
        int y1 = Math.Min(surface.Height - 1, (int)Math.Ceiling(center.Y + radius));
        if (x1 < x0 || y1 < y0) return;

        double alpha = Math.Clamp(_settings.Opacity / 100.0, 0, 1) * (0.55 + 0.45 * pressure);
        double colorAlpha = color.A / 255.0;
        for (int y = y0; y <= y1; y++)
        {
            double dy = y - center.Y;
            for (int x = x0; x <= x1; x++)
            {
                double dx = x - center.X;
                double dist = Math.Sqrt(dx * dx + dy * dy);
                if (dist > radius) continue;
                double falloff = Falloff(dist, radius);
                if (falloff <= 0.001) continue;
                double a = alpha * falloff * flow * colorAlpha;
                if (a <= 0.001) continue;
                Blend(surface.Pixels, (y * surface.Width + x) * 4, color.R, color.G, color.B, Math.Min(1, a));
            }
        }
        WriteDirty(x0, y0, x1 - x0 + 1, y1 - y0 + 1);
    }

    private void StampWatercolor(Point center, double radius, double pressure)
    {
        var surface = _surface!;
        int x0 = Math.Max(0, (int)Math.Floor(center.X - radius));
        int x1 = Math.Min(surface.Width - 1, (int)Math.Ceiling(center.X + radius));
        int y0 = Math.Max(0, (int)Math.Floor(center.Y - radius));
        int y1 = Math.Min(surface.Height - 1, (int)Math.Ceiling(center.Y + radius));
        if (x1 < x0 || y1 < y0) return;

        double alpha = Math.Clamp(_settings.Opacity / 100.0, 0, 1) * (0.55 + 0.45 * pressure);
        var brush = _settings.Color;
        for (int y = y0; y <= y1; y++)
        {
            double dy = y - center.Y;
            for (int x = x0; x <= x1; x++)
            {
                double dx = x - center.X;
                double dist = Math.Sqrt(dx * dx + dy * dy);
                if (dist > radius) continue;
                double falloff = Falloff(dist, radius);
                if (falloff <= 0.001) continue;

                double edge = dist / radius;
                double edgeBoost = edge > 0.60 ? 1.0 + (edge - 0.60) / 0.40 * 1.0 : 1.0;
                double noise = Noise(x, y);
                double a = alpha * falloff * 0.50 * (0.75 + 0.40 * noise) * edgeBoost;
                if (a <= 0.001) continue;

                int i = (y * surface.Width + x) * 4;
                double da = surface.Pixels[i + 3] / 255.0;
                double mix = da > 0.01 ? 0.15 + 0.10 * noise : 0;
                byte sr = (byte)Math.Round(brush.R * (1 - mix) + surface.Pixels[i] * mix);
                byte sg = (byte)Math.Round(brush.G * (1 - mix) + surface.Pixels[i + 1] * mix);
                byte sb = (byte)Math.Round(brush.B * (1 - mix) + surface.Pixels[i + 2] * mix);
                if (edge > 0.60)
                {
                    sr = (byte)(sr * 0.92);
                    sg = (byte)(sg * 0.92);
                    sb = (byte)(sb * 0.92);
                }
                Blend(surface.Pixels, i, sr, sg, sb, Math.Min(1, a));
            }
        }
        WriteDirty(x0, y0, x1 - x0 + 1, y1 - y0 + 1);
    }

    private void StampEraser(Point center, double radius, double pressure)
    {
        var surface = _surface!;
        int x0 = Math.Max(0, (int)Math.Floor(center.X - radius));
        int x1 = Math.Min(surface.Width - 1, (int)Math.Ceiling(center.X + radius));
        int y0 = Math.Max(0, (int)Math.Floor(center.Y - radius));
        int y1 = Math.Min(surface.Height - 1, (int)Math.Ceiling(center.Y + radius));
        if (x1 < x0 || y1 < y0) return;

        double alpha = Math.Clamp(_settings.Opacity / 100.0, 0, 1) * (0.55 + 0.45 * pressure);
        for (int y = y0; y <= y1; y++)
        {
            double dy = y - center.Y;
            for (int x = x0; x <= x1; x++)
            {
                double dx = x - center.X;
                double dist = Math.Sqrt(dx * dx + dy * dy);
                if (dist > radius) continue;
                double falloff = Falloff(dist, radius);
                if (falloff <= 0.001) continue;
                int i = (y * surface.Width + x) * 4;
                double a = alpha * falloff;
                surface.Pixels[i + 3] = (byte)Math.Round(surface.Pixels[i + 3] * (1 - a));
            }
        }
        WriteDirty(x0, y0, x1 - x0 + 1, y1 - y0 + 1);
    }

    private void StampSmudge(Point center, double radius, double pressure)
    {
        var surface = _surface!;
        int x0 = Math.Max(0, (int)Math.Floor(center.X - radius));
        int x1 = Math.Min(surface.Width - 1, (int)Math.Ceiling(center.X + radius));
        int y0 = Math.Max(0, (int)Math.Floor(center.Y - radius));
        int y1 = Math.Min(surface.Height - 1, (int)Math.Ceiling(center.Y + radius));
        if (x1 < x0 || y1 < y0) return;

        var average = SampleAverage(center, radius);
        if (average.A <= 0.001) return;

        double alpha = Math.Clamp(_settings.Opacity / 100.0, 0, 1) * (0.55 + 0.45 * pressure);
        for (int y = y0; y <= y1; y++)
        {
            double dy = y - center.Y;
            for (int x = x0; x <= x1; x++)
            {
                double dx = x - center.X;
                double dist = Math.Sqrt(dx * dx + dy * dy);
                if (dist > radius) continue;
                double falloff = Falloff(dist, radius);
                if (falloff <= 0.001) continue;

                int i = (y * surface.Width + x) * 4;
                double k = Math.Min(0.9, alpha * falloff * 0.55);
                double da = surface.Pixels[i + 3] / 255.0;
                double newA = da + (average.A / 255.0 - da) * k;
                surface.Pixels[i] = (byte)Math.Round(surface.Pixels[i] + (average.R - surface.Pixels[i]) * k);
                surface.Pixels[i + 1] = (byte)Math.Round(surface.Pixels[i + 1] + (average.G - surface.Pixels[i + 1]) * k);
                surface.Pixels[i + 2] = (byte)Math.Round(surface.Pixels[i + 2] + (average.B - surface.Pixels[i + 2]) * k);
                surface.Pixels[i + 3] = (byte)Math.Clamp((int)Math.Round(newA * 255), 0, 255);
            }
        }
        WriteDirty(x0, y0, x1 - x0 + 1, y1 - y0 + 1);
    }

    private Color SampleAverage(Point center, double radius)
    {
        var surface = _surface!;
        int x0 = Math.Max(0, (int)Math.Floor(center.X - radius));
        int x1 = Math.Min(surface.Width - 1, (int)Math.Ceiling(center.X + radius));
        int y0 = Math.Max(0, (int)Math.Floor(center.Y - radius));
        int y1 = Math.Min(surface.Height - 1, (int)Math.Ceiling(center.Y + radius));
        if (x1 < x0 || y1 < y0) return Colors.Transparent;

        int step = Math.Max(1, (int)(radius / 10));
        double r = 0, g = 0, b = 0, a = 0, weight = 0;
        for (int y = y0; y <= y1; y += step)
        {
            double dy = y - center.Y;
            for (int x = x0; x <= x1; x += step)
            {
                double dx = x - center.X;
                if (dx * dx + dy * dy > radius * radius) continue;
                int i = (y * surface.Width + x) * 4;
                double w = surface.Pixels[i + 3] / 255.0;
                if (w <= 0.002) continue;
                r += surface.Pixels[i] * w;
                g += surface.Pixels[i + 1] * w;
                b += surface.Pixels[i + 2] * w;
                a += w;
                weight += w;
            }
        }
        if (weight <= 0.0001) return Colors.Transparent;
        return Color.FromArgb(
            (byte)Math.Clamp((int)Math.Round(a / Math.Max(1, weight) * 255), 0, 255),
            (byte)Math.Clamp((int)Math.Round(r / weight), 0, 255),
            (byte)Math.Clamp((int)Math.Round(g / weight), 0, 255),
            (byte)Math.Clamp((int)Math.Round(b / weight), 0, 255));
    }

    private static double Falloff(double distance, double radius, double softness)
    {
        double inner = radius * (1 - softness);
        double feather = radius - inner;
        if (feather <= 0.0001 || distance <= inner) return 1;
        double t = (distance - inner) / feather;
        t = Math.Clamp(t, 0, 1);
        return 1 - t * t * (3 - 2 * t);
    }

    private double Falloff(double distance, double radius)
    {
        return Falloff(distance, radius, Math.Clamp(_settings.Softness / 100.0, 0, 1));
    }

    private static void Blend(byte[] pixels, int i, byte r, byte g, byte b, double a)
    {
        double sa = Math.Clamp(a, 0, 1);
        if (sa <= 0.0001) return;
        double da = pixels[i + 3] / 255.0;
        double inv = da * (1 - sa);
        double outA = sa + inv;
        if (outA <= 0.0001)
        {
            pixels[i] = pixels[i + 1] = pixels[i + 2] = pixels[i + 3] = 0;
            return;
        }
        pixels[i] = (byte)Math.Clamp((int)Math.Round((b * sa + pixels[i] * inv) / outA), 0, 255);
        pixels[i + 1] = (byte)Math.Clamp((int)Math.Round((g * sa + pixels[i + 1] * inv) / outA), 0, 255);
        pixels[i + 2] = (byte)Math.Clamp((int)Math.Round((r * sa + pixels[i + 2] * inv) / outA), 0, 255);
        pixels[i + 3] = (byte)Math.Clamp((int)Math.Round(outA * 255), 0, 255);
    }

    private double Noise(int x, int y)
    {
        unchecked
        {
            int h = x * 374761393 + y * 668265263 + _seed * 1442695041;
            h = (h ^ (h >> 13)) * 1274126177;
            return ((h ^ (h >> 16)) & 0xFFFF) / 65535.0;
        }
    }

    private void WriteDirty(int x, int y, int width, int height)
    {
        if (_surface == null || width <= 0 || height <= 0) return;
        int offset = y * _surface.Stride + x * 4;
        _surface.Bitmap.WritePixels(
            new Int32Rect(x, y, width, height),
            _surface.Pixels, _surface.Stride, offset);
    }
}

/// <summary>Pixel helpers shared by the paintbrush, undo and merge sampling.</summary>
public static class BrushRaster
{
    public static WriteableBitmap CopyToWriteable(BitmapSource source)
    {
        BitmapSource bgra = source.Format == PixelFormats.Bgra32
            ? source
            : new FormatConvertedBitmap(source, PixelFormats.Bgra32, null, 0);

        int width = Math.Max(1, bgra.PixelWidth);
        int height = Math.Max(1, bgra.PixelHeight);
        int stride = width * 4;
        var pixels = new byte[stride * height];
        bgra.CopyPixels(pixels, stride, 0);

        var bitmap = new WriteableBitmap(width, height, 96, 96, PixelFormats.Bgra32, null);
        bitmap.WritePixels(new Int32Rect(0, 0, width, height), pixels, stride, 0);
        return bitmap;
    }

    public static byte[] ReadPixels(WriteableBitmap bitmap)
    {
        int stride = bitmap.PixelWidth * 4;
        var pixels = new byte[stride * bitmap.PixelHeight];
        bitmap.CopyPixels(pixels, stride, 0);
        return pixels;
    }

    public static WriteableBitmap CreateTransparent(int width, int height)
    {
        width = Math.Max(1, width);
        height = Math.Max(1, height);
        return new WriteableBitmap(width, height, 96, 96, PixelFormats.Bgra32, null);
    }

    public static WriteableBitmap Resize(BitmapSource source, int width, int height)
    {
        width = Math.Max(1, width);
        height = Math.Max(1, height);
        var visual = new DrawingVisual();
        RenderOptions.SetBitmapScalingMode(visual, BitmapScalingMode.HighQuality);
        using (var dc = visual.RenderOpen())
            dc.DrawImage(source, new Rect(0, 0, width, height));
        var rtb = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        rtb.Render(visual);
        return CopyToWriteable(rtb);
    }

    public static Color Sample(BitmapSource source, int x, int y)
    {
        if (x < 0 || y < 0 || x >= source.PixelWidth || y >= source.PixelHeight)
            return Colors.Transparent;
        BitmapSource bgra = source.Format == PixelFormats.Bgra32
            ? source
            : new FormatConvertedBitmap(source, PixelFormats.Bgra32, null, 0);
        var pixel = new byte[4];
        bgra.CopyPixels(new Int32Rect(x, y, 1, 1), pixel, 4, 0);
        return Color.FromArgb(pixel[3], pixel[2], pixel[1], pixel[0]);
    }

    public static Color Sample(byte[] pixels, int width, int height, int x, int y)
    {
        if (x < 0 || y < 0 || x >= width || y >= height) return Colors.Transparent;
        int i = (y * width + x) * 4;
        return Color.FromArgb(pixels[i + 3], pixels[i + 2], pixels[i + 1], pixels[i]);
    }
}
