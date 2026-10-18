using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace YorteImpact.Models;

/// <summary>
/// Raster image element. Rendered with an ImageBrush inside a Border so
/// <see cref="CornerRadius"/> clipping works the same as shapes.
/// </summary>
public class ImageElement : DesignElement
{
    private ImageSource? _source;
    private string? _sourcePath;
    private double _cornerRadius;
    private Brush? _imageBrush;

    public ImageSource? Source
    {
        get => _source;
        set { if (Set(ref _source, value)) RebuildBrush(); }
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
        set => Set(ref _cornerRadius, Math.Max(0, value));
    }

    /// <summary>Brush used by the DataTemplate (ImageBrush wrapping Source).</summary>
    public Brush? ImageBrush
    {
        get => _imageBrush;
        private set => Set(ref _imageBrush, value);
    }

    private void RebuildBrush()
    {
        if (Source is null) { ImageBrush = null; return; }
        var b = new ImageBrush(Source)
        {
            Stretch = Stretch.UniformToFill,
            AlignmentX = AlignmentX.Center,
            AlignmentY = AlignmentY.Center,
        };
        if (b.CanFreeze) b.Freeze();
        ImageBrush = b;
    }

    /// <summary>
    /// Assign a BitmapSource and auto-size the element to the pixel size
    /// (clamped to a sane max) unless keepSize is true.
    /// </summary>
    public void SetBitmap(BitmapSource bmp, bool keepSize = false)
    {
        Source = bmp;
        if (!keepSize && bmp.PixelWidth > 0 && bmp.PixelHeight > 0)
        {
            const double max = 640;
            double scale = Math.Min(1.0, Math.Min(max / bmp.PixelWidth, max / bmp.PixelHeight));
            Width = Math.Max(1, bmp.PixelWidth * scale);
            Height = Math.Max(1, bmp.PixelHeight * scale);
        }
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
        };
        return c;
    }
}
