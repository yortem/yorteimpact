using System.Globalization;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Media;
using YorteImpact.Models;

namespace YorteImpact.Controls;

/// <summary>
/// Non-interactive 12-column website grid plus a live size/position badge for
/// the selected object. Toggled from the grid button at the bottom-right.
/// Coordinates are pasteboard DIPs; the pasteboard zoom transform scales it.
/// </summary>
public class GuideOverlay : FrameworkElement
{
    public static readonly DependencyProperty ArtboardXProperty = Register(nameof(ArtboardX));
    public static readonly DependencyProperty ArtboardYProperty = Register(nameof(ArtboardY));
    public static readonly DependencyProperty ArtboardWidthProperty = Register(nameof(ArtboardWidth));
    public static readonly DependencyProperty ArtboardHeightProperty = Register(nameof(ArtboardHeight));
    public static readonly DependencyProperty ColumnsProperty =
        DependencyProperty.Register(nameof(Columns), typeof(int), typeof(GuideOverlay),
            new FrameworkPropertyMetadata(12, FrameworkPropertyMetadataOptions.AffectsRender,
                null, (_, value) => Math.Clamp((int)value, 2, 24)));
    public static readonly DependencyProperty MarginProperty =
        DependencyProperty.Register(nameof(Margin), typeof(double), typeof(GuideOverlay),
            new FrameworkPropertyMetadata(80.0, FrameworkPropertyMetadataOptions.AffectsRender,
                null, (_, value) => Math.Clamp((double)value, 0, 400)));
    public static readonly DependencyProperty GutterProperty =
        DependencyProperty.Register(nameof(Gutter), typeof(double), typeof(GuideOverlay),
            new FrameworkPropertyMetadata(24.0, FrameworkPropertyMetadataOptions.AffectsRender,
                null, (_, value) => Math.Clamp((double)value, 0, 200)));
    public static readonly DependencyProperty GridOpacityProperty =
        DependencyProperty.Register(nameof(GridOpacity), typeof(double), typeof(GuideOverlay),
            new FrameworkPropertyMetadata(100.0, FrameworkPropertyMetadataOptions.AffectsRender,
                null, (_, value) => Math.Clamp((double)value, 0, 100)));
    public static readonly DependencyProperty ShowCenterLineProperty =
        DependencyProperty.Register(nameof(ShowCenterLine), typeof(bool), typeof(GuideOverlay),
            new FrameworkPropertyMetadata(true, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty SelectedProperty =
        DependencyProperty.Register(nameof(Selected), typeof(DesignElement), typeof(GuideOverlay),
            new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public double ArtboardX { get => (double)GetValue(ArtboardXProperty); set => SetValue(ArtboardXProperty, value); }
    public double ArtboardY { get => (double)GetValue(ArtboardYProperty); set => SetValue(ArtboardYProperty, value); }
    public double ArtboardWidth { get => (double)GetValue(ArtboardWidthProperty); set => SetValue(ArtboardWidthProperty, value); }
    public double ArtboardHeight { get => (double)GetValue(ArtboardHeightProperty); set => SetValue(ArtboardHeightProperty, value); }
    public int Columns { get => (int)GetValue(ColumnsProperty); set => SetValue(ColumnsProperty, value); }
    public double Margin { get => (double)GetValue(MarginProperty); set => SetValue(MarginProperty, value); }
    public double Gutter { get => (double)GetValue(GutterProperty); set => SetValue(GutterProperty, value); }
    public double GridOpacity { get => (double)GetValue(GridOpacityProperty); set => SetValue(GridOpacityProperty, value); }
    public bool ShowCenterLine { get => (bool)GetValue(ShowCenterLineProperty); set => SetValue(ShowCenterLineProperty, value); }
    public DesignElement? Selected { get => (DesignElement?)GetValue(SelectedProperty); set => SetValue(SelectedProperty, value); }

    private static DependencyProperty Register(string name) =>
        DependencyProperty.Register(name, typeof(double), typeof(GuideOverlay),
            new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsRender));

    protected override void OnRender(DrawingContext dc)
    {
        base.OnRender(dc);
        double width = ArtboardWidth, height = ArtboardHeight;
        if (width < 1 || height < 1) return;

        var art = new Rect(ArtboardX, ArtboardY, width, height);
        DrawGrid(dc, art, Columns, Margin, Gutter, GridOpacity, ShowCenterLine);
        DrawSelectionBadge(dc);
    }

    private static void DrawGrid(DrawingContext dc, Rect art, int columns,
        double margin, double gutter, double opacity, bool showCenterLine)
    {
        var metrics = GridMetrics(art, columns, margin, gutter);
        if (metrics.ColumnWidth < 1) return;

        double alpha = Math.Clamp(opacity, 0, 100) / 100.0;
        byte A(byte value) => (byte)Math.Round(value * alpha);
        var fill = new SolidColorBrush(Color.FromArgb(A(18), 46, 155, 255));
        var line = new Pen(new SolidColorBrush(Color.FromArgb(A(85), 46, 155, 255)), 1);
        fill.Freeze();
        line.Freeze();

        for (int i = 0; i < columns; i++)
        {
            double x = art.X + metrics.Margin + i * (metrics.ColumnWidth + metrics.Gutter);
            dc.DrawRoundedRectangle(fill, null,
                new Rect(x, art.Y + metrics.Margin, metrics.ColumnWidth,
                    Math.Max(1, art.Height - metrics.Margin * 2)),
                2, 2);
        }

        var marginPen = new Pen(new SolidColorBrush(Color.FromArgb(A(100), 46, 155, 255)), 1)
        {
            DashStyle = new DashStyle(new DoubleCollection { 5, 4 }, 0),
        };
        marginPen.Freeze();
        dc.DrawRectangle(null, marginPen,
            new Rect(art.X + metrics.Margin, art.Y + metrics.Margin,
                Math.Max(1, art.Width - metrics.Margin * 2),
                Math.Max(1, art.Height - metrics.Margin * 2)));

        if (showCenterLine)
        {
            var centerPen = new Pen(new SolidColorBrush(Color.FromArgb(A(110), 255, 176, 32)), 1)
            {
                DashStyle = new DashStyle(new DoubleCollection { 3, 4 }, 0),
            };
            centerPen.Freeze();
            double cx = art.X + art.Width / 2;
            dc.DrawLine(centerPen, new Point(cx, art.Y + metrics.Margin),
                new Point(cx, art.Bottom - metrics.Margin));
        }
        dc.DrawLine(line, new Point(art.X + metrics.Margin, art.Y + metrics.Margin),
            new Point(art.X + metrics.Margin, art.Bottom - metrics.Margin));
        dc.DrawLine(line, new Point(art.Right - metrics.Margin, art.Y + metrics.Margin),
            new Point(art.Right - metrics.Margin, art.Bottom - metrics.Margin));
    }

    public static (double Margin, double Gutter, double ColumnWidth) GridMetrics(
        Rect art, int columns, double margin, double gutter)
    {
        columns = Math.Clamp(columns, 2, 24);
        margin = Math.Clamp(margin, 0, Math.Max(0, art.Width / 2 - 1));
        gutter = Math.Clamp(gutter, 0, Math.Max(0, art.Width / 4));
        double content = art.Width - margin * 2 - gutter * (columns - 1);
        return (margin, gutter, content / columns);
    }

    /// <summary>Vertical column edges used by Shift snapping to the grid.</summary>
    public IEnumerable<double> GetVerticalLines()
    {
        var art = new Rect(ArtboardX, ArtboardY, ArtboardWidth, ArtboardHeight);
        var metrics = GridMetrics(art, Columns, Margin, Gutter);
        if (metrics.ColumnWidth < 1) yield break;

        yield return art.X + metrics.Margin;
        for (int i = 0; i < Columns; i++)
        {
            double x = art.X + metrics.Margin + i * (metrics.ColumnWidth + metrics.Gutter);
            yield return x;
            yield return x + metrics.ColumnWidth;
        }
        if (ShowCenterLine) yield return art.X + art.Width / 2;
    }

    /// <summary>Top/bottom content margins used by Shift snapping to the grid.</summary>
    public IEnumerable<double> GetHorizontalLines()
    {
        var art = new Rect(ArtboardX, ArtboardY, ArtboardWidth, ArtboardHeight);
        var metrics = GridMetrics(art, Columns, Margin, Gutter);
        yield return art.Y + metrics.Margin;
        yield return art.Bottom - metrics.Margin;
    }

    private void DrawSelectionBadge(DrawingContext dc)
    {
        if (Selected is not { } sel) return;

        string text = $"{sel.Width:0} × {sel.Height:0} px   L{sel.X - ArtboardX:0}  T{sel.Y - ArtboardY:0}";
        var typeface = new Typeface(new FontFamily("Segoe UI"),
            FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal);
        var formatted = new FormattedText(
            text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
            typeface, 11, Brushes.White, VisualTreeHelper.GetDpi(this).PixelsPerDip);

        const double padX = 8, padY = 4;
        double badgeW = formatted.Width + padX * 2;
        double badgeH = formatted.Height + padY * 2;
        double x = sel.X + sel.Width / 2 - badgeW / 2;
        double y = sel.Y - badgeH - 8;
        if (y < 4) y = sel.Y + sel.Height + 8;

        x = Math.Clamp(x, 4, Math.Max(4, ActualWidth - badgeW - 4));
        y = Math.Clamp(y, 4, Math.Max(4, ActualHeight - badgeH - 4));

        var background = new SolidColorBrush(Color.FromArgb(235, 11, 18, 27));
        var border = new Pen(new SolidColorBrush(Color.FromRgb(46, 155, 255)), 1);
        background.Freeze();
        border.Freeze();
        dc.DrawRoundedRectangle(background, border,
            new Rect(x, y, badgeW, badgeH), 4, 4);
        dc.DrawText(formatted, new Point(x + padX, y + padY));
    }
}
