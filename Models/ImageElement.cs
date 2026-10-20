using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using YorteImpact.Services;

namespace YorteImpact.Models;

/// <summary>How the raster fills the element box when the box is resized.</summary>
public enum ImageFitMode
{
    /// <summary>Fill the box and crop the overflow (the original behavior).</summary>
    Fill,
    /// <summary>Show the whole raster with empty space around it.</summary>
    Fit,
    /// <summary>Stretch the raster to the box, distorting the aspect ratio.</summary>
    Stretch,
}

/// <summary>
/// Raster image element. Rendered with an ImageBrush inside a Border so
/// <see cref="CornerRadius"/> clipping works the same as shapes.
/// </summary>
public class ImageElement : DesignElement
{
    private ImageSource? _source;
    private BitmapSource? _displaySource;
    private readonly PhotoAdjustments _adjustments = new();
    private string? _sourcePath;
    private double _cornerRadius;
    private ImageFitMode _fitMode = ImageFitMode.Fill;
    private Brush? _imageBrush;
    private ImageSource? _alphaSource;
    private bool? _hasTransparency;

    public ImageElement()
    {
        PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(UseIndividualCornerRadii) or nameof(IndividualCornerRadii))
                Raise(nameof(RenderCornerRadius));
        };
    }

    public ImageSource? Source
    {
        get => _source;
        set
        {
            if (Equals(_source, value)) return;
            _source = value;
            _displaySource = null;
            RefreshPhotoPreview();
            Raise(nameof(Source));
        }
    }

    /// <summary>Non-destructive photo settings rendered on top of <see cref="Source"/>.</summary>
    public PhotoAdjustments Adjustments => _adjustments;

    /// <summary>Adjusted raster used for display, sampling and export.</summary>
    public ImageSource? DisplaySource => _displaySource ?? _source;

    /// <summary>True when the original raster has a meaningful transparent area.</summary>
    public bool HasTransparency
    {
        get
        {
            if (ReferenceEquals(_alphaSource, _source) && _hasTransparency.HasValue)
                return _hasTransparency.Value;

            _alphaSource = _source;
            _hasTransparency = ImageAlphaService.HasTransparency(_source as BitmapSource);
            return _hasTransparency.Value;
        }
    }

    /// <summary>Rebuild the adjusted raster after photo settings or the base image changed.</summary>
    public void RefreshPhotoPreview()
    {
        // The intensity alone changes nothing, so there is no point in keeping
        // a second copy of the raster in memory.
        _displaySource = !_adjustments.HasAdjustments || _source is not BitmapSource bmp
            ? null
            : PhotoAdjustmentService.Render(bmp, _adjustments);
        RebuildBrush();
        Raise(nameof(DisplaySource));
    }

    /// <summary>Original file path, if loaded from disk. Null for clipboard pastes.</summary>
    public string? SourcePath
    {
        get => _sourcePath;
        set => Set(ref _sourcePath, value);
    }

    public double CornerRadius
    {
        get => _cornerRadius;
        set
        {
            if (Set(ref _cornerRadius, Math.Max(0, value)))
                Raise(nameof(RenderCornerRadius));
        }
    }

    public CornerRadius RenderCornerRadius => UseIndividualCornerRadii
        ? IndividualCornerRadii
        : new CornerRadius(CornerRadius);

    /// <summary>How the image fills the element box when the object is resized.</summary>
    public ImageFitMode FitMode
    {
        get => _fitMode;
        set
        {
            if (Set(ref _fitMode, value)) RebuildBrush();
        }
    }

    /// <summary>Source crop and scale used by the current fit mode.</summary>
    public (double ScaleX, double ScaleY, double OffsetX, double OffsetY,
            int SrcX, int SrcY, int SrcW, int SrcH) GetFitGeometry(BitmapSource bmp)
    {
        int pw = bmp.PixelWidth, ph = bmp.PixelHeight;
        if (pw < 1 || ph < 1) return (1, 1, 0, 0, 0, 0, 1, 1);

        if (_fitMode == ImageFitMode.Fit)
        {
            double scale = Math.Min(Width / pw, Height / ph);
            return (scale, scale,
                (Width - pw * scale) / 2, (Height - ph * scale) / 2,
                0, 0, pw, ph);
        }

        if (_fitMode == ImageFitMode.Stretch)
            return (Width / pw, Height / ph, 0, 0, 0, 0, pw, ph);

        double fill = Math.Max(Width / pw, Height / ph);
        int cx = Math.Clamp((int)Math.Floor((pw * fill - Width) / 2 / fill), 0, pw - 1);
        int cy = Math.Clamp((int)Math.Floor((ph * fill - Height) / 2 / fill), 0, ph - 1);
        int cw = Math.Clamp((int)Math.Ceiling(Width / fill), 1, pw - cx);
        int ch = Math.Clamp((int)Math.Ceiling(Height / fill), 1, ph - cy);
        return (fill, fill, (Width - pw * fill) / 2, (Height - ph * fill) / 2,
            cx, cy, cw, ch);
    }

    /// <summary>Map an element-local point to a source pixel for the current fit mode.</summary>
    public bool TryMapToSource(Point local, out double sourceX, out double sourceY,
        out double pixelsPerUnit)
    {
        sourceX = sourceY = 0;
        pixelsPerUnit = 1;
        if (_source is not BitmapSource bmp || bmp.PixelWidth < 1 || bmp.PixelHeight < 1)
            return false;

        var g = GetFitGeometry(bmp);
        sourceX = (local.X - g.OffsetX) / Math.Max(0.0001, g.ScaleX);
        sourceY = (local.Y - g.OffsetY) / Math.Max(0.0001, g.ScaleY);
        pixelsPerUnit = 1 / Math.Max(0.0001, (g.ScaleX + g.ScaleY) / 2);
        return sourceX >= 0 && sourceY >= 0 &&
               sourceX <= bmp.PixelWidth && sourceY <= bmp.PixelHeight;
    }

    /// <summary>Brush used by the DataTemplate (ImageBrush wrapping Source).</summary>
    public Brush? ImageBrush
    {
        get => _imageBrush;
        private set => Set(ref _imageBrush, value);
    }

    private void RebuildBrush()
    {
        var source = DisplaySource;
        if (source is null) { ImageBrush = null; return; }
        var b = new ImageBrush(source)
        {
            Stretch = _fitMode switch
            {
                ImageFitMode.Fit => Stretch.Uniform,
                ImageFitMode.Stretch => Stretch.Fill,
                _ => Stretch.UniformToFill,
            },
            AlignmentX = AlignmentX.Center,
            AlignmentY = AlignmentY.Center,
        };
        if (b.CanFreeze && source is not WriteableBitmap) b.Freeze();
        ImageBrush = b;
    }

    /// <summary>
    /// Assign a BitmapSource and auto-size the element to its pixel size
    /// (one canvas unit per image pixel) unless keepSize is true.
    /// </summary>
    public void SetBitmap(BitmapSource bmp, bool keepSize = false)
    {
        Source = bmp;
        if (!keepSize && bmp.PixelWidth > 0 && bmp.PixelHeight > 0)
        {
            Width = bmp.PixelWidth;
            Height = bmp.PixelHeight;
        }
    }

    public override void CopyStyleTo(DesignElement target)
    {
        base.CopyStyleTo(target);
        if (target is not ImageElement image) return;
        image.CornerRadius = CornerRadius;
        image.FitMode = FitMode;
        image.Adjustments.CopyFrom(Adjustments);
        image.RefreshPhotoPreview();
    }

    public override DesignElement Clone()
    {
        var c = new ImageElement
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
            Source = Source, SourcePath = SourcePath,
            CornerRadius = CornerRadius,
            UseIndividualCornerRadii = UseIndividualCornerRadii,
            IndividualCornerRadii = IndividualCornerRadii,
            FitMode = FitMode,
        };
        c.Adjustments.CopyFrom(Adjustments);
        c.RefreshPhotoPreview();
        return c;
    }
}
