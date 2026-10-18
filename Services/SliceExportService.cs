using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Webp;
using YorteImpact.Models;

namespace YorteImpact.Services;

/// <summary>
/// Renders the pasteboard to a bitmap and crops regions out of it.
/// Used for PNG export, and for Copy of a slice drawn over empty space.
/// The current view zoom is passed in (<paramref name="zoom"/>) so pixel
/// math stays correct while zoomed: total px per DIP = zoom * dpi / 96.
/// </summary>
public static class SliceExportService
{
    public static void ExportRegion(FrameworkElement pasteboard, Rect regionPasteboard, string filePath, double dpi = 96, double visualScale = 1)
    {
        var cropped = CropRegion(pasteboard, regionPasteboard, visualScale, dpi);
        SaveBitmap(cropped, filePath);
    }

    /// <summary>
    /// Save a bitmap as PNG, GIF or WebP based on the file extension.
    /// PNG/GIF use the built-in WIC encoders (GIF is static, 256 colors);
    /// WebP goes through ImageSharp.
    /// </summary>
    public static void SaveBitmap(BitmapSource bmp, string filePath)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(filePath)) ?? ".");
        string ext = Path.GetExtension(filePath).ToLowerInvariant();
        switch (ext)
        {
            case ".png":
            {
                var enc = new PngBitmapEncoder();
                enc.Frames.Add(BitmapFrame.Create(bmp));
                using var fs = File.OpenWrite(filePath);
                enc.Save(fs);
                break;
            }
            case ".gif":
            {
                var enc = new GifBitmapEncoder();
                enc.Frames.Add(BitmapFrame.Create(bmp));
                using var fs = File.OpenWrite(filePath);
                enc.Save(fs);
                break;
            }
            case ".webp":
                SaveWebp(bmp, filePath);
                break;
            default:
                throw new NotSupportedException($"Unsupported image format '{ext}'. Use .png, .gif or .webp.");
        }
    }

    private static void SaveWebp(BitmapSource bmp, string filePath)
    {
        var pngEnc = new PngBitmapEncoder();
        pngEnc.Frames.Add(BitmapFrame.Create(bmp));
        using var ms = new MemoryStream();
        pngEnc.Save(ms);
        ms.Position = 0;
        using var img = SixLabors.ImageSharp.Image.Load(ms);
        img.SaveAsWebp(filePath, new SixLabors.ImageSharp.Formats.Webp.WebpEncoder { Quality = 90 });
    }

    /// <summary>Crop a pasteboard-space rect to an in-memory bitmap (frozen).</summary>
    public static BitmapSource CropRegion(FrameworkElement pasteboard, Rect regionPasteboard, double zoom = 1, double dpi = 96)
    {
        if (regionPasteboard.Width < 1 || regionPasteboard.Height < 1)
            throw new ArgumentException("Slice region is empty.", nameof(regionPasteboard));
        if (zoom <= 0) zoom = 1;

        var full = RenderToBitmap(pasteboard, dpi, zoom);

        double s = zoom * dpi / 96.0;
        int x = (int)Math.Max(0, Math.Floor(regionPasteboard.X * s));
        int y = (int)Math.Max(0, Math.Floor(regionPasteboard.Y * s));
        int w = (int)Math.Min(full.PixelWidth - x, Math.Ceiling(regionPasteboard.Width * s));
        int h = (int)Math.Min(full.PixelHeight - y, Math.Ceiling(regionPasteboard.Height * s));
        if (w <= 0 || h <= 0) throw new ArgumentException("Slice region is outside the rendered surface.");

        var cropped = new CroppedBitmap(full, new Int32Rect(x, y, w, h));
        if (cropped.CanFreeze) cropped.Freeze();
        return cropped;
    }

    /// <summary>
    /// Crop the slice intersection out of ONE element, ignoring everything
    /// else on the canvas. Renders just the element's own visual (shape or
    /// image), then crops the region relative to its top-left corner. Used by
    /// Copy when a slice (marquee) is drawn over a selected object.
    /// </summary>
    public static BitmapSource CropElementRegion(DesignElement el, Rect interCanvas, double zoom = 1)
    {
        if (el.Width < 1 || el.Height < 1)
            throw new ArgumentException("Element is empty.", nameof(el));
        if (zoom <= 0) zoom = 1;

        var visual = BuildElementVisual(el);
        var rtb = new RenderTargetBitmap(
            Math.Max(1, (int)Math.Ceiling(el.Width * zoom)),
            Math.Max(1, (int)Math.Ceiling(el.Height * zoom)),
            96 * zoom, 96 * zoom, PixelFormats.Pbgra32);
        rtb.Render(visual);
        if (rtb.CanFreeze) rtb.Freeze();

        int x = (int)Math.Floor((interCanvas.X - el.X) * zoom);
        int y = (int)Math.Floor((interCanvas.Y - el.Y) * zoom);
        int w = (int)Math.Ceiling(interCanvas.Width * zoom);
        int h = (int)Math.Ceiling(interCanvas.Height * zoom);
        x = Math.Clamp(x, 0, Math.Max(0, rtb.PixelWidth - 1));
        y = Math.Clamp(y, 0, Math.Max(0, rtb.PixelHeight - 1));
        w = Math.Clamp(w, 1, rtb.PixelWidth - x);
        h = Math.Clamp(h, 1, rtb.PixelHeight - y);

        var cropped = new CroppedBitmap(rtb, new Int32Rect(x, y, w, h));
        if (cropped.CanFreeze) cropped.Freeze();
        return cropped;
    }

    /// <summary>Build the same Border the canvas template uses for an element (shape or image).</summary>
    private static FrameworkElement BuildElementVisual(DesignElement el)
    {
        var scale = new ScaleTransform(el.FlipScaleX, el.FlipScaleY);
        Border border;
        switch (el)
        {
            case ShapeElement s:
            {
                double radius = Math.Min(s.CornerRadius, Math.Min(s.Width, s.Height) / 2);
                border = new Border
                {
                    Width = s.Width, Height = s.Height,
                    CornerRadius = new CornerRadius(radius),
                    Background = s.FillBrush,
                    BorderBrush = s.BorderBrush,
                    BorderThickness = new Thickness(s.StrokeThickness),
                    Opacity = el.OpacityFactor,
                    OpacityMask = el.SoftEdgeMask,
                    RenderTransformOrigin = new System.Windows.Point(0.5, 0.5),
                    RenderTransform = scale,
                };
                break;
            }
            case ImageElement im:
                border = new Border
                {
                    Width = im.Width, Height = im.Height,
                    CornerRadius = new CornerRadius(im.CornerRadius),
                    Background = im.ImageBrush,
                    Opacity = el.OpacityFactor,
                    OpacityMask = el.SoftEdgeMask,
                    RenderTransformOrigin = new System.Windows.Point(0.5, 0.5),
                    RenderTransform = scale,
                };
                break;
            default:
                throw new NotSupportedException("Unsupported element type for slice copy.");
        }
        border.Measure(new System.Windows.Size(el.Width, el.Height));
        border.Arrange(new Rect(0, 0, el.Width, el.Height));
        return border;
    }

    /// <summary>
    /// Render a canvas-space region from the element host (elements only — no
    /// pasteboard chrome, no checkerboard) into a bitmap. When
    /// <paramref name="background"/> is set the region is flattened onto that
    /// solid color; when null the background stays transparent (PNG alpha).
    /// </summary>
    public static BitmapSource RenderRegion(FrameworkElement elementHost, Rect region, System.Windows.Media.Color? background, double zoom = 1, double dpi = 96)
    {
        var elements = CropRegion(elementHost, region, zoom, dpi);
        if (background is not { } bg) return elements;

        var dv = new DrawingVisual();
        using (var dc = dv.RenderOpen())
        {
            dc.DrawRectangle(new SolidColorBrush(bg), null,
                new Rect(0, 0, elements.PixelWidth, elements.PixelHeight));
            dc.DrawImage(elements, new Rect(0, 0, elements.PixelWidth, elements.PixelHeight));
        }
        var rtb = new RenderTargetBitmap(
            Math.Max(1, elements.PixelWidth), Math.Max(1, elements.PixelHeight),
            dpi, dpi, PixelFormats.Pbgra32);
        rtb.Render(dv);
        if (rtb.CanFreeze) rtb.Freeze();
        return rtb;
    }

    /// <summary>RenderRegion + save (image export, honoring transparency).</summary>
    public static void ExportCanvasRegion(FrameworkElement elementHost, Rect region, System.Windows.Media.Color? background, string filePath, double dpi = 96, double zoom = 1)
    {
        SaveBitmap(RenderRegion(elementHost, region, background, zoom, dpi), filePath);
    }

    public static BitmapSource RenderToBitmap(Visual visual, double dpi = 96, double zoom = 1)
    {
        if (visual is not FrameworkElement fe)
            throw new ArgumentException("Visual must be a FrameworkElement.", nameof(visual));
        if (zoom <= 0) zoom = 1;

        double w = fe.ActualWidth > 0 ? fe.ActualWidth : 3000;
        double h = fe.ActualHeight > 0 ? fe.ActualHeight : 2000;
        double pxPerDip = zoom * dpi / 96.0;

        int pxW = Math.Max(1, (int)Math.Ceiling(w * pxPerDip));
        int pxH = Math.Max(1, (int)Math.Ceiling(h * pxPerDip));

        // Snapshot through a VisualBrush with an explicit scale. Rendering the
        // child directly would ignore the view zoom: it lives as a LayoutTransform
        // on the Pasteboard (an ANCESTOR), and RenderTargetBitmap.Render(child)
        // only applies the visual's own transforms. Without this the crop offset
        // (region * zoom) and the rendered content (1:1) disagree — e.g. a slice
        // copy picks up pixels from above the region.
        var brush = new VisualBrush(fe)
        {
            ViewboxUnits = BrushMappingMode.Absolute,
            Viewbox = new Rect(0, 0, w, h),
            Stretch = Stretch.Fill,
        };
        var dv = new DrawingVisual();
        using (var dc = dv.RenderOpen())
            dc.DrawRectangle(brush, null, new Rect(0, 0, w * pxPerDip, h * pxPerDip));

        var rtb = new RenderTargetBitmap(pxW, pxH, dpi, dpi, PixelFormats.Pbgra32);
        rtb.Render(dv);
        if (rtb.CanFreeze) rtb.Freeze();
        return rtb;
    }
}
