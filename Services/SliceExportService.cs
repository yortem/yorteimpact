using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Webp;
using YorteImpact.Controls;
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
    /// <summary>Make pixels outside a Bezier slice transparent.</summary>
    public static BitmapSource ApplySliceMask(BitmapSource source, SliceRegion slice, Rect outputRect, bool inverse = false)
    {
        if (slice.SelectionGeometry == null || outputRect.Width <= 0 || outputRect.Height <= 0)
            return source;
        double sx = source.PixelWidth / outputRect.Width;
        double sy = source.PixelHeight / outputRect.Height;
        var mask = slice.SelectionGeometry.Clone();
        mask.Transform = new MatrixTransform(new Matrix(sx, 0, 0, sy, -outputRect.X * sx, -outputRect.Y * sy));
        Geometry clip = inverse
            ? Geometry.Combine(new RectangleGeometry(new Rect(0, 0, source.PixelWidth, source.PixelHeight)), mask,
                GeometryCombineMode.Exclude, null)
            : mask;
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            dc.PushClip(clip);
            dc.DrawImage(source, new Rect(0, 0, source.PixelWidth, source.PixelHeight));
            dc.Pop();
        }
        var result = new RenderTargetBitmap(source.PixelWidth, source.PixelHeight, 96, 96, PixelFormats.Pbgra32);
        result.Render(visual);
        if (result.CanFreeze) result.Freeze();
        return result;
    }

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

    /// <summary>Build the same Border the canvas template uses for an element.</summary>
    private static FrameworkElement BuildElementVisual(DesignElement el, bool applyFlip = true)
    {
        var scale = new ScaleTransform(el.FlipScaleX, el.FlipScaleY, el.Width / 2, el.Height / 2);
        Border border;
        switch (el)
        {
            case ShapeElement s:
            {
                var path = new System.Windows.Shapes.Path
                {
                    Data = s.ShapeGeometry,
                    Fill = s.FillBrush,
                    Stroke = s.BorderBrush,
                    StrokeThickness = s.StrokeThickness,
                    StrokeLineJoin = PenLineJoin.Round,
                    Stretch = Stretch.None,
                    HorizontalAlignment = HorizontalAlignment.Left,
                    VerticalAlignment = VerticalAlignment.Top,
                };
                border = new Border
                {
                    Width = s.Width, Height = s.Height,
                    Child = path,
                    Opacity = el.OpacityFactor,
                    OpacityMask = el.OpacityMaskBrush,
                    Effect = el.ShadowEffect,
                    RenderTransformOrigin = new System.Windows.Point(0.5, 0.5),
                };
                break;
            }
            case LineElement line:
            {
                var path = new System.Windows.Shapes.Path
                {
                    Data = line.PathGeometry,
                    Fill = Brushes.Transparent,
                    Stroke = line.StrokeBrush,
                    StrokeThickness = line.StrokeThickness,
                    StrokeDashArray = line.DashArray,
                    StrokeDashCap = PenLineCap.Round,
                    StrokeStartLineCap = line.StrokeStartLineCap,
                    StrokeEndLineCap = line.StrokeEndLineCap,
                    Stretch = Stretch.None,
                    HorizontalAlignment = HorizontalAlignment.Left,
                    VerticalAlignment = VerticalAlignment.Top,
                };
                var lineVisuals = new Grid { ClipToBounds = false };
                lineVisuals.Children.Add(path);
                lineVisuals.Children.Add(new System.Windows.Shapes.Path
                {
                    Data = line.StartMarkerGeometry,
                    Fill = line.StrokeBrush,
                    Stroke = line.StrokeBrush,
                    StrokeThickness = line.StrokeThickness,
                    StrokeStartLineCap = PenLineCap.Round,
                    StrokeEndLineCap = PenLineCap.Round,
                    StrokeLineJoin = PenLineJoin.Round,
                    Stretch = Stretch.None,
                    HorizontalAlignment = HorizontalAlignment.Left,
                    VerticalAlignment = VerticalAlignment.Top,
                });
                lineVisuals.Children.Add(new System.Windows.Shapes.Path
                {
                    Data = line.EndMarkerGeometry,
                    Fill = line.StrokeBrush,
                    Stroke = line.StrokeBrush,
                    StrokeThickness = line.StrokeThickness,
                    StrokeStartLineCap = PenLineCap.Round,
                    StrokeEndLineCap = PenLineCap.Round,
                    StrokeLineJoin = PenLineJoin.Round,
                    Stretch = Stretch.None,
                    HorizontalAlignment = HorizontalAlignment.Left,
                    VerticalAlignment = VerticalAlignment.Top,
                });
                border = new Border
                {
                    Width = line.Width, Height = line.Height,
                    Background = Brushes.Transparent,
                    ClipToBounds = false,
                    Child = lineVisuals,
                    Opacity = el.OpacityFactor,
                    OpacityMask = el.OpacityMaskBrush,
                    Effect = el.ShadowEffect,
                    RenderTransformOrigin = new System.Windows.Point(0.5, 0.5),
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
                    OpacityMask = el.OpacityMaskBrush,
                    Effect = el.ShadowEffect,
                    RenderTransformOrigin = new System.Windows.Point(0.5, 0.5),
                };
                break;
            case TextElement text:
                border = new Border
                {
                    Width = text.Width, Height = text.Height,
                    Child = new TextElementView
                    {
                        Element = text,
                        Width = text.Width,
                        Height = text.Height,
                        Effect = text.ShadowEffect,
                    },
                    Opacity = el.OpacityFactor,
                    OpacityMask = el.OpacityMaskBrush,
                    RenderTransformOrigin = new System.Windows.Point(0.5, 0.5),
                };
                break;
            case GroupElement group:
                var groupCanvas = new System.Windows.Controls.Canvas
                {
                    Width = group.Width,
                    Height = group.Height,
                };
                foreach (var child in group.Children)
                {
                    var childVisual = BuildElementVisual(child, false);
                    System.Windows.Controls.Canvas.SetLeft(childVisual, child.X);
                    System.Windows.Controls.Canvas.SetTop(childVisual, child.Y);
                    groupCanvas.Children.Add(childVisual);
                }
                border = new Border
                {
                    Width = group.Width, Height = group.Height,
                    Child = groupCanvas,
                    Opacity = el.OpacityFactor,
                    OpacityMask = el.OpacityMaskBrush,
                    Effect = el.ShadowEffect,
                    RenderTransformOrigin = new System.Windows.Point(0.5, 0.5),
                };
                break;
            default:
                throw new NotSupportedException("Unsupported element type for slice copy.");
        }
        if (applyFlip) border.RenderTransform = scale;
        border.Measure(new System.Windows.Size(el.Width, el.Height));
        border.Arrange(new Rect(0, 0, el.Width, el.Height));
        return border;
    }

    /// <summary>
    /// Render one element on its own at 1:1, including rotation, shadow and
    /// opacity masks. <paramref name="background"/> null keeps transparency.
    /// </summary>
    public static BitmapSource RenderElement(DesignElement el, System.Windows.Media.Color? background, double scale = 1.0)
    {
        if (el.Width < 1 || el.Height < 1)
            throw new ArgumentException("The element is empty.", nameof(el));
        if (scale <= 0) scale = 1.0;

        double rad = el.RotationAngle * Math.PI / 180.0;
        double cos = Math.Cos(rad), sin = Math.Sin(rad);
        double cx = el.Width / 2, cy = el.Height / 2;
        double minX = double.MaxValue, minY = double.MaxValue;
        double maxX = double.MinValue, maxY = double.MinValue;
        foreach (var (lx, ly) in new[]
                 {
                     (0.0, 0.0), (el.Width, 0.0), (el.Width, el.Height), (0.0, el.Height),
                 })
        {
            double x = el.X + cx + (lx - cx) * cos - (ly - cy) * sin;
            double y = el.Y + cy + (lx - cx) * sin + (ly - cy) * cos;
            minX = Math.Min(minX, x); minY = Math.Min(minY, y);
            maxX = Math.Max(maxX, x); maxY = Math.Max(maxY, y);
        }

        double pad = el.ShadowStyle == ShadowStyle.None
            ? 0
            : Math.Max(Math.Abs(el.ShadowX), Math.Abs(el.ShadowY)) + el.ShadowBlur + 2;
        var bounds = new Rect(minX - pad, minY - pad,
            Math.Max(1, maxX - minX + pad * 2), Math.Max(1, maxY - minY + pad * 2));

        int outW = Math.Max(1, (int)Math.Ceiling(bounds.Width * scale));
        int outH = Math.Max(1, (int)Math.Ceiling(bounds.Height * scale));

        var visual = BuildElementVisual(el, applyFlip: false);
        var transform = new TransformGroup();
        transform.Children.Add(new ScaleTransform(el.FlipScaleX, el.FlipScaleY, cx, cy));
        transform.Children.Add(new RotateTransform(el.RotationAngle, cx, cy));
        transform.Children.Add(new TranslateTransform(el.X, el.Y));
        transform.Children.Add(new TranslateTransform(-bounds.X, -bounds.Y));
        transform.Children.Add(new ScaleTransform(scale, scale));
        visual.RenderTransform = transform;

        var rtb = new RenderTargetBitmap(outW, outH, 96, 96, PixelFormats.Pbgra32);
        rtb.Render(visual);
        if (rtb.CanFreeze) rtb.Freeze();
        if (background is not System.Windows.Media.Color bg) return rtb;

        int stride = outW * 4;
        var src = new byte[stride * outH];
        rtb.CopyPixels(src, stride, 0);
        var flat = new byte[stride * outH];
        for (int i = 0; i < flat.Length; i += 4)
        {
            double alpha = src[i + 3] / 255.0;
            flat[i] = (byte)Math.Clamp(src[i] + bg.B * (1 - alpha), 0, 255);
            flat[i + 1] = (byte)Math.Clamp(src[i + 1] + bg.G * (1 - alpha), 0, 255);
            flat[i + 2] = (byte)Math.Clamp(src[i + 2] + bg.R * (1 - alpha), 0, 255);
            flat[i + 3] = 255;
        }

        var flattened = BitmapSource.Create(outW, outH, 96, 96, PixelFormats.Bgra32, null, flat, stride);
        flattened.Freeze();
        return flattened;
    }

    /// <summary>
    /// Render a canvas-space region from the element host (elements only — no
    /// pasteboard chrome, no checkerboard) into a bitmap. When
    /// <paramref name="background"/> is set the region is flattened onto that
    /// solid color; when null the background stays transparent (PNG alpha).
    /// </summary>
    public static BitmapSource RenderRegion(FrameworkElement elementHost, Rect region, System.Windows.Media.Color? background, double zoom = 1, double dpi = 96)
    {
        if (region.Width < 1 || region.Height < 1)
            throw new ArgumentException("Slice region is empty.", nameof(region));
        if (zoom <= 0) zoom = 1;

        double pxPerDip = zoom * dpi / 96.0;
        int outW = Math.Max(1, (int)Math.Round(region.Width * pxPerDip));
        int outH = Math.Max(1, (int)Math.Round(region.Height * pxPerDip));
        double dipW = outW * 96.0 / dpi;
        double dipH = outH * 96.0 / dpi;

        // Draw exactly the requested region instead of rendering the whole
        // pasteboard and cropping it. Floor/ceil cropping a fractional
        // artboard origin used to include a half-pixel strip above/left of
        // the canvas, which showed up as a white line in some WebP viewers.
        var brush = new VisualBrush(elementHost)
        {
            ViewboxUnits = BrushMappingMode.Absolute,
            Viewbox = region,
            Stretch = Stretch.Fill,
        };

        var dv = new DrawingVisual();
        using (var dc = dv.RenderOpen())
        {
            if (background is { } bg)
                dc.DrawRectangle(new SolidColorBrush(bg), null, new Rect(0, 0, dipW, dipH));
            dc.DrawRectangle(brush, null, new Rect(0, 0, dipW, dipH));
        }
        var rtb = new RenderTargetBitmap(outW, outH, dpi, dpi, PixelFormats.Pbgra32);
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
