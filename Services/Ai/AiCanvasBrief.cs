using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization.Metadata;
using System.Windows.Media;
using YorteImpact.Models;
using YorteImpact.ViewModels;

namespace YorteImpact.Services.Ai;

/// <summary>
/// Builds the compact canvas description the model receives every round:
/// canvas size, palette and one object per element with a short, stable id.
/// Ids are assigned per round (by Z-order) and never reused across rounds.
/// </summary>
public static class AiCanvasBrief
{
    private static readonly JsonSerializerOptions CompactOptions = new()
    {
        WriteIndented = false,
        TypeInfoResolver = new DefaultJsonTypeInfoResolver(),
    };

    public static string BuildJson(DesignDocument doc) => Build(doc).Json;

    /// <summary>
    /// The JSON sent to the model plus the id → element map used to resolve
    /// command targets. Both are built from the same ordering in one pass.
    /// </summary>
    public static (string Json, IReadOnlyDictionary<string, DesignElement> Map) Build(DesignDocument doc)
    {
        var map = new Dictionary<string, DesignElement>(StringComparer.OrdinalIgnoreCase);
        var elements = new JsonArray();

        int index = 1;
        foreach (var el in doc.Elements.Where(e => !e.IsBackgroundLayer).OrderBy(e => e.ZIndex))
        {
            string id = "e" + index.ToString(CultureInfo.InvariantCulture);
            index++;
            map[id] = el;
            elements.Add(Describe(doc, id, el));
        }

        if (doc.CanvasPaintLayer is { } paint)
        {
            map["paint"] = paint;
            elements.Add(Describe(doc, "paint", paint));
        }

        var root = new JsonObject
        {
            ["canvas"] = new JsonObject
            {
                ["width"] = Round(doc.ArtboardWidth),
                ["height"] = Round(doc.ArtboardHeight),
                ["background"] = doc.CanvasTransparent ? null : Hex(doc.CanvasColor),
                ["transparent"] = doc.CanvasTransparent,
            },
            ["palette"] = Palette(doc),
            ["elements"] = elements,
        };

        return (root.ToJsonString(CompactOptions), map);
    }

    /// <summary>
    /// Fingerprint of the document used to detect edits made while a request
    /// is in flight; commands are never applied to a document that changed.
    /// </summary>
    public static string Signature(DesignDocument doc) => BuildJson(doc);

    /// <summary>Short human/AI label: type, content and where it sits on the canvas.</summary>
    public static string LabelOf(DesignDocument doc, DesignElement el)
    {
        string position = PositionOf(doc, el);
        return el switch
        {
            ShapeElement s =>
                $"{s.Kind} {Hex(s.FillColor1)}{(s.IsGradient ? " → " + Hex(s.FillColor2) : "")}, {Round(s.Width)}×{Round(s.Height)}, {position}",
            TextElement t =>
                $"Text \"{Preview(t.Text, 48)}\", {Round(t.FontSize)}px, {position}",
            ImageElement im =>
                $"Image {Round(im.Width)}×{Round(im.Height)}{(im.HasTransparency ? ", transparent background" : "")}, {position}",
            PaintLayerElement => "Canvas paint layer",
            GroupElement g => $"Group of {g.Children.Count}, {position}",
            LineElement l => $"Line, {Round(l.Width)}×{Round(l.Height)}, {position}",
            _ => $"{el.GetType().Name}, {position}",
        };
    }

    /// <summary>Semantic role inferred from type, size, position and text.</summary>
    public static string RoleOf(DesignDocument doc, DesignElement el)
    {
        if (el.IsBackgroundLayer) return "background";

        double artboardArea = Math.Max(1, doc.ArtboardWidth * doc.ArtboardHeight);
        if (el is ShapeElement or ImageElement or GroupElement)
        {
            double coverage = el.Width * el.Height / artboardArea;
            if (coverage >= 0.55 || (el.ZIndex <= 1 && coverage >= 0.25)) return "background";
        }

        if (el is TextElement t)
        {
            if (t.FontSize >= 30) return "heading";
            if (t.FontSize <= 16) return "caption";

            string text = (t.Text ?? "").Trim();
            if (text.Length is > 0 and <= 28 && t.FontSize is >= 17 and <= 26) return "button";
            return "body";
        }

        return "body";
    }

