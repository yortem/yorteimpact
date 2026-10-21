using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace YorteImpact.Models;

/// <summary>Glass looks available on a shape or image.</summary>
public enum GlassStyle
{
    Frosted,
    Liquid,
    Glossy,
}

/// <summary>
/// Non-destructive glass overlay settings. The blurred backdrop is a runtime
/// brush supplied by <c>GlassEffectService</c>; only the settings are persisted.
/// </summary>
public sealed class GlassEffect : INotifyPropertyChanged
{
    private bool _enabled;
    private GlassStyle _style = GlassStyle.Frosted;
    private double _blur = 18;
    private double _refraction;
    private double _rim = 45;
    private double _gloss = 10;
    private double _noise = 2;
    private Brush? _backdropBrush;
    private Brush? _highlightBrush;
    private Brush? _rimBrush;
    private static readonly Brush SharedNoiseBrush = BuildNoiseBrush();

    public GlassEffect()
    {
        RebuildLooks();
    }

    public bool Enabled
    {
        get => _enabled;
        set
        {
            if (!Set(ref _enabled, value)) return;
            Raise(nameof(IsEnabled));
            if (!value) BackdropBrush = null;
        }
    }

    public bool IsEnabled => _enabled;

    public GlassStyle Style
    {
        get => _style;
        set
        {
            if (!Set(ref _style, value)) return;
            Raise(nameof(IsLiquid));
            RebuildLooks();
        }
    }

    public bool IsLiquid => _style == GlassStyle.Liquid;

    /// <summary>Backdrop blur radius in DIPs.</summary>
    public double Blur
    {
        get => _blur;
        set => Set(ref _blur, Math.Clamp(value, 0, 60));
    }

    /// <summary>How much the backdrop is zoomed inside the glass, in percent.</summary>
    public double Refraction
    {
        get => _refraction;
        set => Set(ref _refraction, Math.Clamp(value, 0, 15));
    }

    /// <summary>Brightness of the glass edge in percent.</summary>
    public double Rim
    {
        get => _rim;
        set
        {
            if (Set(ref _rim, Math.Clamp(value, 0, 100))) RebuildLooks();
        }
    }

    /// <summary>Strength of the glossy highlight in percent.</summary>
    public double Gloss
    {
        get => _gloss;
        set
        {
            if (Set(ref _gloss, Math.Clamp(value, 0, 100))) RebuildLooks();
        }
    }

    /// <summary>Subtle surface grain amount in percent.</summary>
    public double Noise
    {
        get => _noise;
        set
        {
            if (Set(ref _noise, Math.Clamp(value, 0, 15))) Raise(nameof(NoiseOpacity));
        }
    }

    /// <summary>Runtime backdrop render. Regenerated when content below changes.</summary>
    public Brush? BackdropBrush
    {
        get => _backdropBrush;
        set => Set(ref _backdropBrush, value);
    }

    public Brush? HighlightBrush => _highlightBrush;

    public Brush? RimBrush => _rimBrush;

    public Brush NoiseBrush => SharedNoiseBrush;

    public double NoiseOpacity => Math.Clamp(_noise / 100.0 * 0.5, 0, 0.075);

    public void ApplyPreset(GlassStyle style)
    {
        Style = style;
        switch (style)
        {
            case GlassStyle.Frosted:
                Blur = 18;
                Refraction = 0;
                Rim = 45;
                Gloss = 10;
                Noise = 2;
                break;
            case GlassStyle.Liquid:
                Blur = 14;
                Refraction = 5;
                Rim = 65;
                Gloss = 25;
                Noise = 3;
                break;
            case GlassStyle.Glossy:
                Blur = 0;
                Refraction = 0;
                Rim = 55;
                Gloss = 75;
                Noise = 0;
                break;
        }
    }

    public void CopyFrom(GlassEffect other)
    {
        Enabled = other.Enabled;
        Style = other.Style;
        Blur = other.Blur;
        Refraction = other.Refraction;
        Rim = other.Rim;
        Gloss = other.Gloss;
        Noise = other.Noise;
    }

    public GlassEffect Clone()
    {
        var copy = new GlassEffect();
        copy.CopyFrom(this);
        return copy;
    }

    private void RebuildLooks()
    {
        _highlightBrush = BuildHighlight();
        _rimBrush = BuildRim();
        Raise(nameof(HighlightBrush));
        Raise(nameof(RimBrush));
    }

    private Brush? BuildHighlight()
    {
        double strength = _gloss / 100.0;
        if (strength <= 0.001) return null;

        byte Alpha(double value) =>
            (byte)Math.Clamp(Math.Round(255 * strength * value), 0, 255);

        var brush = new LinearGradientBrush
        {
            StartPoint = new Point(0, 0),
            EndPoint = new Point(1, 1),
        };
        brush.GradientStops.Add(new GradientStop(
            Color.FromArgb(Alpha(_style == GlassStyle.Glossy ? 1.0 : 0.8), 255, 255, 255), 0));
        brush.GradientStops.Add(new GradientStop(
            Color.FromArgb(Alpha(_style == GlassStyle.Glossy ? 0.45 : 0.3), 255, 255, 255), 0.22));
        brush.GradientStops.Add(new GradientStop(
            Color.FromArgb(Alpha(0.04), 255, 255, 255), 0.52));
        brush.GradientStops.Add(new GradientStop(
            Color.FromArgb(Alpha(0.12), 255, 255, 255), 1));
        brush.Freeze();
        return brush;
    }

    private Brush? BuildRim()
    {
        double strength = _rim / 100.0;
        if (strength <= 0.001) return null;

        byte Alpha(double value) =>
            (byte)Math.Clamp(Math.Round(255 * strength * value), 0, 255);

        var brush = new LinearGradientBrush
        {
            StartPoint = new Point(0, 0),
            EndPoint = new Point(1, 1),
        };
        brush.GradientStops.Add(new GradientStop(
            Color.FromArgb(Alpha(0.95), 255, 255, 255), 0));
        brush.GradientStops.Add(new GradientStop(
            Color.FromArgb(Alpha(0.2), 255, 255, 255), 0.45));
        brush.GradientStops.Add(new GradientStop(
            Color.FromArgb(Alpha(0.5), 255, 255, 255), 1));
        brush.Freeze();
        return brush;
    }

    private static Brush BuildNoiseBrush()
    {
        const int size = 48;
        var pixels = new byte[size * size * 4];
        var random = new Random(7);
        for (int i = 0; i < size * size; i++)
        {
            byte alpha = (byte)random.Next(0, 256);
            pixels[i * 4] = alpha;
            pixels[i * 4 + 1] = alpha;
            pixels[i * 4 + 2] = alpha;
            pixels[i * 4 + 3] = alpha;
        }

        var bitmap = BitmapSource.Create(size, size, 96, 96,
            PixelFormats.Pbgra32, null, pixels, size * 4);
        bitmap.Freeze();
        var brush = new ImageBrush(bitmap)
        {
            TileMode = TileMode.Tile,
            ViewportUnits = BrushMappingMode.Absolute,
            Viewport = new Rect(0, 0, size, size),
            Stretch = Stretch.None,
        };
        brush.Freeze();
        return brush;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (Equals(field, value)) return false;
        field = value;
        Raise(name ?? string.Empty);
        return true;
    }

    private void Raise(string name) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
