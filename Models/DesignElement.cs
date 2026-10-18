using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Effects;

namespace YorteImpact.Models;

/// <summary>Shadow look presets. <see cref="Custom"/> means the free settings.</summary>
public enum ShadowStyle
{
    None,
    Soft,
    Medium,
    Hard,
    Long,
    Glow,
    Custom,
}

/// <summary>Direction of a fade-to-transparent opacity gradient.</summary>
public enum OpacityFadeDirection
{
    TopToBottom,
    BottomToTop,
    LeftToRight,
    RightToLeft,
    DiagonalDown,
    DiagonalUp,
    DiagonalDownReverse,
    DiagonalUpReverse,
}

/// <summary>
/// Base class for every placeable object on the pasteboard.
/// Stores geometry (X/Y/W/H in pasteboard coordinates), stacking (ZIndex),
/// orientation (RotationAngle in degrees, Flip H/V) and selection state.
/// Rendering is done via DataTemplates; this class stays UI-framework-light
/// except for INotifyPropertyChanged so Canvas bindings stay live.
/// </summary>
public abstract class DesignElement : INotifyPropertyChanged
{
    public Guid Id { get; internal set; } = Guid.NewGuid();

    private double _x;
    private double _y;
    private double _width = 160;
    private double _height = 120;
    private double _rotationAngle;
    private bool _flipHorizontal;
    private bool _flipVertical;
    private int _zIndex;
    private bool _isSelected;
    private double _opacity = 100; // percent
    private double _softEdge;       // percent (feather), 0 = hard edges
    private bool _useOpacityGradient;
    private OpacityFadeDirection _opacityDirection = OpacityFadeDirection.TopToBottom;
    private double _opacityStart;              // percent where the opaque area starts to fade
    private double _opacityEnd = 100;          // percent where the transparent level is reached
    private double _opacityStrength = 100;     // how transparent the faded end becomes
    private ShadowStyle _shadowStyle = ShadowStyle.None;
    private double _shadowX = 4;
    private double _shadowY = 8;
    private double _shadowBlur = 14;
    private double _shadowOpacity = 35;
    private Color _shadowColor = Colors.Black;

    /// <summary>Left position in pasteboard coordinates.</summary>
    public double X
    {
        get => _x;
        set { if (Set(ref _x, value)) OnDerivedChanged(); }
    }

    /// <summary>Top position in pasteboard coordinates.</summary>
    public double Y
    {
        get => _y;
        set { if (Set(ref _y, value)) OnDerivedChanged(); }
    }

    public double Width
    {
        get => _width;
        set => Set(ref _width, Math.Max(1, value));
    }

    public double Height
    {
        get => _height;
        set => Set(ref _height, Math.Max(1, value));
    }

    /// <summary>Clockwise rotation in degrees. Normalized to (-180, 180].</summary>
    public double RotationAngle
    {
        get => _rotationAngle;
        set => Set(ref _rotationAngle, NormalizeAngle(value));
    }

    public bool FlipHorizontal
    {
        get => _flipHorizontal;
        set { if (Set(ref _flipHorizontal, value)) OnDerivedChanged(); }
    }

    public bool FlipVertical
    {
        get => _flipVertical;
        set { if (Set(ref _flipVertical, value)) OnDerivedChanged(); }
    }

    /// <summary>Stacking order. Higher = painted later (on top).</summary>
    public int ZIndex
    {
        get => _zIndex;
        set => Set(ref _zIndex, value);
    }

    public bool IsSelected
    {
        get => _isSelected;
        set => Set(ref _isSelected, value);
    }

    /// <summary>Object transparency, kept as opacity percent (100 = fully opaque).</summary>
    public double Opacity
    {
        get => _opacity;
        set { if (Set(ref _opacity, Math.Clamp(value, 0, 100))) Raise(nameof(OpacityFactor)); }
    }

    /// <summary>0..1 multiplier the DataTemplates bind to (Border.Opacity).</summary>
    public double OpacityFactor => _opacity / 100.0;

    /// <summary>
    /// Soft edge (feather) amount as a percent, 0..100. Fades the object's
    /// edges to transparent for a vignette-like
    /// fade on every side (0 = crisp edges).
    /// </summary>
    public double SoftEdge
    {
        get => _softEdge;
        set
        {
            if (Set(ref _softEdge, Math.Clamp(value, 0, 100)))
            {
                Raise(nameof(SoftEdgeMask));
                Raise(nameof(OpacityMaskBrush));
            }
        }
    }