    private static JsonObject Describe(DesignDocument doc, string id, DesignElement el)
    {
        var node = new JsonObject
        {
            ["id"] = id,
            ["type"] = TypeOf(el),
            ["x"] = Round(el.X - doc.ArtboardX),
            ["y"] = Round(el.Y - doc.ArtboardY),
            ["width"] = Round(el.Width),
            ["height"] = Round(el.Height),
            ["z"] = el.ZIndex,
            ["label"] = LabelOf(doc, el),
            ["role"] = RoleOf(doc, el),
        };

        if (el is ShapeElement shape) node["kind"] = shape.Kind.ToString();
        if (el is TextElement text) node["text"] = text.Text ?? "";
        if (el is ImageElement image) node["transparent"] = image.HasTransparency;
        if (el is GroupElement group) node["children"] = group.Children.Count;

        var props = PropertiesOf(el);
        if (props.Count > 0) node["props"] = props;
        return node;
    }

    private static string TypeOf(DesignElement el) => el switch
    {
        ShapeElement => "shape",
        TextElement => "text",
        ImageElement => "image",
        PaintLayerElement => "paint",
        GroupElement => "group",
        LineElement => "line",
        _ => el.GetType().Name.ToLowerInvariant(),
    };

    /// <summary>Only non-default values, so the brief stays small.</summary>
    private static JsonObject PropertiesOf(DesignElement el)
    {
        var props = new JsonObject();

        if (Math.Abs(el.Opacity - 100) > 0.01) props["Opacity"] = Round(el.Opacity);
        if (el.SoftEdge > 0.01) props["SoftEdge"] = Round(el.SoftEdge);
        if (el.ShadowStyle != ShadowStyle.None)
        {
            props["ShadowStyle"] = el.ShadowStyle.ToString();
            props["ShadowX"] = Round(el.ShadowX);
            props["ShadowY"] = Round(el.ShadowY);
            props["ShadowBlur"] = Round(el.ShadowBlur);
            props["ShadowOpacity"] = Round(el.ShadowOpacity);
            props["ShadowColor"] = Hex(el.ShadowColor);
        }
        if (el.UseOpacityGradient)
        {
            props["UseOpacityGradient"] = true;
            props["OpacityDirection"] = el.OpacityDirection.ToString();
            props["OpacityStart"] = Round(el.OpacityStart);
            props["OpacityEnd"] = Round(el.OpacityEnd);
            props["OpacityStrength"] = Round(el.OpacityStrength);
        }
        if (Math.Abs(el.RotationAngle) > 0.01) props["Rotation"] = Round(el.RotationAngle);
        if (el.FlipHorizontal) props["FlipH"] = true;
        if (el.FlipVertical) props["FlipV"] = true;

        switch (el)
        {
            case ShapeElement s:
                props["FillColor1"] = Hex(s.FillColor1);
                props["IsGradient"] = s.IsGradient;
                if (s.IsGradient)
                {
                    props["FillColor2"] = Hex(s.FillColor2);
                    props["GradientType"] = s.GradientType.ToString();
                    props["GradientAngle"] = Round(s.GradientAngle);
                }
                if (s.StrokeThickness > 0.01)
                {
                    props["StrokeColor"] = Hex(s.StrokeColor);
                    props["StrokeThickness"] = Round(s.StrokeThickness);
                }
                if (s.CornerRadius > 0.01) props["CornerRadius"] = Round(s.CornerRadius);
                break;

            case TextElement t:
                props["FontSize"] = Round(t.FontSize);
                if (!string.Equals(t.FontFamilyName, "Segoe UI", StringComparison.OrdinalIgnoreCase))
                    props["FontFamilyName"] = t.FontFamilyName;
                if (t.IsBold) props["IsBold"] = true;
                if (t.IsItalic) props["IsItalic"] = true;
                if (t.IsUnderline) props["IsUnderline"] = true;
                if (t.IsStrikethrough) props["IsStrikethrough"] = true;
                props["TextColor1"] = Hex(t.TextColor1);
                if (t.IsGradient)
                {
                    props["IsGradient"] = true;
                    props["TextColor2"] = Hex(t.TextColor2);
                    props["TextGradientAngle"] = Round(t.TextGradientAngle);
                }
                if (t.Alignment != TextAlign.Right) props["Alignment"] = t.Alignment.ToString();
                if (t.Direction != TextDirection.Auto) props["Direction"] = t.Direction.ToString();
                if (t.LetterSpacing > 0.01) props["LetterSpacing"] = Round(t.LetterSpacing);
                if (Math.Abs(t.LineSpacing - 120) > 0.01) props["LineSpacing"] = Round(t.LineSpacing);
                if (t.WrapWidth > 0.01) props["WrapWidth"] = Round(t.WrapWidth);
                if (t.HasBorder)
                {
                    props["HasBorder"] = true;
                    props["BorderColor"] = Hex(t.BorderColor);
                    props["BorderThickness"] = Round(t.BorderThickness);
                }
                break;

            case ImageElement im:
                if (im.FitMode != ImageFitMode.Fill) props["FitMode"] = im.FitMode.ToString();
                if (im.CornerRadius > 0.01) props["CornerRadius"] = Round(im.CornerRadius);
                if (im.Adjustments.HasAdjustments || im.Adjustments.Intensity != 100)
                    props["Photo"] = PhotoProperties(im.Adjustments);
                break;

            case LineElement line:
                props["StrokeColor"] = Hex(line.StrokeColor);
                props["StrokeThickness"] = Round(line.StrokeThickness);
                break;
        }

        return props;
    }

