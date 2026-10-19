using System.Windows;
using System.Collections.Generic;
using System.Windows.Media;
using System.Windows.Threading;
using YorteImpact.Models;

namespace YorteImpact.Controls;

/// <summary>
/// Which part of the selection chrome the pointer is over.
/// </summary>
public enum HandleKind
{
    None,
    Move,           // inside the box
    N, S, E, W,     // edge handles
    NW, NE, SW, SE, // corner handles
    Rotate,         // floating rotation handle
}

/// <summary>
/// Draws the Pick-tool selection chrome for one <see cref="DesignElement"/>:
/// a 1px animated dashed border ("marching ants"), 8 resize handles and a
/// rotation handle on a stem. Lightweight FrameworkElement (single OnRender,
/// no child visuals) sized to the full pasteboard; hit-testing is manual via
/// <see cref="HitTestHandle"/>. Coordinates are pasteboard DIPs.
/// </summary>
public class SelectionOverlay : FrameworkElement
{
    public static readonly DependencyProperty TargetProperty =
        DependencyProperty.Register(nameof(Target), typeof(DesignElement), typeof(SelectionOverlay),
            new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender, OnTargetChanged));

    public DesignElement? Target
    {
        get => (DesignElement?)GetValue(TargetProperty);
        set => SetValue(TargetProperty, value);
    }

    public double HandleSize { get; set; } = 10;
    public double RotateHandleOffset { get; set; } = 28;

    /// <summary>
    /// Pixel-hugging outline in pasteboard coords (unrotated), built by
    /// <see cref="Services.ContourService"/>. Null = fall back to the
    /// (rounded) bounding box.
    /// </summary>
    public static readonly DependencyProperty ContourProperty =
        DependencyProperty.Register(nameof(Contour), typeof(Geometry), typeof(SelectionOverlay),
            new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public Geometry? Contour
    {
        get => (Geometry?)GetValue(ContourProperty);
        set => SetValue(ContourProperty, value);
    }

    public static readonly DependencyProperty TargetsProperty =
        DependencyProperty.Register(nameof(Targets), typeof(IReadOnlyList<DesignElement>), typeof(SelectionOverlay),
            new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    /// <summary>All selected elements. More than one draws a grouped box without handles.</summary>
    public IReadOnlyList<DesignElement>? Targets
    {
        get => (IReadOnlyList<DesignElement>?)GetValue(TargetsProperty);
        set => SetValue(TargetsProperty, value);
    }

    private static readonly Brush Accent = new SolidColorBrush(Color.FromRgb(0x2E, 0x9B, 0xFF));
    private static readonly Brush HandleFill = Brushes.White;

    private readonly DispatcherTimer _antsTimer;
    private double _dashOffset;

    static SelectionOverlay()
    {
        if (Accent.CanFreeze) Accent.Freeze();
        if (HandleFill.CanFreeze) HandleFill.Freeze();
    }

    public SelectionOverlay()
    {
        // Marching-ants animation: advance the dash offset ~10x/sec, but only
        // while something is selected (timer sleeps otherwise).
        _antsTimer = new DispatcherTimer(DispatcherPriority.Render)
        {
            Interval = TimeSpan.FromMilliseconds(100),
        };
        _antsTimer.Tick += (_, _) =>
        {
            if (Target == null || !IsVisible) return;
            _dashOffset = (_dashOffset - 2) % 18;
            InvalidateVisual();
        };
        Loaded += (_, _) => UpdateTimer();
        Unloaded += (_, _) => _antsTimer.Stop();
    }

    private static void OnTargetChanged(DependencyObject d, DependencyPropertyChangedEventArgs e) =>
        ((SelectionOverlay)d).UpdateTimer();

    private void UpdateTimer()
    {
        if (!IsLoaded) return;
        if (Target != null && !_antsTimer.IsEnabled) _antsTimer.Start();
        else if (Target == null && _antsTimer.IsEnabled) _antsTimer.Stop();
    }

    /// <summary>Screen positions (pasteboard coords) of the 8 handles + rotate handle.</summary>
    public Dictionary<HandleKind, Point> GetHandlePositions()
    {
        var d = new Dictionary<HandleKind, Point>();
        var t = Target;
        if (t == null) return d;

        double cx = t.X + t.Width / 2, cy = t.Y + t.Height / 2;
        double rad = t.RotationAngle * Math.PI / 180.0;
        double cos = Math.Cos(rad), sin = Math.Sin(rad);

        Point localToCanvas(double lx, double ly)
        {
            // Local origin = element center; rotate then translate.
            return new Point(cx + lx * cos - ly * sin, cy + lx * sin + ly * cos);
        }

        double hw = t.Width / 2, hh = t.Height / 2;
        d[HandleKind.NW] = localToCanvas(-hw, -hh);
        d[HandleKind.N] = localToCanvas(0, -hh);
        d[HandleKind.NE] = localToCanvas(hw, -hh);
        d[HandleKind.E] = localToCanvas(hw, 0);
        d[HandleKind.SE] = localToCanvas(hw, hh);
        d[HandleKind.S] = localToCanvas(0, hh);
        d[HandleKind.SW] = localToCanvas(-hw, hh);
        d[HandleKind.W] = localToCanvas(-hw, 0);
        return d;
    }

    /// <summary>Manual hit-test: rotate handle first, then resize handles, then inside-box = Move.</summary>
    public HandleKind HitTestHandle(Point p)
    {
        var t = Target;
        if (t == null) return HandleKind.None;

        var positions = GetHandlePositions();
        double r = HandleSize / 2 + 3; // generous grab radius

        foreach (var k in new[] { HandleKind.NW, HandleKind.NE, HandleKind.SW, HandleKind.SE, HandleKind.N, HandleKind.S, HandleKind.E, HandleKind.W })
        {
            if (positions.TryGetValue(k, out var hp) && Distance(p, hp) <= r)
                return k;
        }

        if (IsInsideRotatedRect(p, t)) return HandleKind.Move;
        return HandleKind.None;
    }

    protected override void OnRender(DrawingContext dc)
    {
        base.OnRender(dc);
        if (Targets is { Count: > 1 } targets)
        {
            DrawMultiSelection(dc, targets);
            return;
        }

        var t = Target;
        if (t == null) return;

        // 1px marching ants: white dashed underlay + dark dashed overlay with
        // a half-period offset, both shifted every timer tick. Visible on any
        // background (white artboard, gray pasteboard, red squares alike).
        var dashes = new DoubleCollection { 5, 4 };
        var whitePen = new Pen(Brushes.White, 1)
        {
            DashStyle = new DashStyle(dashes, _dashOffset),
        };
        var darkPen = new Pen(Brushes.Black, 1)
        {
            DashStyle = new DashStyle(dashes, _dashOffset + 4.5),
        };

        double cx = t.X + t.Width / 2, cy = t.Y + t.Height / 2;
        var box = new Rect(t.X, t.Y, t.Width, t.Height);

        // Hug the real opaque shape; fall back to the (rounded) box.
        Geometry outline = Contour ?? FallbackBox(t);

        dc.PushTransform(new RotateTransform(t.RotationAngle, cx, cy));
        dc.DrawGeometry(null, whitePen, outline);
        dc.DrawGeometry(null, darkPen, outline);
        dc.Pop(); // back to canvas space for square handles

        var positions = GetHandlePositions();
        double hs = HandleSize;
        var handlePen = new Pen(Accent, 1.5);
        foreach (var kv in positions)
        {
            var p = kv.Value;
            if (kv.Key == HandleKind.Rotate)
            {
                dc.DrawEllipse(HandleFill, handlePen, p, hs / 2, hs / 2);
                dc.DrawEllipse(Accent, null, p, 2, 2);
            }
            else
            {
                dc.DrawRectangle(HandleFill, handlePen, new Rect(p.X - hs / 2, p.Y - hs / 2, hs, hs));
            }
        }
    }

    private void DrawMultiSelection(DrawingContext dc, IReadOnlyList<DesignElement> targets)
    {
        var itemPen = new Pen(new SolidColorBrush(Color.FromArgb(150, 46, 155, 255)), 1);
        var groupPen = new Pen(Accent, 1.5);
        itemPen.Freeze();
        groupPen.Freeze();

        Rect group = Rect.Empty;
        foreach (var t in targets)
        {
            Rect rotated = RotatedBounds(t);
            group = group.IsEmpty ? rotated : Rect.Union(group, rotated);

            dc.PushTransform(new RotateTransform(t.RotationAngle,
                t.X + t.Width / 2, t.Y + t.Height / 2));
            dc.DrawRectangle(null, itemPen, new Rect(t.X, t.Y, t.Width, t.Height));
            dc.Pop();
        }

        if (!group.IsEmpty) dc.DrawRectangle(null, groupPen, group);
    }

    private static Rect RotatedBounds(DesignElement t)
    {
        double cx = t.X + t.Width / 2, cy = t.Y + t.Height / 2;
        double rad = t.RotationAngle * Math.PI / 180.0;
        double cos = Math.Cos(rad), sin = Math.Sin(rad);
        double minX = double.MaxValue, minY = double.MaxValue;
        double maxX = double.MinValue, maxY = double.MinValue;
        foreach (var (lx, ly) in new[]
                 {
                     (0.0, 0.0), (t.Width, 0.0), (t.Width, t.Height), (0.0, t.Height),
                 })
        {
            double x = cx + (lx - t.Width / 2) * cos - (ly - t.Height / 2) * sin;
            double y = cy + (lx - t.Width / 2) * sin + (ly - t.Height / 2) * cos;
            minX = Math.Min(minX, x); minY = Math.Min(minY, y);
            maxX = Math.Max(maxX, x); maxY = Math.Max(maxY, y);
        }
        return new Rect(minX, minY, maxX - minX, maxY - minY);
    }

    private static double Distance(Point a, Point b)
    {
        double dx = a.X - b.X, dy = a.Y - b.Y;
        return Math.Sqrt(dx * dx + dy * dy);
    }

    private static Geometry FallbackBox(DesignElement t)
    {
        double fr = t switch
        {
            ShapeElement s => s.CornerRadius,
            ImageElement i => i.CornerRadius,
            _ => 0,
        };
        fr = Math.Clamp(fr, 0, Math.Min(t.Width, t.Height) / 2);
        return new RectangleGeometry(new Rect(t.X, t.Y, t.Width, t.Height), fr, fr);
    }

    private static bool IsInsideRotatedRect(Point p, DesignElement t)
    {
        // Inverse-rotate the point around the center, then AABB-test.
        double cx = t.X + t.Width / 2, cy = t.Y + t.Height / 2;
        double rad = -t.RotationAngle * Math.PI / 180.0;
        double cos = Math.Cos(rad), sin = Math.Sin(rad);
        double lx = (p.X - cx) * cos - (p.Y - cy) * sin;
        double ly = (p.X - cx) * sin + (p.Y - cy) * cos;
        return Math.Abs(lx) <= t.Width / 2 && Math.Abs(ly) <= t.Height / 2;
    }
}