    /// <summary>
    /// Opacity mask that feathers the object's edges when <see cref="SoftEdge"/>
    /// is &gt; 0, otherwise null (no mask = hard edges). It is a rectangular
    /// feather (the product of a horizontal and a vertical edge ramp) so the
    /// four borders fade while the interior — corners included — stays solid,
    /// instead of a radial vignette that would round the shape off.
    /// </summary>
    public Brush? SoftEdgeMask
    {
        get
        {
            if (_softEdge <= 0.01) return null;

            // Feather width as a fraction of each side (0.5 = fade to the middle).
            double f = Math.Clamp(_softEdge / 100.0, 0.0, 1.0) * 0.5;
            var clear = Color.FromArgb(0, 255, 255, 255);

            var horizontal = new LinearGradientBrush
            {
                StartPoint = new Point(0, 0.5),
                EndPoint = new Point(1, 0.5),
                GradientStops =
                {
                    new GradientStop(clear, 0),
                    new GradientStop(Colors.White, f),
                    new GradientStop(Colors.White, 1 - f),
                    new GradientStop(clear, 1),
                },
            };

            var vertical = new LinearGradientBrush
            {
                StartPoint = new Point(0.5, 0),
                EndPoint = new Point(0.5, 1),
                GradientStops =
                {
                    new GradientStop(clear, 0),
                    new GradientStop(Colors.White, f),
                    new GradientStop(Colors.White, 1 - f),
                    new GradientStop(clear, 1),
                },
            };

            // White rectangle = the mask's "keep everything" area; each group
            // multiplies one directional ramp, so both edges feather together.
            var inner = new DrawingGroup { OpacityMask = horizontal };
            inner.Children.Add(new GeometryDrawing(
                new SolidColorBrush(Colors.White), null, new RectangleGeometry(new Rect(0, 0, 1, 1))));

            var outer = new DrawingGroup { OpacityMask = vertical };
            outer.Children.Add(inner);

            var brush = new DrawingBrush(outer) { Stretch = Stretch.Fill };
            brush.Freeze();
            return brush;
        }
    }

    // --- Opacity gradient (fade an element to transparent in one direction) ---

    public bool UseOpacityGradient
    {
        get => _useOpacityGradient;
        set { if (Set(ref _useOpacityGradient, value)) Raise(nameof(OpacityMaskBrush)); }
    }

    public OpacityFadeDirection OpacityDirection
    {
        get => _opacityDirection;
        set { if (Set(ref _opacityDirection, value)) Raise(nameof(OpacityMaskBrush)); }
    }

    /// <summary>Percent (0..100) where the fade starts; before it the object is opaque.</summary>
    public double OpacityStart
    {
        get => _opacityStart;
        set { if (Set(ref _opacityStart, Math.Clamp(value, 0, 100))) Raise(nameof(OpacityMaskBrush)); }
    }

    /// <summary>Percent (0..100) where the faded (transparent) level is reached.</summary>
    public double OpacityEnd
    {
        get => _opacityEnd;
        set { if (Set(ref _opacityEnd, Math.Clamp(value, 0, 100))) Raise(nameof(OpacityMaskBrush)); }
    }

    /// <summary>How transparent the faded end becomes (100 = fully transparent).</summary>
    public double OpacityStrength
    {
        get => _opacityStrength;
        set { if (Set(ref _opacityStrength, Math.Clamp(value, 0, 100))) Raise(nameof(OpacityMaskBrush)); }
    }

    /// <summary>
    /// Opacity mask bound by the templates: either the soft-edge feather or a
    /// directional fade. White = keep, black/transparent = hide.
    /// </summary>
    public Brush? OpacityMaskBrush
    {
        get
        {
            if (!_useOpacityGradient) return SoftEdgeMask;

            double start = Math.Clamp(_opacityStart / 100.0, 0, 1);
            double end = Math.Clamp(_opacityEnd / 100.0, 0, 1);
            if (end < start) end = start;
            if (end - start < 0.001) end = Math.Min(1, start + 0.001);

            byte endAlpha = (byte)Math.Round(255 * (1 - Math.Clamp(_opacityStrength, 0, 100) / 100.0));
            var faded = Color.FromArgb(endAlpha, 255, 255, 255);

            var (sp, ep) = _opacityDirection switch
            {
                OpacityFadeDirection.TopToBottom => (new Point(0.5, 0), new Point(0.5, 1)),
                OpacityFadeDirection.BottomToTop => (new Point(0.5, 1), new Point(0.5, 0)),
                OpacityFadeDirection.LeftToRight => (new Point(0, 0.5), new Point(1, 0.5)),
                OpacityFadeDirection.RightToLeft => (new Point(1, 0.5), new Point(0, 0.5)),
                OpacityFadeDirection.DiagonalDown => (new Point(0, 0), new Point(1, 1)),
                OpacityFadeDirection.DiagonalUp => (new Point(1, 0), new Point(0, 1)),
                OpacityFadeDirection.DiagonalDownReverse => (new Point(0, 1), new Point(1, 0)),
                _ => (new Point(1, 1), new Point(0, 0)),
            };

            var brush = new LinearGradientBrush
            {
                StartPoint = sp,
                EndPoint = ep,
                MappingMode = BrushMappingMode.RelativeToBoundingBox,
                GradientStops =
                {
                    new GradientStop(Colors.White, 0),
                    new GradientStop(Colors.White, start),
                    new GradientStop(faded, end),
                    new GradientStop(faded, 1),
                },
            };
            brush.Freeze();
            return brush;
        }
    }

