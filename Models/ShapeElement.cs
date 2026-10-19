using System.Windows;
using System.Windows.Media;

namespace YorteImpact.Models;

/// <summary>
/// Gradient fill modes a shape can
/// use: a straight <see cref="Linear"/> ramp (angle driven), a true circular
/// <see cref="Radial"/> burst from the center, and an <see cref="Elliptical"/>
/// ramp that fills the element box.
/// </summary>
public enum GradientType
{
    Linear,
    Radial,
    Elliptical,
}

/// <summary>Geometry drawn inside the element's box.</summary>
public enum ShapeKind
{
    Rectangle,
    Ellipse,
    Triangle,
    Polygon,
}

/// <summary>
/// Vector element with a fill, border and corner radius. The geometry can be a
/// rectangle, ellipse, triangle or a custom polygon; the template binds to the
/// prebuilt geometry and brushes so XAML stays converter-free.
/// </summary>
public class ShapeElement : DesignElement
{
    private double _cornerRadius = 12;
    private ShapeKind _kind = ShapeKind.Rectangle;
    private Color _fillColor1 = Color.FromRgb(0x3B, 0x82, 0xF6);
    private Color _fillColor2 = Color.FromRgb(0x93, 0xC5, 0xFD);
    private bool _isGradient = true;
    private GradientType _gradientType = GradientType.Linear;
    private double _gradientAngle = 135;
    private Color _strokeColor = Color.FromRgb(0x1E, 0x3A, 0x8A);
    private double _strokeThickness = 2;

    private Brush _fillBrush = Brushes.CornflowerBlue;
    private Brush _borderBrush = Brushes.Navy;
    private Geometry _shapeGeometry = Geometry.Empty;

    private readonly List<Point> _polygonPoints = new();

    private static readonly Point[] DefaultPolygon =
    {
        new(0.5, 0), new(1, 0.38), new(0.82, 1), new(0.18, 1), new(0, 0.38),
    };

