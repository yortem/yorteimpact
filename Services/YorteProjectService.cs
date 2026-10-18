using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using YorteImpact.Models;
using YorteImpact.ViewModels;

namespace YorteImpact.Services;

/// <summary>
/// Native editable project format (.yimpact): JSON document with embedded
/// PNG image data (base64), so clipboard-pasted images survive save/load.
/// Re-encoded on save; decoded back to frozen BitmapSources on load.
/// </summary>
public static class YorteProjectService
{
    public const string Extension = ".yimpact";
    public const string Filter = "YorteImpact project (*.yimpact)|*.yimpact";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    // ---------------- DTOs ----------------

    public sealed class ProjectFile
    {
        public int Version { get; set; } = 1;
        public string Name { get; set; } = "Untitled-1";
        public double ArtboardWidth { get; set; }
        public double ArtboardHeight { get; set; }
        public string? Primary { get; set; } // project primary color (#RRGGBB)
        public string? CanvasColor { get; set; } // canvas paper color (#RRGGBB)
        public bool CanvasTransparent { get; set; } // true = no background (exports transparent)
        public List<ElementDto> Elements { get; set; } = new();
    }

    public sealed class ElementDto
    {
        public string? Id { get; set; } // stable across undo snapshots (not just files)
        public string Kind { get; set; } = "shape"; // "shape" | "image"
        public double X { get; set; }
        public double Y { get; set; }
        public double Width { get; set; }
        public double Height { get; set; }
        public double Rotation { get; set; }
        public bool FlipH { get; set; }
        public bool FlipV { get; set; }
        public int Z { get; set; }
        public double Opacity { get; set; } = 100; // percent
        public double SoftEdge { get; set; }       // percent (feather), 0 = hard edges
        public double CornerRadius { get; set; }
        // shadow
        public string? ShadowStyle { get; set; }
        public double ShadowX { get; set; }
        public double ShadowY { get; set; }
        public double ShadowBlur { get; set; }
        public double ShadowOpacity { get; set; }
        public string? ShadowColor { get; set; }
        // directional opacity gradient
        public bool OpacityGradient { get; set; }
        public string? OpacityDirection { get; set; }
        public double OpacityStart { get; set; }
        public double OpacityEnd { get; set; }
        public double OpacityStrength { get; set; }
        // shape only
        public string? Fill1 { get; set; }
        public string? Fill2 { get; set; }
        public bool IsGradient { get; set; }
        public string? GradientType { get; set; }
        public double GradientAngle { get; set; }
        public string? Stroke { get; set; }
        public double StrokeThickness { get; set; }
        // text only
        public string? Text { get; set; }
        public string? FontFamily { get; set; }
        public double FontSize { get; set; }
        public bool Bold { get; set; }
        public bool Italic { get; set; }
        public bool Underline { get; set; }
        public bool Strikethrough { get; set; }
        public string? TextDirection { get; set; }
        public string? TextAlignment { get; set; }
        public double LetterSpacing { get; set; }
        public double LineSpacing { get; set; }
        public double WrapWidth { get; set; }
        // image only (embedded PNG)
        public string? ImageBase64 { get; set; }
    }

    // ---------------- save ----------------

    /// <summary>Capture the whole document as an in-memory snapshot (undo + save share it).</summary>
    public static ProjectFile Capture(DesignDocument doc)
    {
        var file = new ProjectFile
        {
            Name = doc.DocumentName,
            ArtboardWidth = doc.ArtboardWidth,
            ArtboardHeight = doc.ArtboardHeight,
            CanvasColor = ToHex(doc.CanvasColor),
            CanvasTransparent = doc.CanvasTransparent,
        };
        foreach (var el in doc.Elements.OrderBy(e => e.ZIndex))
            file.Elements.Add(ToDto(el));
        return file;
    }

