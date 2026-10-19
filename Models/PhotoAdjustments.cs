using System.Windows.Media;

namespace YorteImpact.Models;

/// <summary>
/// Non-destructive photo adjustments stored on an <see cref="ImageElement"/>.
/// The original raster stays untouched; the display and export are rendered
/// through these values.
/// </summary>
public sealed class PhotoAdjustments
{
    public static Color DefaultToneColor { get; } = Color.FromRgb(0xFF, 0x8A, 0x3D);

    private double _exposure;
    private double _contrast;
    private double _highlights;
    private double _shadows;
    private double _whites;
    private double _blacks;
    private double _temperature;
    private double _tint;
    private double _vibrance;
    private double _saturation;
    private double _clarity;
    private double _vignette;
    private double _grain;
    private double _blur;
    private double _sharpen;
    private double _glow;
    private double _toneAmount;
    private Color _toneColor = DefaultToneColor;

    /// <summary>Exposure in stops, -5..+5.</summary>
    public double Exposure { get => _exposure; set => _exposure = Clamp(value, -5, 5); }

    public double Contrast { get => _contrast; set => _contrast = Clamp(value, -100, 100); }
    public double Highlights { get => _highlights; set => _highlights = Clamp(value, -100, 100); }
    public double Shadows { get => _shadows; set => _shadows = Clamp(value, -100, 100); }
    public double Whites { get => _whites; set => _whites = Clamp(value, -100, 100); }
    public double Blacks { get => _blacks; set => _blacks = Clamp(value, -100, 100); }

    public double Temperature { get => _temperature; set => _temperature = Clamp(value, -100, 100); }
    public double Tint { get => _tint; set => _tint = Clamp(value, -100, 100); }
    public double Vibrance { get => _vibrance; set => _vibrance = Clamp(value, -100, 100); }
    public double Saturation { get => _saturation; set => _saturation = Clamp(value, -100, 100); }

    public double Clarity { get => _clarity; set => _clarity = Clamp(value, -100, 100); }
    public double Vignette { get => _vignette; set => _vignette = Clamp(value, -100, 100); }
    public double Grain { get => _grain; set => _grain = Clamp(value, 0, 100); }

    public double Blur { get => _blur; set => _blur = Clamp(value, 0, 100); }
    public double Sharpen { get => _sharpen; set => _sharpen = Clamp(value, 0, 100); }
    public double Glow { get => _glow; set => _glow = Clamp(value, 0, 100); }

    /// <summary>Strength of the colorize/tone effect, 0..100.</summary>
    public double ToneAmount { get => _toneAmount; set => _toneAmount = Clamp(value, 0, 100); }
    public Color ToneColor { get => _toneColor; set => _toneColor = value; }

    public bool IsDefault =>
        _exposure == 0 && _contrast == 0 && _highlights == 0 && _shadows == 0 &&
        _whites == 0 && _blacks == 0 && _temperature == 0 && _tint == 0 &&
        _vibrance == 0 && _saturation == 0 && _clarity == 0 && _vignette == 0 &&
        _grain == 0 && _blur == 0 && _sharpen == 0 && _glow == 0 && _toneAmount == 0;

    public void Reset()
    {
        _exposure = _contrast = _highlights = _shadows = _whites = _blacks = 0;
        _temperature = _tint = _vibrance = _saturation = 0;
        _clarity = _vignette = _grain = 0;
        _blur = _sharpen = _glow = _toneAmount = 0;
    }

    public void CopyFrom(PhotoAdjustments? other)
    {
        if (other == null) { Reset(); return; }
        Exposure = other.Exposure;
        Contrast = other.Contrast;
        Highlights = other.Highlights;
        Shadows = other.Shadows;
        Whites = other.Whites;
        Blacks = other.Blacks;
        Temperature = other.Temperature;
        Tint = other.Tint;
        Vibrance = other.Vibrance;
        Saturation = other.Saturation;
        Clarity = other.Clarity;
        Vignette = other.Vignette;
        Grain = other.Grain;
        Blur = other.Blur;
        Sharpen = other.Sharpen;
        Glow = other.Glow;
        ToneAmount = other.ToneAmount;
        ToneColor = other.ToneColor;
    }

    public PhotoAdjustments Clone()
    {
        var copy = new PhotoAdjustments();
        copy.CopyFrom(this);
        return copy;
    }

    private static double Clamp(double value, double min, double max) =>
        double.IsFinite(value) ? Math.Clamp(value, min, max) : 0;
}