    public ShapeElement()
    {
        RebuildBrushes();
        RebuildGeometry();
        // Radial uses absolute radii (so it stays a true circle), which means
        // the brush has to follow the element size, not just its colors.
        PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(Width) or nameof(Height))
            {
                RebuildBrushes();
                RebuildGeometry();
            }
        };
    }

    public ShapeKind Kind
    {
        get => _kind;
        set
        {
            if (Set(ref _kind, value))
            {
                Raise(nameof(UsesCornerRadius));
                RebuildGeometry();
            }
        }
    }

    public bool UsesCornerRadius => _kind == ShapeKind.Rectangle;

    /// <summary>Normalized (0..1) polygon points, used when <see cref="Kind"/> is Polygon.</summary>
    public IReadOnlyList<Point> PolygonPoints => _polygonPoints;

    public void SetPolygonPoints(IEnumerable<Point> points)
    {
        _polygonPoints.Clear();
        _polygonPoints.AddRange(points);
        RebuildGeometry();
    }

    public double CornerRadius
    {
        get => _cornerRadius;
        set
        {
            if (Set(ref _cornerRadius, Math.Max(0, value))) RebuildGeometry();
        }
    }

    public Color FillColor1
    {
        get => _fillColor1;
        set { if (Set(ref _fillColor1, value)) RebuildBrushes(); }
    }

    public Color FillColor2
    {
        get => _fillColor2;
        set { if (Set(ref _fillColor2, value)) RebuildBrushes(); }
    }

    public bool IsGradient
    {
        get => _isGradient;
        set { if (Set(ref _isGradient, value)) RebuildBrushes(); }
    }

    /// <summary>Which fill method is used when <see cref="IsGradient"/> is true.</summary>
    public GradientType GradientType
    {
        get => _gradientType;
        set { if (Set(ref _gradientType, value)) RebuildBrushes(); }
    }

    /// <summary>Gradient direction in degrees (Linear only; 0 = left-to-right).</summary>
    public double GradientAngle
    {
        get => _gradientAngle;
        set { if (Set(ref _gradientAngle, value)) RebuildBrushes(); }
    }

    public Color StrokeColor
    {
        get => _strokeColor;
        set { if (Set(ref _strokeColor, value)) RebuildBrushes(); }
    }

    public double StrokeThickness
    {
        get => _strokeThickness;
        set { if (Set(ref _strokeThickness, Math.Max(0, value))) RebuildBrushes(); }
    }

    public Brush FillBrush
    {
        get => _fillBrush;
        private set => Set(ref _fillBrush, value);
    }

    public Brush BorderBrush
    {
        get => _borderBrush;
        private set => Set(ref _borderBrush, value);
    }

    /// <summary>Geometry bound by the template; rebuilt whenever its inputs change.</summary>
    public Geometry ShapeGeometry
    {
        get => _shapeGeometry;
        private set => Set(ref _shapeGeometry, value);
    }

    private void RebuildGeometry()
    {
        double w = Math.Max(1, Width);
        double h = Math.Max(1, Height);
        Geometry geometry;

        switch (_kind)
        {
            case ShapeKind.Ellipse:
                geometry = new EllipseGeometry(new Rect(0, 0, w, h));
                break;

            case ShapeKind.Triangle:
            {
                var sg = new StreamGeometry();
                using (var ctx = sg.Open())
                {
                    ctx.BeginFigure(new Point(w / 2, 0), true, true);
                    ctx.LineTo(new Point(w, h), true, false);
                    ctx.LineTo(new Point(0, h), true, false);
                }
                sg.Freeze();
                geometry = sg;
                break;
            }

            case ShapeKind.Polygon:
            {
                IReadOnlyList<Point> pts = _polygonPoints.Count >= 3 ? _polygonPoints : DefaultPolygon;
                var pg = new StreamGeometry();
                using (var ctx = pg.Open())
                {
                    ctx.BeginFigure(new Point(pts[0].X * w, pts[0].Y * h), true, true);
                    for (int i = 1; i < pts.Count; i++)
                        ctx.LineTo(new Point(pts[i].X * w, pts[i].Y * h), true, false);
                }
                pg.Freeze();
                geometry = pg;
                break;
            }

            default:
            {
                double r = Math.Min(_cornerRadius, Math.Min(w, h) / 2);
                geometry = new RectangleGeometry(new Rect(0, 0, w, h), r, r);
                break;
            }
        }

        if (geometry.CanFreeze) geometry.Freeze();
        ShapeGeometry = geometry;
    }


    private void RebuildBrushes()
    {
        FillBrush = IsGradient ? BuildGradient() : new SolidColorBrush(FillColor1);
        BorderBrush = new SolidColorBrush(StrokeColor);
        foreach (var b in new[] { FillBrush, BorderBrush })
            if (b.CanFreeze) b.Freeze(); // perf: frozen brushes render faster
    }

    private Brush BuildGradient()
    {
        var stops = new GradientStopCollection
        {
            new GradientStop(FillColor1, 0),
            new GradientStop(FillColor2, 1),
        };

        switch (GradientType)
        {
            case GradientType.Radial:
            {
                // Absolute radii keep the burst circular on non-square elements.
                double cx = Width / 2, cy = Height / 2;
                double radius = Math.Max(0.01, Math.Sqrt(cx * cx + cy * cy));
                return new RadialGradientBrush(stops)
                {
                    MappingMode = BrushMappingMode.Absolute,
                    Center = new Point(cx, cy),
                    GradientOrigin = new Point(cx, cy),
                    RadiusX = radius,
                    RadiusY = radius,
                };
            }
            case GradientType.Elliptical:
                return new RadialGradientBrush(stops)
                {
                    Center = new Point(0.5, 0.5),
                    GradientOrigin = new Point(0.5, 0.5),
                    RadiusX = 0.5,
                    RadiusY = 0.5,
                };
            default: // Linear
            {
                double rad = (GradientAngle - 90) * Math.PI / 180.0;
                var dx = Math.Cos(rad) / 2.0;
                var dy = Math.Sin(rad) / 2.0;
                return new LinearGradientBrush(stops)
                {
                    StartPoint = new Point(0.5 - dx, 0.5 - dy),
                    EndPoint = new Point(0.5 + dx, 0.5 + dy),
                };
            }
        }
    }

    /// <summary>
    /// Gradient position (0..1) at a point in the element's LOCAL unrotated
    /// frame. Mirrors <see cref="BuildGradient"/> exactly so the eyedropper
    /// samples the same color that is drawn.
    /// </summary>
    public double GradientT(double localX, double localY)
    {
        double cx = Width / 2, cy = Height / 2;
        switch (GradientType)
        {
            case GradientType.Radial:
            {
                double radius = Math.Sqrt(cx * cx + cy * cy);
                if (radius < 1e-9) return 0;
                double d = Math.Sqrt((localX - cx) * (localX - cx) + (localY - cy) * (localY - cy));
                return d / radius;
            }
            case GradientType.Elliptical:
            {
                double nx = cx < 1e-9 ? 0 : (localX - cx) / cx;
                double ny = cy < 1e-9 ? 0 : (localY - cy) / cy;
                return Math.Sqrt(nx * nx + ny * ny);
            }
            default:
            {
                double rad = (GradientAngle - 90) * Math.PI / 180.0;
                double dx = Math.Cos(rad) / 2, dy = Math.Sin(rad) / 2;
                double px = localX / Width - (0.5 - dx);
                double py = localY / Height - (0.5 - dy);
                double len2 = 4 * (dx * dx + dy * dy);
                return len2 < 1e-9 ? 0 : (px * 2 * dx + py * 2 * dy) / len2;
            }
        }
    }

    public override void CopyStyleTo(DesignElement target)
    {
        base.CopyStyleTo(target);
        if (target is not ShapeElement shape) return;
        shape.FillColor1 = FillColor1;
        shape.FillColor2 = FillColor2;
        shape.IsGradient = IsGradient;
        shape.GradientType = GradientType;
        shape.GradientAngle = GradientAngle;
        shape.StrokeColor = StrokeColor;
        shape.StrokeThickness = StrokeThickness;
        shape.CornerRadius = CornerRadius;
    }

    public override DesignElement Clone()
    {
        var c = new ShapeElement
        {
            X = X, Y = Y,
            Width = Width, Height = Height,
            RotationAngle = RotationAngle,
            FlipHorizontal = FlipHorizontal, FlipVertical = FlipVertical,
            Opacity = Opacity,
            SoftEdge = SoftEdge,
            ShadowStyle = ShadowStyle,
            ShadowX = ShadowX, ShadowY = ShadowY,
            ShadowBlur = ShadowBlur, ShadowOpacity = ShadowOpacity, ShadowColor = ShadowColor,
            UseOpacityGradient = UseOpacityGradient,
            OpacityDirection = OpacityDirection,
            OpacityStart = OpacityStart,
            OpacityEnd = OpacityEnd,
            OpacityStrength = OpacityStrength,
            CornerRadius = CornerRadius,
            Kind = Kind,
            FillColor1 = FillColor1, FillColor2 = FillColor2,
            IsGradient = IsGradient, GradientType = GradientType, GradientAngle = GradientAngle,
            StrokeColor = StrokeColor, StrokeThickness = StrokeThickness,
        };
        c.SetPolygonPoints(_polygonPoints);
        return c;
    }
}
