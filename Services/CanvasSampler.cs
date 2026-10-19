using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using YorteImpact.Models;

namespace YorteImpact.Services;

/// <summary>
/// Exact canvas color queries for the eyedropper (no rendering: analytic
/// shape math + single-pixel image reads). Pure functions over the model,
/// so they stay unit-testable; the view only supplies hit-testing.
/// </summary>
public static class CanvasSampler
{
    /// <summary>Canvas point -> element-local unrotated coords.</summary>
    public static Point ToLocalUnrotated(DesignElement el, Point p)
    {
        double cx = el.X + el.Width / 2, cy = el.Y + el.Height / 2;
        double rad = -el.RotationAngle * Math.PI / 180.0;
        double cos = Math.Cos(rad), sin = Math.Sin(rad);
        double vx = p.X - cx, vy = p.Y - cy;
        return new Point(vx * cos - vy * sin + el.Width / 2, vx * sin + vy * cos + el.Height / 2);
    }

    public static bool InRoundedRect(double x, double y, double w, double h, double r)
    {
        if (x < 0 || y < 0 || x > w || y > h) return false;
        if (r <= 0) return true;
        double cx = Math.Min(Math.Max(x, r), w - r);
        double cy = Math.Min(Math.Max(y, r), h - r);
        double dx = x - cx, dy = y - cy;
        return dx * dx + dy * dy <= r * r;
    }

    /// <summary>Exact displayed color of a shape at a canvas point (bg = what's behind).</summary>
    public static Color SampleShape(ShapeElement s, Point canvasPoint, Color behind)
    {
        var local = ToLocalUnrotated(s, canvasPoint);
        double r = Math.Min(s.CornerRadius, Math.Min(s.Width, s.Height) / 2);
        if (!InRoundedRect(local.X, local.Y, s.Width, s.Height, r))
            return behind; // transparent rounded corner

        // Border band (approximate near rounded corners).
        double edge = Math.Min(Math.Min(local.X, s.Width - local.X), Math.Min(local.Y, s.Height - local.Y));
        if (edge < s.StrokeThickness && s.StrokeThickness > 0)
            return s.StrokeColor;

        if (!s.IsGradient) return s.FillColor1;

        // Spot on the same gradient the brush draws (linear / radial / elliptical).
        double t = s.GradientT(local.X, local.Y);
        return Lerp(s.FillColor1, s.FillColor2, Math.Clamp(t, 0, 1));
    }

    /// <summary>Exact displayed pixel of an image element (UniformToFill crop aware).</summary>
    public static Color SampleImagePixel(ImageElement im, Point canvasPoint, Color behind)
    {
        if (im.DisplaySource is not BitmapSource src || src.PixelWidth <= 0 || src.PixelHeight <= 0)
            return behind;
        var local = ToLocalUnrotated(im, canvasPoint);
        if (im.FlipHorizontal) local.X = im.Width - local.X;
        if (im.FlipVertical) local.Y = im.Height - local.Y;
        double r = Math.Min(im.CornerRadius, Math.Min(im.Width, im.Height) / 2);
        if (!InRoundedRect(local.X, local.Y, im.Width, im.Height, r))
            return behind;

        int pw = src.PixelWidth, ph = src.PixelHeight;
        if (!im.TryMapToSource(local, out double fx, out double fy, out _)) return behind;
        int sx = Math.Clamp((int)fx, 0, pw - 1);
        int sy = Math.Clamp((int)fy, 0, ph - 1);
        var one = new CroppedBitmap(src, new Int32Rect(sx, sy, 1, 1));
        var fmt = new FormatConvertedBitmap(one, PixelFormats.Pbgra32, null, 0);
        var bytes = new byte[4];
        fmt.CopyPixels(bytes, 4, 0);
        int a = bytes[3];
        if (a == 0) return behind;
        return Color.FromRgb(
            (byte)(bytes[2] * 255 / Math.Max(1, a)),
            (byte)(bytes[1] * 255 / Math.Max(1, a)),
            (byte)(bytes[0] * 255 / Math.Max(1, a)));
    }

    /// <summary>Canvas point -> source pixel of an image element (UniformToFill crop aware). False when outside.</summary>
    public static bool TryToSourcePixel(ImageElement im, Point canvasPoint, out int sx, out int sy)
    {
        sx = sy = 0;
        if (im.Source is not BitmapSource src || src.PixelWidth <= 0 || src.PixelHeight <= 0) return false;
        var local = ToLocalUnrotated(im, canvasPoint);
        if (im.FlipHorizontal) local.X = im.Width - local.X;
        if (im.FlipVertical) local.Y = im.Height - local.Y;
        double r = Math.Min(im.CornerRadius, Math.Min(im.Width, im.Height) / 2);
        if (!InRoundedRect(local.X, local.Y, im.Width, im.Height, r)) return false;

        int pw = src.PixelWidth, ph = src.PixelHeight;
        if (!im.TryMapToSource(local, out double fx, out double fy, out _)) return false;
        sx = Math.Clamp((int)fx, 0, pw - 1);
        sy = Math.Clamp((int)fy, 0, ph - 1);
        return true;
    }

    public static Color Lerp(Color a, Color b, double t) =>        Color.FromRgb(
            (byte)(a.R + (b.R - a.R) * t),
            (byte)(a.G + (b.G - a.G) * t),
            (byte)(a.B + (b.B - a.B) * t));

    public static Color Darken(Color c, double f) =>
        Color.FromRgb((byte)(c.R * f), (byte)(c.G * f), (byte)(c.B * f));
}