    public static void Save(DesignDocument doc, string path, string? primaryHex = null)
    {
        var file = Capture(doc);
        file.Primary = primaryHex;

        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path)) ?? ".");
        File.WriteAllText(path, JsonSerializer.Serialize(file, JsonOptions));
    }

    private static ElementDto ToDto(DesignElement el)
    {
        var dto = new ElementDto
        {
            Id = el.Id.ToString("D"),
            X = el.X, Y = el.Y, Width = el.Width, Height = el.Height,
            Rotation = el.RotationAngle,
            FlipH = el.FlipHorizontal, FlipV = el.FlipVertical,
            Z = el.ZIndex,
            Opacity = el.Opacity,
            SoftEdge = el.SoftEdge,
            ShadowStyle = el.ShadowStyle.ToString(),
            ShadowX = el.ShadowX, ShadowY = el.ShadowY,
            ShadowBlur = el.ShadowBlur, ShadowOpacity = el.ShadowOpacity,
            ShadowColor = ToHex(el.ShadowColor),
            OpacityGradient = el.UseOpacityGradient,
            OpacityDirection = el.OpacityDirection.ToString(),
            OpacityStart = el.OpacityStart,
            OpacityEnd = el.OpacityEnd,
            OpacityStrength = el.OpacityStrength,
        };
        switch (el)
        {
            case ShapeElement s:
                dto.Kind = "shape";
                dto.CornerRadius = s.CornerRadius;
                dto.Fill1 = ToHex(s.FillColor1); dto.Fill2 = ToHex(s.FillColor2);
                dto.IsGradient = s.IsGradient; dto.GradientAngle = s.GradientAngle;
                dto.GradientType = s.GradientType.ToString();
                dto.Stroke = ToHex(s.StrokeColor); dto.StrokeThickness = s.StrokeThickness;
                break;
            case ImageElement im:
                dto.Kind = "image";
                dto.CornerRadius = im.CornerRadius;
                if (im.Source is BitmapSource bmp)
                    dto.ImageBase64 = Convert.ToBase64String(EncodePng(bmp));
                break;
            case TextElement t:
                dto.Kind = "text";
                dto.Text = t.Text;
                dto.FontFamily = t.FontFamilyName;
                dto.FontSize = t.FontSize;
                dto.Fill1 = ToHex(t.TextColor1); dto.Fill2 = ToHex(t.TextColor2);
                dto.IsGradient = t.IsGradient; dto.GradientAngle = t.TextGradientAngle;
                dto.Bold = t.IsBold; dto.Italic = t.IsItalic;
                dto.Underline = t.IsUnderline; dto.Strikethrough = t.IsStrikethrough;
                dto.TextDirection = t.Direction.ToString();
                dto.TextAlignment = t.Alignment.ToString();
                dto.LetterSpacing = t.LetterSpacing; dto.LineSpacing = t.LineSpacing;
                dto.WrapWidth = t.WrapWidth;
                break;
        }
        return dto;
    }

    // ---------------- load ----------------

    public static ProjectFile Load(string path)
    {
        var file = JsonSerializer.Deserialize<ProjectFile>(File.ReadAllText(path), JsonOptions)
            ?? throw new InvalidDataException("Not a valid .yimpact file.");
        if (file.Version != 1) throw new InvalidDataException($"Unsupported project version {file.Version}.");
        return file;
    }

    /// <summary>Populate a fresh canvas from a project file (used by MainWindow).</summary>
    public static void ApplyTo(ProjectFile file, DesignDocument doc)
    {
        doc.NewCanvas(file.Name,
            Math.Clamp(file.ArtboardWidth, 50, 8000),
            Math.Clamp(file.ArtboardHeight, 50, 8000));

        doc.CanvasColor = FromHex(file.CanvasColor, Colors.White);
        doc.CanvasTransparent = file.CanvasTransparent;

        foreach (var dto in file.Elements.OrderBy(e => e.Z))
        {
            DesignElement? el = dto.Kind switch
            {
                "shape" => new ShapeElement
                {
                    CornerRadius = dto.CornerRadius,
                    FillColor1 = FromHex(dto.Fill1, Colors.Blue),
                    FillColor2 = FromHex(dto.Fill2, Colors.LightBlue),
                    IsGradient = dto.IsGradient, GradientAngle = dto.GradientAngle,
                    GradientType = ParseGradientType(dto.GradientType),
                    StrokeColor = FromHex(dto.Stroke, Colors.Navy),
                    StrokeThickness = dto.StrokeThickness,
                },
                "image" => LoadImage(dto),
                "text" => new TextElement
                {
                    Text = dto.Text ?? "",
                    FontFamilyName = dto.FontFamily ?? "Segoe UI",
                    FontSize = dto.FontSize > 0 ? dto.FontSize : 32,
                    TextColor1 = FromHex(dto.Fill1, Color.FromRgb(0x1E, 0x29, 0x3B)),
                    TextColor2 = FromHex(dto.Fill2, Color.FromRgb(0x2E, 0x9B, 0xFF)),
                    IsGradient = dto.IsGradient,
                    TextGradientAngle = dto.GradientAngle,
                    IsBold = dto.Bold,
                    IsItalic = dto.Italic,
                    IsUnderline = dto.Underline,
                    IsStrikethrough = dto.Strikethrough,
                    Direction = ParseTextDirection(dto.TextDirection),
                    Alignment = ParseTextAlign(dto.TextAlignment),
                    LetterSpacing = dto.LetterSpacing,
                    LineSpacing = dto.LineSpacing > 0 ? dto.LineSpacing : 120,
                    WrapWidth = dto.WrapWidth,
                },
                _ => null,
            };
            if (el == null) continue;
            if (Guid.TryParse(dto.Id, out var gid)) el.Id = gid; // keep identity for undo selection
            el.X = dto.X; el.Y = dto.Y;
            el.Width = Math.Max(1, dto.Width); el.Height = Math.Max(1, dto.Height);
            el.RotationAngle = dto.Rotation; el.FlipHorizontal = dto.FlipH; el.FlipVertical = dto.FlipV;
            el.ZIndex = dto.Z;
            el.Opacity = dto.Opacity;
            el.SoftEdge = dto.SoftEdge;
            el.ShadowStyle = ParseShadowStyle(dto.ShadowStyle);
            el.ShadowX = dto.ShadowX;
            el.ShadowY = dto.ShadowY;
            el.ShadowBlur = dto.ShadowBlur;
            el.ShadowOpacity = dto.ShadowOpacity;
            el.ShadowColor = FromHex(dto.ShadowColor, Colors.Black);
            el.UseOpacityGradient = dto.OpacityGradient;
            el.OpacityDirection = ParseOpacityDirection(dto.OpacityDirection);
            el.OpacityStart = dto.OpacityStart;
            el.OpacityEnd = dto.OpacityGradient && dto.OpacityEnd <= 0 ? 100 : dto.OpacityEnd;
            el.OpacityStrength = dto.OpacityGradient && dto.OpacityStrength <= 0 ? 100 : dto.OpacityStrength;
            if (el is ImageElement im) im.CornerRadius = dto.CornerRadius;
            if (el is TextElement tx) tx.EnableAnchoring();
            doc.Elements.Add(el);
        }
        doc.NormalizeZ();
        doc.Select(null);
    }

    private static ImageElement? LoadImage(ElementDto dto)
    {
        if (string.IsNullOrEmpty(dto.ImageBase64)) return null;
        try
        {
            byte[] bytes = Convert.FromBase64String(dto.ImageBase64);
            using var ms = new MemoryStream(bytes);
            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.StreamSource = ms;
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            bmp.EndInit();
            bmp.Freeze();
            return new ImageElement { Source = bmp };
        }
        catch { return null; } // skip corrupt image, keep the rest
    }

    // ---------------- helpers ----------------

    public static byte[] EncodePng(BitmapSource bmp)
    {
        var enc = new PngBitmapEncoder();
        enc.Frames.Add(BitmapFrame.Create(bmp));
        using var ms = new MemoryStream();
        enc.Save(ms);
        return ms.ToArray();
    }

    private static string ToHex(Color c) => $"#{c.R:X2}{c.G:X2}{c.B:X2}";

    private static GradientType ParseGradientType(string? value) =>
        Enum.TryParse<GradientType>(value, ignoreCase: true, out var t) ? t : GradientType.Linear;

    private static ShadowStyle ParseShadowStyle(string? value) =>
        Enum.TryParse<ShadowStyle>(value, ignoreCase: true, out var s) ? s : ShadowStyle.None;

    private static OpacityFadeDirection ParseOpacityDirection(string? value) =>
        Enum.TryParse<OpacityFadeDirection>(value, ignoreCase: true, out var d)
            ? d
            : OpacityFadeDirection.TopToBottom;

    private static TextDirection ParseTextDirection(string? value) =>
        Enum.TryParse<TextDirection>(value, ignoreCase: true, out var d) ? d : TextDirection.Auto;

    private static TextAlign ParseTextAlign(string? value) =>
        Enum.TryParse<TextAlign>(value, ignoreCase: true, out var a) ? a : TextAlign.Right;

    private static Color FromHex(string? hex, Color fallback)
    {
        if (string.IsNullOrEmpty(hex)) return fallback;
        try { return (Color)ColorConverter.ConvertFromString(hex); }
        catch { return fallback; }
    }
}
