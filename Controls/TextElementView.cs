using System;
using System.ComponentModel;
using System.Globalization;
using System.Windows;
using System.Windows.Media;
using YorteImpact.Models;

namespace YorteImpact.Controls;

/// <summary>
/// Renders a <see cref="TextElement"/> with letter spacing, block-wide gradient
/// fill, RTL order and underline/strikethrough. Using a custom OnRender pass is
/// necessary because WPF's TextBlock has no character-spacing property.
/// </summary>
public class TextElementView : FrameworkElement
{
    public static readonly DependencyProperty ElementProperty =
        DependencyProperty.Register(nameof(Element), typeof(TextElement), typeof(TextElementView),
            new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender,
                OnElementChanged));

    public TextElement? Element
    {
        get => (TextElement?)GetValue(ElementProperty);
        set => SetValue(ElementProperty, value);
    }

    private TextElement? _subscribed;

    private static void OnElementChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var view = (TextElementView)d;
        if (view._subscribed != null)
            view._subscribed.PropertyChanged -= view.OnElementPropertyChanged;
        view._subscribed = e.NewValue as TextElement;
        if (view._subscribed != null)
            view._subscribed.PropertyChanged += view.OnElementPropertyChanged;
        view.InvalidateVisual();
    }

    private void OnElementPropertyChanged(object? sender, PropertyChangedEventArgs e) =>
        InvalidateVisual();

    protected override Size MeasureOverride(Size availableSize)
    {
        var te = Element;
        return te == null ? new Size(0, 0) : new Size(te.Width, te.Height);
    }

    protected override void OnRender(DrawingContext dc)
    {
        var te = Element;
        if (te == null || string.IsNullOrEmpty(te.Text)) return;

        double dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        var typeface = new Typeface(new FontFamily(te.FontFamilyName),
            te.FontStyle, te.FontWeight, FontStretches.Normal);
        var flow = te.IsRtl ? FlowDirection.RightToLeft : FlowDirection.LeftToRight;
        Brush brush = BuildBrush(te);
        Pen? borderPen = null;
        if (te.HasVisibleBorder)
        {
            var borderBrush = new SolidColorBrush(te.BorderColor);
            if (borderBrush.CanFreeze) borderBrush.Freeze();
            borderPen = new Pen(borderBrush, te.BorderThickness);
            if (borderPen.CanFreeze) borderPen.Freeze();
        }
        var culture = CultureInfo.CurrentUICulture;

        var lines = te.ComputeLayoutLines();
        double lineH = te.LineHeight;
        double y = 0;

        foreach (string line in lines)
        {
            if (line.Length > 0)
            {
                var ft = new FormattedText(line, culture, flow, typeface,
                    te.FontSize, brush, dpi);
                double spacing = te.LetterSpacing;
                double runW = ft.WidthIncludingTrailingWhitespace;
                if (spacing > 0.01 && line.Length > 1)
                    runW += spacing * (line.Length - 1);

                if (spacing > 0.01 && line.Length > 1)
                    DrawSpaced(dc, line, te, typeface, flow, brush, borderPen, dpi, y, culture);
                else
                {
                    double x = LineX(te, runW);
                    if (borderPen != null)
                        dc.DrawGeometry(brush, borderPen, ft.BuildGeometry(new Point(x, y)));
                    else
                        dc.DrawText(ft, new Point(x, y));
                }

                if (te.IsUnderline || te.IsStrikethrough)
                {
                    double x0 = LineX(te, runW);
                    double thickness = Math.Max(1, te.FontSize / 18.0);
                    var pen = new Pen(brush, thickness);
                    if (te.IsUnderline)
                    {
                        double uy = y + ft.Baseline + Math.Max(2, te.FontSize * 0.06);
                        dc.DrawLine(pen, new Point(x0, uy), new Point(x0 + runW, uy));
                    }
                    if (te.IsStrikethrough)
                    {
                        double sy = y + ft.Baseline - te.FontSize * 0.32;
                        dc.DrawLine(pen, new Point(x0, sy), new Point(x0 + runW, sy));
                    }
                }
            }
            y += lineH;
        }
    }

    /// <summary>Advance each glyph by its own width + tracking; RTL lays out right-to-left.</summary>
    private static void DrawSpaced(DrawingContext dc, string line, TextElement te,
        Typeface typeface, FlowDirection flow, Brush brush, Pen? borderPen, double dpi, double y,
        CultureInfo culture)
    {
        double spacing = te.LetterSpacing;
        var glyphs = new FormattedText[line.Length];
        double total = 0;
        for (int i = 0; i < line.Length; i++)
        {
            glyphs[i] = new FormattedText(line[i].ToString(), culture, flow, typeface,
                te.FontSize, brush, dpi);
            total += glyphs[i].WidthIncludingTrailingWhitespace;
        }
        total += spacing * (line.Length - 1);

        double x = LineX(te, total);
        double cum = 0;
        for (int i = 0; i < line.Length; i++)
        {
            double w = glyphs[i].WidthIncludingTrailingWhitespace;
            double offset = te.IsRtl ? total - cum - w : cum;
            var pt = new Point(x + offset, y);
            if (borderPen != null)
                dc.DrawGeometry(brush, borderPen, glyphs[i].BuildGeometry(pt));
            else
                dc.DrawText(glyphs[i], pt);
            cum += w + spacing;
        }
    }

    /// <summary>Physical alignment of a line inside the block.</summary>
    private static double LineX(TextElement te, double runW) => te.Alignment switch
    {
        TextAlign.Left => 0,
        TextAlign.Center => (te.Width - runW) / 2,
        _ => te.Width - runW,
    };

    /// <summary>Solid color, or one block-wide gradient across the element box.</summary>
    private static Brush BuildBrush(TextElement te)
    {
        if (!te.IsGradient)
        {
            var solid = new SolidColorBrush(te.TextColor1);
            if (solid.CanFreeze) solid.Freeze();
            return solid;
        }

        double rad = (te.TextGradientAngle - 90) * Math.PI / 180.0;
        var center = new Point(te.Width / 2, te.Height / 2);
        double len = Math.Abs(te.Width * Math.Cos(rad)) + Math.Abs(te.Height * Math.Sin(rad));
        if (len < 1e-6) len = 1;
        var start = new Point(center.X - Math.Cos(rad) * len / 2, center.Y - Math.Sin(rad) * len / 2);
        var end = new Point(center.X + Math.Cos(rad) * len / 2, center.Y + Math.Sin(rad) * len / 2);

        var brush = new LinearGradientBrush(te.TextColor1, te.TextColor2, start, end)
        {
            MappingMode = BrushMappingMode.Absolute,
        };
        if (brush.CanFreeze) brush.Freeze();
        return brush;
    }
}
