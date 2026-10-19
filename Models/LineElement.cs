using System.Windows;
using System.Windows.Media;

namespace YorteImpact.Models;

public enum LinePattern
{
    Solid,
    Dashed,
    Dotted,
}

public enum LineEndpointStyle
{
    Flat,
    OpenArrow,
    FilledTriangle,
    Round,
    Square,
}

/// <summary>A stroked, open vector path with optional endpoint markers.</summary>
public sealed class LineElement : DesignElement
{
    private Color _strokeColor = Color.FromRgb(0x2E, 0x9B, 0xFF);
    private double _strokeThickness = 4;
    private LinePattern _pattern;
    private LineEndpointStyle _startCap;
    private LineEndpointStyle _endCap;
    private bool _isFreehand;
    private bool _isPolyline;
    private double _curveStrength = 50;
    private Brush _strokeBrush = Brushes.DodgerBlue;
    private DoubleCollection? _dashArray;
    private Geometry _pathGeometry = Geometry.Empty;
    private Geometry _startMarkerGeometry = Geometry.Empty;
    private Geometry _endMarkerGeometry = Geometry.Empty;
    private readonly List<Point> _pathPoints = new();

    public LineElement()
    {
        RebuildStrokeBrush();
        RebuildDashArray();
        RebuildGeometry();
        PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(Width) or nameof(Height))
                RebuildGeometry();
        };
    }

    public Color StrokeColor
    {
        get => _strokeColor;
        set { if (Set(ref _strokeColor, value)) RebuildStrokeBrush(); }
    }

    public double StrokeThickness
    {
        get => _strokeThickness;
        set
        {
            if (!Set(ref _strokeThickness, Math.Clamp(value, 0.25, 200))) return;
            RebuildDashArray();
            RebuildGeometry();
        }
    }

    public LinePattern Pattern
    {
        get => _pattern;
        set { if (Set(ref _pattern, value)) RebuildDashArray(); }
    }

    public LineEndpointStyle StartCap
    {
        get => _startCap;
        set
        {
            if (!Set(ref _startCap, value)) return;
            Raise(nameof(StrokeStartLineCap));
            RebuildGeometry();
        }
    }

    public LineEndpointStyle EndCap
    {
        get => _endCap;
        set
        {
            if (!Set(ref _endCap, value)) return;
            Raise(nameof(StrokeEndLineCap));
            RebuildGeometry();
        }
    }

    public bool IsFreehand
    {
        get => _isFreehand;
        set { if (Set(ref _isFreehand, value)) RebuildGeometry(); }
    }

    public bool IsPolyline
    {
        get => _isPolyline;
        set => Set(ref _isPolyline, value);
    }

    /// <summary>0 keeps freehand points sharp; 100 applies the strongest spline smoothing.</summary>
    public double CurveStrength
    {
        get => _curveStrength;
        set { if (Set(ref _curveStrength, Math.Clamp(value, 0, 100))) RebuildGeometry(); }
    }

    public IReadOnlyList<Point> PathPoints => _pathPoints;
    public Brush StrokeBrush { get => _strokeBrush; private set => Set(ref _strokeBrush, value); }
    public DoubleCollection? DashArray { get => _dashArray; private set => Set(ref _dashArray, value); }
    public Geometry PathGeometry { get => _pathGeometry; private set => Set(ref _pathGeometry, value); }
    public Geometry StartMarkerGeometry { get => _startMarkerGeometry; private set => Set(ref _startMarkerGeometry, value); }
    public Geometry EndMarkerGeometry { get => _endMarkerGeometry; private set => Set(ref _endMarkerGeometry, value); }
    public PenLineCap StrokeStartLineCap => PenLineCap.Flat;
    public PenLineCap StrokeEndLineCap => PenLineCap.Flat;

    public void SetPathPoints(IEnumerable<Point> normalizedPoints)
    {
        _pathPoints.Clear();
        _pathPoints.AddRange(normalizedPoints.Select(p => new Point(
            Math.Clamp(p.X, 0, 1), Math.Clamp(p.Y, 0, 1))));
        RebuildGeometry();
    }

    private void RebuildStrokeBrush()
    {
        var brush = new SolidColorBrush(StrokeColor);
        if (brush.CanFreeze) brush.Freeze();
        StrokeBrush = brush;
    }

    private void RebuildDashArray()
    {
        if (Pattern == LinePattern.Solid)
        {
            DashArray = null;
            return;
        }

        var dash = Pattern == LinePattern.Dashed
            ? new DoubleCollection { 3, 2 }
            : new DoubleCollection { 0.01, 1.5 };
        dash.Freeze();
        DashArray = dash;
    }

    private void RebuildGeometry()
    {
        if (_pathPoints.Count < 2)
        {
            PathGeometry = Geometry.Empty;
            StartMarkerGeometry = Geometry.Empty;
            EndMarkerGeometry = Geometry.Empty;
            return;
        }

        Point Local(Point p) => new(p.X * Math.Max(1, Width), p.Y * Math.Max(1, Height));
        var geometry = new StreamGeometry();
        using (var context = geometry.Open())
        {
            var first = Local(_pathPoints[0]);
            context.BeginFigure(first, isFilled: false, isClosed: false);

            if (!IsFreehand || CurveStrength <= 0 || _pathPoints.Count == 2)
            {
                for (int i = 1; i < _pathPoints.Count; i++)
                    context.LineTo(Local(_pathPoints[i]), isStroked: true, isSmoothJoin: false);
            }
            else
            {
                double tension = CurveStrength / 100.0;
                for (int i = 0; i < _pathPoints.Count - 1; i++)
                {
                    Point before = Local(_pathPoints[Math.Max(0, i - 1)]);
                    Point start = Local(_pathPoints[i]);
                    Point end = Local(_pathPoints[i + 1]);
                    Point after = Local(_pathPoints[Math.Min(_pathPoints.Count - 1, i + 2)]);
                    var control1 = new Point(
                        start.X + (end.X - before.X) * tension / 6,
                        start.Y + (end.Y - before.Y) * tension / 6);
                    var control2 = new Point(
                        end.X - (after.X - start.X) * tension / 6,
                        end.Y - (after.Y - start.Y) * tension / 6);
                    context.BezierTo(control1, control2, end, isStroked: true, isSmoothJoin: true);
                }
            }
        }

        if (geometry.CanFreeze) geometry.Freeze();
        PathGeometry = geometry;
        StartMarkerGeometry = BuildMarkerGeometry(StartCap, atStart: true);
        EndMarkerGeometry = BuildMarkerGeometry(EndCap, atStart: false);
    }

    private Geometry BuildMarkerGeometry(LineEndpointStyle cap, bool atStart)
    {
        if (cap == LineEndpointStyle.Flat) return Geometry.Empty;

        int last = _pathPoints.Count - 1;
        Point first = ToLocal(_pathPoints[0]);
        Point second = ToLocal(_pathPoints[1]);
        Point penultimate = ToLocal(_pathPoints[last - 1]);
        Point end = ToLocal(_pathPoints[last]);
        Point tip = atStart ? first : end;

        Vector outward;
        if (IsFreehand && CurveStrength > 0 && _pathPoints.Count > 2)
        {
            double tension = CurveStrength / 100.0;
            if (atStart)
            {
                var control = new Point(
                    first.X + (second.X - first.X) * tension / 6,
                    first.Y + (second.Y - first.Y) * tension / 6);
                outward = first - control;
                if (outward.Length < 0.001) outward = first - second;
            }
            else
            {
                var control = new Point(
                    end.X - (end.X - penultimate.X) * tension / 6,
                    end.Y - (end.Y - penultimate.Y) * tension / 6);
                outward = end - control;
                if (outward.Length < 0.001) outward = end - penultimate;
            }
        }
        else
        {
            outward = atStart ? first - second : end - penultimate;
        }

        double segmentLength = atStart ? (second - first).Length : (end - penultimate).Length;
        if (segmentLength < 2 || outward.Length < 0.001) return Geometry.Empty;
        outward.Normalize();
        var normal = new Vector(-outward.Y, outward.X);

        if (cap is LineEndpointStyle.Round or LineEndpointStyle.Square)
        {
            double radius = Math.Clamp(StrokeThickness * 1.25, 4, 16);
            Geometry marker = cap == LineEndpointStyle.Round
                ? new EllipseGeometry(tip, radius, radius)
                : new LineGeometry(tip - normal * radius, tip + normal * radius);
            if (marker.CanFreeze) marker.Freeze();
            return marker;
        }

        bool filled = cap == LineEndpointStyle.FilledTriangle;
        if (cap != LineEndpointStyle.OpenArrow && !filled) return Geometry.Empty;

        double availableLength = segmentLength;
        if (IsFreehand && _pathPoints.Count > 2)
        {
            availableLength = 0;
            for (int i = 1; i <= last; i++)
                availableLength += (ToLocal(_pathPoints[i]) - ToLocal(_pathPoints[i - 1])).Length;
        }

        double length = Math.Min(Math.Clamp(StrokeThickness * 4.2, 16, 42), availableLength * 0.45);
        double halfWidth = Math.Min(Math.Clamp(StrokeThickness * 1.8, 5, 22), length * 0.65);
        Point baseCenter = tip - outward * length;
        Point left = baseCenter + normal * halfWidth;
        Point right = baseCenter - normal * halfWidth;

        var geometry = new StreamGeometry();
        using (var context = geometry.Open())
        {
            context.BeginFigure(filled ? tip : left, isFilled: filled, isClosed: filled);
            if (filled)
            {
                context.LineTo(left, isStroked: true, isSmoothJoin: false);
                context.LineTo(right, isStroked: true, isSmoothJoin: false);
            }
            else
            {
                context.LineTo(tip, isStroked: true, isSmoothJoin: true);
                context.LineTo(right, isStroked: true, isSmoothJoin: false);
            }
        }
        if (geometry.CanFreeze) geometry.Freeze();
        return geometry;
    }

    private Point ToLocal(Point point) => new(
        point.X * Math.Max(1, Width),
        point.Y * Math.Max(1, Height));

    public override void CopyStyleTo(DesignElement target)
    {
        base.CopyStyleTo(target);
        if (target is not LineElement line) return;
        line.StrokeColor = StrokeColor;
        line.StrokeThickness = StrokeThickness;
        line.Pattern = Pattern;
        line.StartCap = StartCap;
        line.EndCap = EndCap;
        line.IsFreehand = IsFreehand;
        line.IsPolyline = IsPolyline;
        line.CurveStrength = CurveStrength;
    }

    public override DesignElement Clone()
    {
        var clone = new LineElement
        {
            X = X,
            Y = Y,
            Width = Width,
            Height = Height,
            RotationAngle = RotationAngle,
            FlipHorizontal = FlipHorizontal,
            FlipVertical = FlipVertical,
            ZIndex = ZIndex,
            Opacity = Opacity,
            SoftEdge = SoftEdge,
            ShadowStyle = ShadowStyle,
            ShadowX = ShadowX,
            ShadowY = ShadowY,
            ShadowBlur = ShadowBlur,
            ShadowOpacity = ShadowOpacity,
            ShadowColor = ShadowColor,
            UseOpacityGradient = UseOpacityGradient,
            OpacityDirection = OpacityDirection,
            OpacityStart = OpacityStart,
            OpacityEnd = OpacityEnd,
            OpacityStrength = OpacityStrength,
            StrokeColor = StrokeColor,
            StrokeThickness = StrokeThickness,
            Pattern = Pattern,
            StartCap = StartCap,
            EndCap = EndCap,
            IsFreehand = IsFreehand,
            IsPolyline = IsPolyline,
            CurveStrength = CurveStrength,
        };
        clone.SetPathPoints(_pathPoints);
        return clone;
    }
}