    private static JsonObject PhotoProperties(PhotoAdjustments a)
    {
        var props = new JsonObject();
        void Add(string name, double value)
        {
            if (Math.Abs(value) > 0.0001) props[name] = Round(value);
        }

        Add(nameof(a.Exposure), a.Exposure);
        Add(nameof(a.Contrast), a.Contrast);
        Add(nameof(a.Highlights), a.Highlights);
        Add(nameof(a.Shadows), a.Shadows);
        Add(nameof(a.Whites), a.Whites);
        Add(nameof(a.Blacks), a.Blacks);
        Add(nameof(a.Temperature), a.Temperature);
        Add(nameof(a.Tint), a.Tint);
        Add(nameof(a.Vibrance), a.Vibrance);
        Add(nameof(a.Saturation), a.Saturation);
        Add(nameof(a.Clarity), a.Clarity);
        Add(nameof(a.Vignette), a.Vignette);
        Add(nameof(a.Grain), a.Grain);
        Add(nameof(a.Blur), a.Blur);
        Add(nameof(a.Sharpen), a.Sharpen);
        Add(nameof(a.Glow), a.Glow);
        Add(nameof(a.ToneAmount), a.ToneAmount);
        if (a.ToneAmount > 0) props["ToneColor"] = Hex(a.ToneColor);
        Add(nameof(a.RecolorAmount), a.RecolorAmount);
        if (a.RecolorAmount > 0) props["RecolorColor"] = Hex(a.RecolorColor);
        if (Math.Abs(a.Intensity - 100) > 0.01) props["Intensity"] = Round(a.Intensity);
        return props;
    }

    private static JsonArray Palette(DesignDocument doc)
    {
        var colors = new List<string>();

        void Add(Color color)
        {
            if (color.A == 0 || colors.Count >= 8) return;
            string hex = Hex(color);
            if (!colors.Contains(hex)) colors.Add(hex);
        }

        if (!doc.CanvasTransparent) Add(doc.CanvasColor);

        foreach (var el in doc.Elements.OrderBy(e => e.ZIndex))
        {
            switch (el)
            {
                case ShapeElement s:
                    Add(s.FillColor1);
                    if (s.IsGradient) Add(s.FillColor2);
                    if (s.StrokeThickness > 0) Add(s.StrokeColor);
                    break;
                case TextElement t:
                    Add(t.TextColor1);
                    if (t.IsGradient) Add(t.TextColor2);
                    break;
                case ImageElement im:
                    if (im.Adjustments.ToneAmount > 0) Add(im.Adjustments.ToneColor);
                    break;
            }
        }

        var array = new JsonArray();
        foreach (string color in colors) array.Add(color);
        return array;
    }

    private static string PositionOf(DesignDocument doc, DesignElement el)
    {
        double cx = el.X + el.Width / 2;
        double cy = el.Y + el.Height / 2;
        double thirdW = doc.ArtboardWidth / 3;
        double thirdH = doc.ArtboardHeight / 3;

        string vertical = cy < doc.ArtboardY + thirdH ? "top"
            : cy > doc.ArtboardY + thirdH * 2 ? "bottom"
            : "middle";
        string horizontal = cx < doc.ArtboardX + thirdW ? "left"
            : cx > doc.ArtboardX + thirdW * 2 ? "right"
            : "center";

        return vertical == "middle" && horizontal == "center" ? "center" : $"{vertical}-{horizontal}";
    }

    private static string Preview(string? text, int max)
    {
        if (string.IsNullOrEmpty(text)) return "";
        string single = text.Replace("\r", "").Replace("\n", "\\n");
        return single.Length <= max ? single : single[..max] + "…";
    }

    private static string Hex(Color c) => c.A == 255
        ? $"#{c.R:X2}{c.G:X2}{c.B:X2}"
        : $"#{c.A:X2}{c.R:X2}{c.G:X2}{c.B:X2}";

    private static double Round(double value) => Math.Round(value, 2);
}