    // --- Shadow (drop shadow via a WPF DropShadowEffect) ---

    /// <summary>Selected shadow preset. None = no shadow; Custom = free settings.</summary>
    public ShadowStyle ShadowStyle
    {
        get => _shadowStyle;
        set { if (Set(ref _shadowStyle, value)) Raise(nameof(ShadowEffect)); }
    }

    /// <summary>Horizontal shadow offset in pixels (positive = right).</summary>
    public double ShadowX
    {
        get => _shadowX;
        set { if (Set(ref _shadowX, value)) Raise(nameof(ShadowEffect)); }
    }

    /// <summary>Vertical shadow offset in pixels (positive = down).</summary>
    public double ShadowY
    {
        get => _shadowY;
        set { if (Set(ref _shadowY, value)) Raise(nameof(ShadowEffect)); }
    }

    /// <summary>Softness of the shadow edge, 0..200.</summary>
    public double ShadowBlur
    {
        get => _shadowBlur;
        set { if (Set(ref _shadowBlur, Math.Clamp(value, 0, 200))) Raise(nameof(ShadowEffect)); }
    }

    /// <summary>Shadow opacity in percent, 0..100.</summary>
    public double ShadowOpacity
    {
        get => _shadowOpacity;
        set { if (Set(ref _shadowOpacity, Math.Clamp(value, 0, 100))) Raise(nameof(ShadowEffect)); }
    }

    public Color ShadowColor
    {
        get => _shadowColor;
        set { if (Set(ref _shadowColor, value)) Raise(nameof(ShadowEffect)); }
    }

    /// <summary>Effect bound by the DataTemplates; null when no shadow is enabled.</summary>
    public DropShadowEffect? ShadowEffect
    {
        get
        {
            if (_shadowStyle == ShadowStyle.None) return null;

            // WPF expresses the drop shadow as a distance along a direction.
            double depth = Math.Sqrt(_shadowX * _shadowX + _shadowY * _shadowY);
            double direction = Math.Atan2(_shadowY, _shadowX) * 180.0 / Math.PI;
            if (direction < 0) direction += 360;

            var effect = new DropShadowEffect
            {
                Direction = direction,
                ShadowDepth = Math.Clamp(depth, 0, 200),
                BlurRadius = _shadowBlur,
                Opacity = _shadowOpacity / 100.0,
                Color = _shadowColor,
            };
            if (effect.CanFreeze) effect.Freeze();
            return effect;
        }
    }

    /// <summary>Apply one of the built-in looks (or None to turn the shadow off).</summary>
    public void ApplyShadowPreset(ShadowStyle style)
    {
        ShadowStyle = style;
        switch (style)
        {
            case ShadowStyle.Soft:
                ShadowX = 0; ShadowY = 6; ShadowBlur = 18; ShadowOpacity = 28; ShadowColor = Colors.Black;
                break;
            case ShadowStyle.Medium:
                ShadowX = 4; ShadowY = 8; ShadowBlur = 14; ShadowOpacity = 35; ShadowColor = Colors.Black;
                break;
            case ShadowStyle.Hard:
                ShadowX = 6; ShadowY = 8; ShadowBlur = 4; ShadowOpacity = 45; ShadowColor = Colors.Black;
                break;
            case ShadowStyle.Long:
                ShadowX = 14; ShadowY = 18; ShadowBlur = 24; ShadowOpacity = 30; ShadowColor = Colors.Black;
                break;
            case ShadowStyle.Glow:
                ShadowX = 0; ShadowY = 0; ShadowBlur = 30; ShadowOpacity = 60;
                ShadowColor = Color.FromRgb(0x2E, 0x9B, 0xFF);
                break;
        }
        Raise(nameof(ShadowEffect));
    }

    // --- Derived helpers for XAML transforms (Scale -1 = flipped) ---

    public double FlipScaleX => FlipHorizontal ? -1 : 1;
    public double FlipScaleY => FlipVertical ? -1 : 1;

    /// <summary>Center in pasteboard coords. Used for rotation math.</summary>
    public System.Windows.Point Center => new(X + Width / 2.0, Y + Height / 2.0);

    public Rect Bounds => new(X, Y, Width, Height);

    public event PropertyChangedEventHandler? PropertyChanged;

    protected bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (Equals(field, value)) return false;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        return true;
    }

    protected void Raise(string name) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    private void OnDerivedChanged()
    {
        Raise(nameof(FlipScaleX));
        Raise(nameof(FlipScaleY));
        Raise(nameof(Center));
        Raise(nameof(Bounds));
    }

    public static double NormalizeAngle(double a)
    {
        a %= 360;
        if (a > 180) a -= 360;
        if (a <= -180) a += 360;
        return a;
    }

    public abstract DesignElement Clone();
}
