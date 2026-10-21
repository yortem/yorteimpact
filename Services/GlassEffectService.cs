using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using YorteImpact.Models;
using YorteImpact.ViewModels;

namespace YorteImpact.Services;

/// <summary>
/// Builds the transparent glass backdrop by rendering only the elements below
/// the glass object, then blurring and mapping that sample into the object.
/// </summary>
public static class GlassEffectService
{
    public static void Update(DesignElement element, GlassEffect effect,
        DesignDocument doc, ItemsControl host)
    {
        if (!effect.IsEnabled)
        {
            effect.BackdropBrush = null;
            return;
        }
        if (host.ActualWidth <= 0 || host.ActualHeight <= 0) return;

        host.UpdateLayout();
        if (host.ItemContainerGenerator.ContainerFromItem(element) is not UIElement glassContainer)
            return;

        var hidden = new List<(UIElement Element, double Opacity)>();
        try
        {
            foreach (var other in doc.Elements)
            {
                if (other.ZIndex < element.ZIndex) continue;
                if (host.ItemContainerGenerator.ContainerFromItem(other) is not UIElement container)
                    continue;
                if (container.Visibility != Visibility.Visible || container.Opacity <= 0) continue;
                hidden.Add((container, container.Opacity));
                // Opacity keeps the layout and text focus intact while the glass
                // samples the content below it.
                container.Opacity = 0;
            }

            host.UpdateLayout();

            double pad = Math.Clamp(effect.Blur * 2 + 8, 8, 160);
            int outWidth = Math.Max(1, (int)Math.Ceiling(element.Width + pad * 2));
            int outHeight = Math.Max(1, (int)Math.Ceiling(element.Height + pad * 2));

            var transform = new TransformGroup();
            transform.Children.Add(new RotateTransform(-element.RotationAngle, element.Center.X, element.Center.Y));
            if (element.FlipHorizontal || element.FlipVertical)
                transform.Children.Add(new ScaleTransform(element.FlipScaleX, element.FlipScaleY,
                    element.Center.X, element.Center.Y));
            transform.Children.Add(new TranslateTransform(-element.X + pad, -element.Y + pad));

            // Render the host directly instead of through a VisualBrush: a
            // VisualBrush can keep the previously rendered pixels after the
            // elements above the glass are hidden, which made the glass sample
            // its own fill. RenderTransform keeps the sample in glass-local
            // space, including rotation and flip.
            BitmapSource rendered;
            var oldTransform = host.RenderTransform;
            try
            {
                host.RenderTransform = transform;
                host.UpdateLayout();
                var raw = new RenderTargetBitmap(outWidth, outHeight, 96, 96, PixelFormats.Pbgra32);
                raw.Render(host);
                raw.Freeze();
                rendered = raw;
            }
            finally
            {
                host.RenderTransform = oldTransform;
                host.UpdateLayout();
            }

            if (!doc.CanvasTransparent)
            {
                var paper = new DrawingVisual();
                using (var dc = paper.RenderOpen())
                {
                    dc.DrawRectangle(new SolidColorBrush(doc.CanvasColor), null,
                        new Rect(0, 0, outWidth, outHeight));
                    dc.DrawImage(rendered, new Rect(0, 0, outWidth, outHeight));
                }
                var flat = new RenderTargetBitmap(outWidth, outHeight, 96, 96, PixelFormats.Pbgra32);
                flat.Render(paper);
                flat.Freeze();
                rendered = flat;
            }

            BitmapSource blurred = Blur(rendered, effect.Blur);
            double inset = Math.Min(element.Width, element.Height) * (effect.Refraction / 100.0);
            inset = Math.Clamp(inset, 0, Math.Min(element.Width, element.Height) * 0.25);
            var view = new Rect(
                pad + inset,
                pad + inset,
                Math.Max(1, element.Width - inset * 2),
                Math.Max(1, element.Height - inset * 2));

            var brush = new ImageBrush(blurred)
            {
                Stretch = Stretch.Fill,
                ViewboxUnits = BrushMappingMode.Absolute,
                Viewbox = view,
                AlignmentX = AlignmentX.Left,
                AlignmentY = AlignmentY.Top,
            };
            brush.Freeze();
            effect.BackdropBrush = brush;
        }
        catch
        {
            effect.BackdropBrush = null;
        }
        finally
        {
            foreach (var (target, opacity) in hidden)
                target.Opacity = opacity;
            host.UpdateLayout();
        }
    }

    private static BitmapSource Blur(BitmapSource source, double radius)
    {
        if (radius <= 0.1) return source;

        var image = new Image
        {
            Source = source,
            Width = source.PixelWidth,
            Height = source.PixelHeight,
            Stretch = Stretch.Fill,
            Effect = new BlurEffect
            {
                Radius = radius,
                KernelType = KernelType.Gaussian,
                RenderingBias = RenderingBias.Performance,
            },
        };
        image.Measure(new Size(source.PixelWidth, source.PixelHeight));
        image.Arrange(new Rect(0, 0, source.PixelWidth, source.PixelHeight));

        var result = new RenderTargetBitmap(source.PixelWidth, source.PixelHeight,
            source.DpiX, source.DpiY, PixelFormats.Pbgra32);
        result.Render(image);
        result.Freeze();
        return result;
    }
}
