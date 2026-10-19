using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using YorteImpact.Models;

namespace YorteImpact.Services;

/// <summary>Outcome of punching the slice rectangle out of an element.</summary>
public enum PunchResult
{
    Unsupported,    // e.g. rotated element, missing bitmap
    NoIntersection, // slice does not touch the element
    FullyCovered,   // slice covers the whole element -> caller deletes it
    Partial,        // slice area was removed, remainder kept
}

/// <summary>
/// Implements "Delete respects the slice": when the slice rectangle overlaps
/// the selected element, Delete removes only the overlapped part —
/// raster-erase for images, geometric subtraction (split into fragments)
/// for shapes. Only axis-aligned elements (|rotation| &lt;= 0.5°) are
/// supported; rotated elements fall back to whole-element delete.
/// </summary>
public static class SliceCutService
{
    private const double Tolerance = 0.5;

    public static bool IsAxisAligned(DesignElement el) =>
        Math.Abs(DesignElement.NormalizeAngle(el.RotationAngle)) <= Tolerance;

    /// <summary>
    /// Analyze a rendered slice: average color + whether it is (near-)uniform.
    /// Lets Copy of an empty-background slice paste back as a crisp solid
    /// ShapeElement instead of a raster tile.
    /// </summary>
    public static (Color Color, bool IsUniform) AnalyzeRegion(BitmapSource bmp)
    {
        BitmapSource src = bmp.Format == PixelFormats.Pbgra32
            ? bmp
            : new FormatConvertedBitmap(bmp, PixelFormats.Pbgra32, null, 0);

        int w = src.PixelWidth, h = src.PixelHeight;
        if (w <= 0 || h <= 0) return (Colors.Transparent, false);
        int stride = w * 4;
        var px = new byte[h * stride];
        src.CopyPixels(px, stride, 0);

        long sumR = 0, sumG = 0, sumB = 0, n = 0;
        int minR = 255, minG = 255, minB = 255, maxR = 0, maxG = 0, maxB = 0;
        bool opaque = true;

        for (int i = 0; i < px.Length; i += 4)
        {
            int b = px[i], g = px[i + 1], r = px[i + 2], a = px[i + 3];
            if (a < 250) { opaque = false; continue; }
            r = r * 255 / a; g = g * 255 / a; b = b * 255 / a; // un-premultiply
            sumR += r; sumG += g; sumB += b; n++;
            if (r < minR) minR = r; if (r > maxR) maxR = r;
            if (g < minG) minG = g; if (g > maxG) maxG = g;
            if (b < minB) minB = b; if (b > maxB) maxB = b;
        }

        if (n == 0) return (Colors.Transparent, false);
        var avg = Color.FromRgb((byte)(sumR / n), (byte)(sumG / n), (byte)(sumB / n));
        bool uniform = opaque && (maxR - minR) <= 8 && (maxG - minG) <= 8 && (maxB - minB) <= 8;
        return (avg, uniform);
    }

    /// <summary>
    /// Cut the slice area out of a shape and flatten the result to a SINGLE
    /// image element ("flat object"): fill, gradient, border and flip are
    /// baked to pixels at 2x, the overlapped part becomes transparent.
    /// Requires partial overlap of an axis-aligned shape (caller checks).
    /// </summary>
    public static ImageElement CutShapeFlat(ShapeElement el, Rect sliceCanvas)
    {
        const double scale = 2.0; // crisp pixels regardless of view zoom

        // Render the shape exactly as the canvas template draws it.
        var path = new System.Windows.Shapes.Path
        {
            Data = el.ShapeGeometry,
            Fill = el.FillBrush,
            Stroke = el.BorderBrush,
            StrokeThickness = el.StrokeThickness,
            StrokeLineJoin = PenLineJoin.Round,
            Stretch = Stretch.None,
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top,
        };
        var border = new Border
        {
            Width = el.Width,
            Height = el.Height,
            Child = path,
            RenderTransformOrigin = new Point(0.5, 0.5),
            RenderTransform = new ScaleTransform(el.FlipScaleX, el.FlipScaleY),
        };
        border.Measure(new Size(el.Width, el.Height));
        border.Arrange(new Rect(0, 0, el.Width, el.Height));

        var rtb = new RenderTargetBitmap(
            Math.Max(1, (int)Math.Ceiling(el.Width * scale)),
            Math.Max(1, (int)Math.Ceiling(el.Height * scale)),
            96 * scale, 96 * scale, PixelFormats.Pbgra32);
        rtb.Render(border);
        rtb.Freeze();

        var wb = new WriteableBitmap(rtb);

        // Erase the overlapped part (element-local DIPs -> bitmap pixels).
        var local = Rect.Intersect(el.Bounds, sliceCanvas);
        int x = (int)Math.Floor((local.X - el.X) * scale);
        int y = (int)Math.Floor((local.Y - el.Y) * scale);
        int w = (int)Math.Ceiling(local.Width * scale);
        int h = (int)Math.Ceiling(local.Height * scale);
        x = Math.Clamp(x, 0, wb.PixelWidth - 1);
        y = Math.Clamp(y, 0, wb.PixelHeight - 1);
        w = Math.Clamp(w, 1, wb.PixelWidth - x);
        h = Math.Clamp(h, 1, wb.PixelHeight - y);
        wb.WritePixels(new Int32Rect(x, y, w, h), new byte[h * w * 4], w * 4, 0);

        var img = new ImageElement
        {
            X = el.X,
            Y = el.Y,
            CornerRadius = 0,
        };
        img.SetBitmap(wb);
        img.Width = el.Width;
        img.Height = el.Height;
        TrimToOpaqueBounds(img);
        return img;
    }

