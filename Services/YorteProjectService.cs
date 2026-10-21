using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Windows;
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
        public double? ArtboardX { get; set; } // absent in older files: canvas is centered
        public double? ArtboardY { get; set; }
        public string? Primary { get; set; } // project primary color (#RRGGBB)
        public string? CanvasColor { get; set; } // canvas paper color (#RRGGBB)
        public bool CanvasTransparent { get; set; } // true = no background (exports transparent)
        public List<ElementDto> Elements { get; set; } = new();
    }

    public sealed class ElementDto
    {
        public string? Id { get; set; } // stable across undo snapshots (not just files)
        public string Kind { get; set; } = "shape"; // "shape" | "line" | "image" | "text" | "paint" | "group"
        public List<ElementDto>? Children { get; set; }
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
        public bool IndividualCorners { get; set; }
        public double CornerTopLeft { get; set; }
        public double CornerTopRight { get; set; }
        public double CornerBottomRight { get; set; }
        public double CornerBottomLeft { get; set; }
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
        public string? ShapeKind { get; set; }
        public List<PolygonPointDto>? PolygonPoints { get; set; }
        public string? CustomPathData { get; set; }
        // glass (shape or image)
        public bool GlassEnabled { get; set; }
        public string? GlassStyle { get; set; }
        public double GlassBlur { get; set; } = 18;
        public double GlassRefraction { get; set; }
        public double GlassRim { get; set; } = 45;
        public double GlassGloss { get; set; } = 10;
        public double GlassNoise { get; set; } = 2;
        public string? LinePattern { get; set; }
        public string? LineStartCap { get; set; }
        public string? LineEndCap { get; set; }
        public bool LineFreehand { get; set; }
        public bool LinePolyline { get; set; }
        public double LineCurveStrength { get; set; } = 50;
        public List<PolygonPointDto>? LinePoints { get; set; }
        public string? FillImageBase64 { get; set; }
        // text only
        public string? Text { get; set; }
        public string? FontFamily { get; set; }
        public double FontSize { get; set; }
        public bool Bold { get; set; }
        public bool Italic { get; set; }
        public bool Underline { get; set; }
        public bool Strikethrough { get; set; }
        public bool TextBorder { get; set; }
        public string? TextBorderColor { get; set; }
        public double TextBorderThickness { get; set; }
        public string? TextDirection { get; set; }
        public string? TextAlignment { get; set; }
        public double LetterSpacing { get; set; }
        public double LineSpacing { get; set; }
        public double WrapWidth { get; set; }
        // image only (embedded PNG)
        public string? ImageBase64 { get; set; }
        public string? ImageFit { get; set; }
        public PhotoAdjustDto? Photo { get; set; }
    }

    public sealed class PhotoAdjustDto
    {
        public double Exposure { get; set; }
        public double Contrast { get; set; }
        public double Highlights { get; set; }
        public double Shadows { get; set; }
        public double Whites { get; set; }
        public double Blacks { get; set; }
        public double Temperature { get; set; }
        public double Tint { get; set; }
        public double Vibrance { get; set; }
        public double Saturation { get; set; }
        public double Clarity { get; set; }
        public double Vignette { get; set; }
        public double Grain { get; set; }
        public double Blur { get; set; }
        public double Sharpen { get; set; }
        public double Glow { get; set; }
        public double ToneAmount { get; set; }
        public string? ToneColor { get; set; }
        public double RecolorAmount { get; set; }
        public string? RecolorColor { get; set; }
        /// <summary>Strength of the whole edit (100 = full). Null in projects saved before it existed.</summary>
        public double? Intensity { get; set; }
    }

    public sealed class PolygonPointDto
    {
        public double X { get; set; }
        public double Y { get; set; }
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
            ArtboardX = doc.ArtboardX,
            ArtboardY = doc.ArtboardY,
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
            IndividualCorners = el.UseIndividualCornerRadii,
            CornerTopLeft = el.IndividualCornerRadii.TopLeft,
            CornerTopRight = el.IndividualCornerRadii.TopRight,
            CornerBottomRight = el.IndividualCornerRadii.BottomRight,
            CornerBottomLeft = el.IndividualCornerRadii.BottomLeft,
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
                dto.ShapeKind = s.Kind.ToString();
                dto.GlassEnabled = s.Glass.Enabled;
                dto.GlassStyle = s.Glass.Style.ToString();
                dto.GlassBlur = s.Glass.Blur;
                dto.GlassRefraction = s.Glass.Refraction;
                dto.GlassRim = s.Glass.Rim;
                dto.GlassGloss = s.Glass.Gloss;
                dto.GlassNoise = s.Glass.Noise;
                if (s.FillImage is BitmapSource fillBmp)
                    dto.FillImageBase64 = Convert.ToBase64String(EncodePng(fillBmp));
                if (s.Kind == ShapeKind.Polygon)
                    dto.PolygonPoints = s.PolygonPoints
                        .Select(p => new PolygonPointDto { X = p.X, Y = p.Y })
                        .ToList();
                if (s.Kind == ShapeKind.CustomPath)
                    dto.CustomPathData = s.CustomPathData;
                break;
            case LineElement line:
                dto.Kind = "line";
                dto.Stroke = ToHex(line.StrokeColor);
                dto.StrokeThickness = line.StrokeThickness;
                dto.LinePattern = line.Pattern.ToString();
                dto.LineStartCap = line.StartCap.ToString();
                dto.LineEndCap = line.EndCap.ToString();
                dto.LineFreehand = line.IsFreehand;
                dto.LinePolyline = line.IsPolyline;
                dto.LineCurveStrength = line.CurveStrength;
                dto.LinePoints = line.PathPoints
                    .Select(p => new PolygonPointDto { X = p.X, Y = p.Y })
                    .ToList();
                break;
            case ImageElement im:
                dto.Kind = "image";
                dto.CornerRadius = im.CornerRadius;
                dto.ImageFit = im.FitMode.ToString();
                dto.GlassEnabled = im.Glass.Enabled;
                dto.GlassStyle = im.Glass.Style.ToString();
                dto.GlassBlur = im.Glass.Blur;
                dto.GlassRefraction = im.Glass.Refraction;
                dto.GlassRim = im.Glass.Rim;
                dto.GlassGloss = im.Glass.Gloss;
                dto.GlassNoise = im.Glass.Noise;
                if (im.Source is BitmapSource bmp)
                    dto.ImageBase64 = Convert.ToBase64String(EncodePng(bmp));
                if (!im.Adjustments.IsDefault)
                    dto.Photo = ToDto(im.Adjustments);
                break;
            case PaintLayerElement paint:
                dto.Kind = "paint";
                if (paint.Source is BitmapSource paintBmp)
                    dto.ImageBase64 = Convert.ToBase64String(EncodePng(paintBmp));
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
                dto.TextBorder = t.HasBorder;
                dto.TextBorderColor = ToHex(t.BorderColor);
                dto.TextBorderThickness = t.BorderThickness;
                dto.TextDirection = t.Direction.ToString();
                dto.TextAlignment = t.Alignment.ToString();
                dto.LetterSpacing = t.LetterSpacing; dto.LineSpacing = t.LineSpacing;
                dto.WrapWidth = t.WrapWidth;
                break;
            case GroupElement g:
                dto.Kind = "group";
                dto.Children = g.Children.Select(c => ToDto(c)).ToList();
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
            Math.Clamp(file.ArtboardWidth, 1, 8000),
            Math.Clamp(file.ArtboardHeight, 1, 8000));
        if (file.ArtboardX is double ax && file.ArtboardY is double ay)
            doc.SetArtboardRect(new Rect(ax, ay, doc.ArtboardWidth, doc.ArtboardHeight));

        doc.CanvasColor = FromHex(file.CanvasColor, Colors.White);
        doc.CanvasTransparent = file.CanvasTransparent;

        foreach (var dto in file.Elements.OrderBy(e => e.Z))
        {
            DesignElement? el = dto.Kind switch
            {
                "shape" => new ShapeElement
                {
                    CornerRadius = dto.CornerRadius,
                    Kind = ParseShapeKind(dto.ShapeKind),
                    FillColor1 = FromHex(dto.Fill1, Colors.Blue),
                    FillColor2 = FromHex(dto.Fill2, Colors.LightBlue),
                    IsGradient = dto.IsGradient, GradientAngle = dto.GradientAngle,
                    GradientType = ParseGradientType(dto.GradientType),
                    StrokeColor = FromHex(dto.Stroke, Colors.Navy),
                    StrokeThickness = dto.StrokeThickness,
                    CustomPathData = dto.CustomPathData,
                },
                "line" => LoadLine(dto),
                "image" => LoadImage(dto),
                "paint" => LoadPaintLayer(dto),
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
                    HasBorder = dto.TextBorder,
                    BorderColor = FromHex(dto.TextBorderColor, Colors.Black),
                    BorderThickness = dto.TextBorderThickness > 0 ? dto.TextBorderThickness : 1,
                    Direction = ParseTextDirection(dto.TextDirection),
                    Alignment = ParseTextAlign(dto.TextAlignment),
                    LetterSpacing = dto.LetterSpacing,
                    LineSpacing = dto.LineSpacing > 0 ? dto.LineSpacing : 120,
                    WrapWidth = dto.WrapWidth,
                },
                "group" => LoadGroup(dto),
                _ => null,
            };
            if (el == null) continue;
            if (Guid.TryParse(dto.Id, out var gid)) el.Id = gid; // keep identity for undo selection
            el.X = dto.X; el.Y = dto.Y;
            el.Width = Math.Max(1, dto.Width); el.Height = Math.Max(1, dto.Height);
            if (el is GroupElement loadedGroup) loadedGroup.CaptureOriginalSize();
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
            el.UseIndividualCornerRadii = dto.IndividualCorners;
            el.IndividualCornerRadii = new CornerRadius(dto.CornerTopLeft, dto.CornerTopRight,
                dto.CornerBottomRight, dto.CornerBottomLeft);
            if (el is ImageElement im) im.CornerRadius = dto.CornerRadius;
            if (el is ImageElement image && dto.Photo is { } photo)
            {
                FromDto(photo, image.Adjustments);
                image.RefreshPhotoPreview();
            }
            if (el is ImageElement fitted)
                fitted.FitMode = ParseImageFit(dto.ImageFit);
            if (el is ShapeElement shp && dto.PolygonPoints is { Count: >= 3 })
                shp.SetPolygonPoints(dto.PolygonPoints.Select(p => new Point(p.X, p.Y)));
            if (el is ShapeElement filled && !string.IsNullOrEmpty(dto.FillImageBase64))
            {
                var fillBmp = DecodeBitmap(dto.FillImageBase64);
                if (fillBmp != null) filled.FillImage = fillBmp;
            }
            GlassEffect? glass = el switch
            {
                ShapeElement shape => shape.Glass,
                ImageElement imageEl => imageEl.Glass,
                _ => null,
            };
            if (glass != null) ApplyGlass(dto, glass);
            if (el is TextElement tx) tx.EnableAnchoring();
            doc.Elements.Add(el);
        }
        doc.NormalizeZ();
        doc.Select(null);
    }

    private static void ApplyGlass(ElementDto dto, GlassEffect glass)
    {
        glass.Enabled = dto.GlassEnabled;
        if (Enum.TryParse<GlassStyle>(dto.GlassStyle, out var style))
            glass.Style = style;
        glass.Blur = dto.GlassBlur;
        glass.Refraction = dto.GlassRefraction;
        glass.Rim = dto.GlassRim;
        glass.Gloss = dto.GlassGloss;
        glass.Noise = dto.GlassNoise;
    }

    private static ImageElement? LoadImage(ElementDto dto)
    {
        if (string.IsNullOrEmpty(dto.ImageBase64)) return null;
        var bmp = DecodeBitmap(dto.ImageBase64);
        return bmp == null ? null : new ImageElement { Source = bmp };
    }

    private static PaintLayerElement? LoadPaintLayer(ElementDto dto)
    {
        if (string.IsNullOrEmpty(dto.ImageBase64)) return new PaintLayerElement();
        var bmp = DecodeBitmap(dto.ImageBase64);
        return bmp == null ? null : new PaintLayerElement { Source = bmp };
    }

    private static LineElement? LoadLine(ElementDto dto)
    {
        if (dto.LinePoints is not { Count: >= 2 }) return null;
        var line = new LineElement
        {
            StrokeColor = FromHex(dto.Stroke, Colors.Black),
            StrokeThickness = dto.StrokeThickness > 0 ? dto.StrokeThickness : 4,
            Pattern = ParseLinePattern(dto.LinePattern),
            StartCap = ParseLineCap(dto.LineStartCap),
            EndCap = ParseLineCap(dto.LineEndCap),
            IsFreehand = dto.LineFreehand,
            IsPolyline = dto.LinePolyline,
            CurveStrength = dto.LineCurveStrength,
        };
        line.SetPathPoints(dto.LinePoints.Select(p => new Point(p.X, p.Y)));
        return line;
    }

    private static GroupElement? LoadGroup(ElementDto dto)
    {
        if (dto.Children is null) return null;
        var group = new GroupElement();
        foreach (var childDto in dto.Children.OrderBy(c => c.Z))
        {
            DesignElement? child = childDto.Kind switch
            {
                "shape" => new ShapeElement
                {
                    CornerRadius = childDto.CornerRadius,
                    Kind = ParseShapeKind(childDto.ShapeKind),
                    FillColor1 = FromHex(childDto.Fill1, Colors.Blue),
                    FillColor2 = FromHex(childDto.Fill2, Colors.LightBlue),
                    IsGradient = childDto.IsGradient, GradientAngle = childDto.GradientAngle,
                    GradientType = ParseGradientType(childDto.GradientType),
                    StrokeColor = FromHex(childDto.Stroke, Colors.Navy),
                    StrokeThickness = childDto.StrokeThickness,
                    CustomPathData = childDto.CustomPathData,
                },
                "line" => LoadLine(childDto),
                "image" => LoadImage(childDto),
                "paint" => LoadPaintLayer(childDto),
                "text" => new TextElement
                {
                    Text = childDto.Text ?? "",
                    FontFamilyName = childDto.FontFamily ?? "Segoe UI",
                    FontSize = childDto.FontSize > 0 ? childDto.FontSize : 32,
                    TextColor1 = FromHex(childDto.Fill1, Color.FromRgb(0x1E, 0x29, 0x3B)),
                    TextColor2 = FromHex(childDto.Fill2, Color.FromRgb(0x2E, 0x9B, 0xFF)),
                    IsGradient = childDto.IsGradient,
                    TextGradientAngle = childDto.GradientAngle,
                    IsBold = childDto.Bold,
                    IsItalic = childDto.Italic,
                    IsUnderline = childDto.Underline,
                    IsStrikethrough = childDto.Strikethrough,
                    HasBorder = childDto.TextBorder,
                    BorderColor = FromHex(childDto.TextBorderColor, Colors.Black),
                    BorderThickness = childDto.TextBorderThickness > 0 ? childDto.TextBorderThickness : 1,
                    Direction = ParseTextDirection(childDto.TextDirection),
                    Alignment = ParseTextAlign(childDto.TextAlignment),
                    LetterSpacing = childDto.LetterSpacing,
                    LineSpacing = childDto.LineSpacing > 0 ? childDto.LineSpacing : 120,
                    WrapWidth = childDto.WrapWidth,
                },
                "group" => LoadGroup(childDto),
                _ => null,
            };
            if (child is LineElement line)
            {
                if (Guid.TryParse(childDto.Id, out var id)) line.Id = id;
                line.X = childDto.X;
                line.Y = childDto.Y;
                line.Width = Math.Max(1, childDto.Width);
                line.Height = Math.Max(1, childDto.Height);
                line.RotationAngle = childDto.Rotation;
                line.FlipHorizontal = childDto.FlipH;
                line.FlipVertical = childDto.FlipV;
                line.ZIndex = childDto.Z;
                line.Opacity = childDto.Opacity;
                line.SoftEdge = childDto.SoftEdge;
                line.ShadowStyle = ParseShadowStyle(childDto.ShadowStyle);
                line.ShadowX = childDto.ShadowX;
                line.ShadowY = childDto.ShadowY;
                line.ShadowBlur = childDto.ShadowBlur;
                line.ShadowOpacity = childDto.ShadowOpacity;
                line.ShadowColor = FromHex(childDto.ShadowColor, Colors.Black);
                line.UseOpacityGradient = childDto.OpacityGradient;
                line.OpacityDirection = ParseOpacityDirection(childDto.OpacityDirection);
                line.OpacityStart = childDto.OpacityStart;
                line.OpacityEnd = childDto.OpacityGradient && childDto.OpacityEnd <= 0 ? 100 : childDto.OpacityEnd;
                line.OpacityStrength = childDto.OpacityGradient && childDto.OpacityStrength <= 0 ? 100 : childDto.OpacityStrength;
            }
            GlassEffect? childGlass = child switch
            {
                ShapeElement childShape => childShape.Glass,
                ImageElement childImage => childImage.Glass,
                _ => null,
            };
            if (childGlass != null) ApplyGlass(childDto, childGlass);
            if (child != null) group.Children.Add(child);
        }
        if (group.Children.Count == 0) return null;
        return group;
    }

    private static BitmapSource? DecodeBitmap(string base64)
    {
        try
        {
            byte[] bytes = Convert.FromBase64String(base64);
            using var ms = new MemoryStream(bytes);
            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.StreamSource = ms;
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            bmp.EndInit();
            bmp.Freeze();
            return bmp;
        }
        catch { return null; } // skip corrupt image, keep the rest
    }

    // ---------------- helpers ----------------

    private static PhotoAdjustDto ToDto(PhotoAdjustments a) => new()
    {
        Exposure = a.Exposure,
        Contrast = a.Contrast,
        Highlights = a.Highlights,
        Shadows = a.Shadows,
        Whites = a.Whites,
        Blacks = a.Blacks,
        Temperature = a.Temperature,
        Tint = a.Tint,
        Vibrance = a.Vibrance,
        Saturation = a.Saturation,
        Clarity = a.Clarity,
        Vignette = a.Vignette,
        Grain = a.Grain,
        Blur = a.Blur,
        Sharpen = a.Sharpen,
        Glow = a.Glow,
        ToneAmount = a.ToneAmount,
        ToneColor = ToHex(a.ToneColor),
        RecolorAmount = a.RecolorAmount,
        RecolorColor = ToHex(a.RecolorColor),
        Intensity = a.Intensity,
    };

    private static void FromDto(PhotoAdjustDto dto, PhotoAdjustments a)
    {
        a.Exposure = dto.Exposure;
        a.Contrast = dto.Contrast;
        a.Highlights = dto.Highlights;
        a.Shadows = dto.Shadows;
        a.Whites = dto.Whites;
        a.Blacks = dto.Blacks;
        a.Temperature = dto.Temperature;
        a.Tint = dto.Tint;
        a.Vibrance = dto.Vibrance;
        a.Saturation = dto.Saturation;
        a.Clarity = dto.Clarity;
        a.Vignette = dto.Vignette;
        a.Grain = dto.Grain;
        a.Blur = dto.Blur;
        a.Sharpen = dto.Sharpen;
        a.Glow = dto.Glow;
        a.ToneAmount = dto.ToneAmount;
        a.ToneColor = FromHex(dto.ToneColor, PhotoAdjustments.DefaultToneColor);
        a.RecolorAmount = dto.RecolorAmount;
        a.RecolorColor = FromHex(dto.RecolorColor, Colors.White);
        a.Intensity = dto.Intensity ?? 100; // projects saved before it existed read as full strength
    }

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

    private static ShapeKind ParseShapeKind(string? value) =>
        Enum.TryParse<ShapeKind>(value, ignoreCase: true, out var k) ? k : ShapeKind.Rectangle;

    private static LinePattern ParseLinePattern(string? value) =>
        Enum.TryParse<LinePattern>(value, ignoreCase: true, out var p) ? p : LinePattern.Solid;

    private static LineEndpointStyle ParseLineCap(string? value) =>
        string.Equals(value, "Triangle", StringComparison.OrdinalIgnoreCase)
            ? LineEndpointStyle.OpenArrow
            : Enum.TryParse<LineEndpointStyle>(value, ignoreCase: true, out var cap)
                ? cap
                : LineEndpointStyle.Flat;

    private static ShadowStyle ParseShadowStyle(string? value) =>
        Enum.TryParse<ShadowStyle>(value, ignoreCase: true, out var s) ? s : ShadowStyle.None;

    private static ImageFitMode ParseImageFit(string? value) =>
        Enum.TryParse<ImageFitMode>(value, ignoreCase: true, out var f) ? f : ImageFitMode.Fill;

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
