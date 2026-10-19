using System;
using System.IO;
using System.Text.Json;

namespace YorteImpact.Services;

/// <summary>
/// Persisted user preferences. Stored as JSON under
/// <c>%AppData%\YorteImpact\settings.json</c>; a missing or corrupt file
/// falls back to the defaults (2000 x 2000 white canvas).
/// </summary>
public sealed class AppSettings
{
    public int DefaultCanvasWidth { get; set; } = 2000;
    public int DefaultCanvasHeight { get; set; } = 2000;
    public bool DefaultCanvasTransparent { get; set; }

    public double? WindowLeft { get; set; }
    public double? WindowTop { get; set; }
    public double? WindowWidth { get; set; }
    public double? WindowHeight { get; set; }
    public bool WindowMaximized { get; set; }

    /// <summary>Style applied to newly drawn shapes, remembered between sessions.</summary>
    public ShapeDefaults Shape { get; set; } = new();

    /// <summary>Last-used line and arrow settings, remembered between sessions.</summary>
    public LineDefaults Line { get; set; } = new();

    /// <summary>Last-used paintbrush settings, remembered between sessions.</summary>
    public BrushDefaults Brush { get; set; } = new();

    /// <summary>Last-used text appearance for newly created text.</summary>
    public TextDefaults? Text { get; set; }

    /// <summary>User-saved paintbrush presets.</summary>
    public List<BrushDefaults> BrushPresets { get; set; } = new();

    private static string FilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "YorteImpact", "settings.json");

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(FilePath))
                return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath))
                       ?? new AppSettings();
        }
        catch
        {
            // Corrupt or unreadable: use defaults rather than fail to start.
        }
        return new AppSettings();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.WriteAllText(FilePath,
                JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch
        {
            // Settings are best-effort; never crash the editor over them.
        }
    }
}

/// <summary>
/// Last-used appearance for new shapes. Colors are stored as hex strings and
/// enums by name so the JSON stays readable and survives enum reordering.
/// </summary>
public sealed class ShapeDefaults
{
    public string Fill1 { get; set; } = "#3B82F6";
    public string Fill2 { get; set; } = "#93C5FD";
    public bool IsGradient { get; set; }
    public string GradientType { get; set; } = "Linear";
    public double GradientAngle { get; set; } = 135;
    public string Stroke { get; set; } = "#26549F";
    public double StrokeThickness { get; set; } = 2;
    public string ShapeKind { get; set; } = "Rectangle";
    public double CornerRadius { get; set; } = 14;
      public bool IndividualCorners { get; set; }
      public double CornerTopLeft { get; set; } = 14;
      public double CornerTopRight { get; set; } = 14;
      public double CornerBottomRight { get; set; } = 14;
      public double CornerBottomLeft { get; set; } = 14;

    public double Opacity { get; set; } = 100;
    public double SoftEdge { get; set; }
    public string ShadowStyle { get; set; } = "None";
    public double ShadowX { get; set; } = 4;
    public double ShadowY { get; set; } = 8;
    public double ShadowBlur { get; set; } = 14;
    public double ShadowOpacity { get; set; } = 35;
    public string ShadowColor { get; set; } = "#000000";

    public bool UseOpacityGradient { get; set; }
    public string OpacityDirection { get; set; } = "TopToBottom";
    public double OpacityStart { get; set; }
    public double OpacityEnd { get; set; } = 100;
    public double OpacityStrength { get; set; } = 100;
}

public sealed class LineDefaults
{
    public string Mode { get; set; } = "Line";
    public string Color { get; set; } = "#2E9BFF";
    public double Thickness { get; set; } = 4;
    public string Pattern { get; set; } = "Solid";
    public string StartCap { get; set; } = "Flat";
    public string EndCap { get; set; } = "Flat";
    public double CurveStrength { get; set; } = 50;
    public bool Shadow { get; set; }
    public string ShadowColor { get; set; } = "#000000";
}

/// <summary>
/// Paintbrush settings. Enums are stored by name and the color as hex so the
/// JSON stays readable and survives enum reordering.
/// </summary>
public sealed class BrushDefaults
{
    public string Name { get; set; } = "";
    public string Style { get; set; } = "Paint";
    public string Target { get; set; } = "Auto";
    public double Size { get; set; } = 36;
    public double Opacity { get; set; } = 100;
    public double Softness { get; set; } = 45;
    public double Smoothing { get; set; } = 35;
    public string Color { get; set; } = "#2E9BFF";
}

/// <summary>
/// Last-used text appearance. Enums and colors are stored as names/hex so the
/// JSON stays readable and survives enum reordering.
/// </summary>
public sealed class TextDefaults
{
    public string FontFamilyName { get; set; } = "Segoe UI";
    public double FontSize { get; set; } = 32;
    public string Color1 { get; set; } = "#1E293B";
    public string Color2 { get; set; } = "#2E9BFF";
    public bool IsGradient { get; set; }
    public double GradientAngle { get; set; } = 135;
    public bool IsBold { get; set; }
    public bool IsItalic { get; set; }
    public bool IsUnderline { get; set; }
    public bool IsStrikethrough { get; set; }
    public bool HasBorder { get; set; }
    public string BorderColor { get; set; } = "#000000";
    public double BorderThickness { get; set; } = 1;
    public string Direction { get; set; } = "Auto";
    public string Alignment { get; set; } = "Right";
    public double LetterSpacing { get; set; }
    public double LineSpacing { get; set; } = 120;
    public string ShadowStyle { get; set; } = "None";
    public double ShadowX { get; set; } = 4;
    public double ShadowY { get; set; } = 8;
    public double ShadowBlur { get; set; } = 14;
    public double ShadowOpacity { get; set; } = 35;
    public string ShadowColor { get; set; } = "#000000";
    public double Opacity { get; set; } = 100;
    public double SoftEdge { get; set; }
}