    /// <summary>
    /// Shrink an image element to the bounding box of its visible pixels.
    /// After a slice removes an entire side, the punched pixels are transparent
    /// and the element would otherwise keep its old (now half-empty) box.
    /// </summary>
    private static void TrimToOpaqueBounds(ImageElement el)
    {
        if (el.Source is not BitmapSource src || src.PixelWidth <= 0 || src.PixelHeight <= 0)
            return;

        int pw = src.PixelWidth, ph = src.PixelHeight;
        double s = Math.Max(el.Width / pw, el.Height / ph);
        if (s <= 0) return;

        // Same UniformToFill center-crop mapping used by the renderer.
        double offX = (pw * s - el.Width) / 2;
        double offY = (ph * s - el.Height) / 2;

        int visX = Math.Clamp((int)Math.Floor(offX / s), 0, pw - 1);
        int visY = Math.Clamp((int)Math.Floor(offY / s), 0, ph - 1);
        int visW = Math.Clamp((int)Math.Ceiling(el.Width / s), 1, pw - visX);
        int visH = Math.Clamp((int)Math.Ceiling(el.Height / s), 1, ph - visY);

        BitmapSource formatted = src.Format == PixelFormats.Pbgra32
            ? src
            : new FormatConvertedBitmap(src, PixelFormats.Pbgra32, null, 0);

        int stride = visW * 4;
        var pixels = new byte[stride * visH];
        formatted.CopyPixels(new Int32Rect(visX, visY, visW, visH), pixels, stride, 0);

        int minX = int.MaxValue, minY = int.MaxValue, maxX = -1, maxY = -1;
        for (int y = 0; y < visH; y++)
        {
            int row = y * stride;
            for (int x = 0; x < visW; x++)
            {
                if (pixels[row + x * 4 + 3] == 0) continue;
                if (x < minX) minX = x;
                if (x > maxX) maxX = x;
                if (y < minY) minY = y;
                if (y > maxY) maxY = y;
            }
        }

        if (maxX < 0) return; // nothing opaque left
        int cw = maxX - minX + 1;
        int ch = maxY - minY + 1;
        if (cw >= visW && ch >= visH) return; // content still fills the box

        int cropX = visX + minX;
        int cropY = visY + minY;
        var crop = new CroppedBitmap(formatted, new Int32Rect(cropX, cropY, cw, ch));
        if (crop.CanFreeze) crop.Freeze();

        el.X += cropX * s - offX;
        el.Y += cropY * s - offY;
        el.Width = Math.Max(1, cw * s);
        el.Height = Math.Max(1, ch * s);
        el.Source = crop;
    }

    /// <summary>
    /// Erase the slice-overlapped pixels of an image element (alpha = 0).
    /// Accounts for the UniformToFill + center-crop display mapping, so the
    /// hole lines up with the dashed slice rect on screen.
    /// </summary>
    public static PunchResult EraseImageRegion(ImageElement el, Rect sliceCanvas)
    {
        if (!IsAxisAligned(el)) return PunchResult.Unsupported;
        if (el.Source is not BitmapSource src || src.PixelWidth <= 0 || src.PixelHeight <= 0)
            return PunchResult.Unsupported;

        var local = Rect.Intersect(el.Bounds, sliceCanvas);
        if (local.IsEmpty || local.Width < 1 || local.Height < 1)
            return PunchResult.NoIntersection;

        // Fully covered -> caller deletes the whole element.
        if (local.X <= el.X + 0.5 && local.Y <= el.Y + 0.5 &&
            local.Right >= el.X + el.Width - 0.5 && local.Bottom >= el.Y + el.Height - 0.5)
            return PunchResult.FullyCovered;

        // UniformToFill mapping: source pixels -> DIPs, centered crop.
        int pw = src.PixelWidth, ph = src.PixelHeight;
        double s = Math.Max(el.Width / pw, el.Height / ph);
        double offX = (pw * s - el.Width) / 2;   // cropped-away DIPs on the left
        double offY = (ph * s - el.Height) / 2;  // cropped-away DIPs on the top

        int x = (int)Math.Floor((offX + (local.X - el.X)) / s);
        int y = (int)Math.Floor((offY + (local.Y - el.Y)) / s);
        int w = (int)Math.Ceiling(local.Width / s);
        int h = (int)Math.Ceiling(local.Height / s);

        x = Math.Clamp(x, 0, pw - 1);
        y = Math.Clamp(y, 0, ph - 1);
        w = Math.Clamp(w, 1, pw - x);
        h = Math.Clamp(h, 1, ph - y);

        BitmapSource formatted = src.Format == PixelFormats.Pbgra32
            ? src
            : new FormatConvertedBitmap(src, PixelFormats.Pbgra32, null, 0);

        var wb = new WriteableBitmap(formatted);
        int stride = w * 4;
        wb.WritePixels(new Int32Rect(x, y, w, h), new byte[h * stride], stride, 0);
        el.Source = wb; // setter rebuilds the ImageBrush
        TrimToOpaqueBounds(el);
        return PunchResult.Partial;
    }
}
