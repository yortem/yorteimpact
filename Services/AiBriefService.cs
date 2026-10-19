using System.Globalization;
using System.Reflection;
using System.Text;
using System.Windows.Media;
using YorteImpact.Models;
using YorteImpact.ViewModels;

namespace YorteImpact.Services;

/// <summary>
/// Builds a compact, readable description of the current sketch for an AI.
/// It is derived from the document model, so new writable properties and
/// element types are included automatically through reflection.
/// </summary>
public static class AiBriefService
{
    private static readonly HashSet<string> SkipProperties = new()
    {
        nameof(DesignElement.Id), nameof(DesignElement.IsSelected), nameof(DesignElement.ZIndex),
        nameof(DesignElement.X), nameof(DesignElement.Y),
        nameof(DesignElement.Width), nameof(DesignElement.Height),
        nameof(DesignElement.Center), nameof(DesignElement.Bounds),
        nameof(DesignElement.OpacityFactor), nameof(DesignElement.SoftEdgeMask),
        nameof(DesignElement.OpacityMaskBrush), nameof(DesignElement.ShadowEffect),
        nameof(DesignElement.FlipScaleX), nameof(DesignElement.FlipScaleY),
        "Source", "Adjustments", "DisplaySource", "ImageBrush",
        "FillBrush", "BorderBrush", "ShapeGeometry", "UseSimpleRender",
        "FontFamilyValue", "LogicalAlignment", "IsRtl", "LineHeight",
    };

    public static string Build(DesignDocument doc)
    {
        var sb = new StringBuilder();
        sb.AppendLine("WEBSITE SECTION SKETCH — AI BRIEF");
        sb.AppendLine();
        sb.AppendLine($"Canvas: {doc.ArtboardWidth:0} × {doc.ArtboardHeight:0} px");
        sb.AppendLine(doc.CanvasTransparent
            ? "Background: transparent"
            : $"Background: {Hex(doc.CanvasColor)}");

        var palette = Palette(doc);
        if (palette.Count > 0)
            sb.AppendLine("Palette: " + string.Join(", ", palette));

        sb.AppendLine("Coordinates are absolute pixels from the canvas top-left. Elements are listed back-to-front (Z-order).");
        sb.AppendLine("Embedded image data is omitted; use the exported sketch PNG as the visual reference.");
        sb.AppendLine();

        int index = 1;
        foreach (var el in doc.Elements
                     .Where(e => !e.IsBackgroundLayer)
                     .OrderBy(e => e.ZIndex))
        {
            sb.AppendLine($"{index++}. {DescribeElement(el)}");
            string properties = DescribeProperties(el);
            if (properties.Length > 0)
                sb.AppendLine("   Properties: " + properties);
            sb.AppendLine();
        }

        if (doc.Elements.FirstOrDefault(e => e.IsBackgroundLayer) is PaintLayerElement paint)
        {
            string size = paint.Source is System.Windows.Media.Imaging.BitmapSource bmp
                ? $"{bmp.PixelWidth}×{bmp.PixelHeight} px"
                : "embedded raster";
            sb.AppendLine($"Background paint layer: {size}; flatten it into the section background.");
            sb.AppendLine();
        }

        sb.AppendLine("Task: infer the website-section structure from this sketch and recreate it as responsive HTML + Tailwind CSS.");
        sb.AppendLine("Treat the absolute positions as a desktop reference; infer semantic sections, rows and columns where obvious.");
        sb.AppendLine("Preserve the hierarchy, palette, spacing, typography, rounded corners, shadows and image crops.");
        sb.AppendLine("Use placeholder asset names for embedded images.");
        return sb.ToString();
    }

    private static string DescribeElement(DesignElement el)
    {
        string geometry = $"at {el.X:0},{el.Y:0} · {el.Width:0}×{el.Height:0} px";
        string rotation = Math.Abs(el.RotationAngle) > 0.01 ? $" · rotation {el.RotationAngle:0}°" : "";
        string flip = el.FlipHorizontal || el.FlipVertical
            ? $" · flip {(el.FlipHorizontal ? "H" : "")}{(el.FlipVertical ? "V" : "")}"
            : "";

        return el switch
        {
            ShapeElement s =>
                $"Shape ({s.Kind}) {geometry}{rotation}{flip}",
            ImageElement im =>
                $"Image {geometry}{rotation}{flip} · fit {im.FitMode}" +
                (im.Source is System.Windows.Media.Imaging.BitmapSource bmp
                    ? $" · source {bmp.PixelWidth}×{bmp.PixelHeight} px"
                    : ""),
            TextElement t =>
                $"Text \"{Preview(t.Text, 80)}\" {geometry}{rotation}{flip}",
            PaintLayerElement =>
                $"Background paint layer {geometry}",
            _ =>
                $"{el.GetType().Name} {geometry}{rotation}{flip}",
        };
    }

    private static string DescribeProperties(DesignElement el)
    {
        var parts = new List<string>();
        foreach (var property in el.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            if (!property.CanRead || !property.CanWrite || property.GetIndexParameters().Length > 0)
                continue;
            if (SkipProperties.Contains(property.Name))
                continue;
            if (!IsSimple(property.PropertyType))
                continue;
            if (el.ShadowStyle == ShadowStyle.None && property.Name.StartsWith("Shadow", StringComparison.Ordinal))
                continue;
            if (!el.UseOpacityGradient && property.Name.StartsWith("Opacity", StringComparison.Ordinal) &&
                property.Name != nameof(DesignElement.Opacity))
                continue;
            if (el is ShapeElement shape && !shape.IsGradient &&
                property.Name == nameof(ShapeElement.FillColor2))
                continue;
            if (el is TextElement text)
            {
                if (!text.IsGradient && property.Name == nameof(TextElement.TextColor2))
                    continue;
                if (!text.HasBorder && property.Name is nameof(TextElement.BorderColor) or nameof(TextElement.BorderThickness))
                    continue;
            }

            object? value = property.GetValue(el);
            if (IsDefault(value) || (value is string s && string.IsNullOrWhiteSpace(s)))
                continue;

            parts.Add($"{property.Name}={Format(value)}");
        }

        if (el is ImageElement { Adjustments: { } adjustments } && !adjustments.IsDefault)
        {
            string tone = adjustments.ToneAmount > 0 ? $"ToneColor={Hex(adjustments.ToneColor)}; " : "";
            parts.Add("PhotoAdjustments: " + tone + DescribeSimpleObject(adjustments));
        }

        return string.Join("; ", parts.Where(p => p.Length > 0));
    }

    private static string DescribeSimpleObject(object value)
    {
        var parts = new List<string>();
        foreach (var property in value.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            if (!property.CanRead || !property.CanWrite || !IsSimple(property.PropertyType))
                continue;
            object? propertyValue = property.GetValue(value);
            if (IsDefault(propertyValue)) continue;
            if (property.PropertyType == typeof(Color) && property.Name == "ToneColor") continue;
            parts.Add($"{property.Name}={Format(propertyValue)}");
        }
        return string.Join("; ", parts);
    }

    private static List<string> Palette(DesignDocument doc)
    {
        var colors = new List<string>();
        if (!doc.CanvasTransparent) Add(doc.CanvasColor);

        foreach (var el in doc.Elements)
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
                    if (t.HasBorder) Add(t.BorderColor);
                    break;
                case ImageElement im:
                    if (im.Adjustments.ToneAmount > 0) Add(im.Adjustments.ToneColor);
                    break;
                default:
                    foreach (var property in el.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance))
                    {
                        if (!property.CanRead || property.PropertyType != typeof(Color)) continue;
                        if (property.GetValue(el) is Color color) Add(color);
                    }
                    break;
            }
        }

        return colors;

        void Add(Color color)
        {
            if (color.A == 0) return;
            string hex = Hex(color);
            if (!colors.Contains(hex)) colors.Add(hex);
        }
    }

    private static bool IsSimple(Type type) =>
        type.IsPrimitive || type.IsEnum || type == typeof(string) || type == typeof(Color) ||
        type == typeof(double) || type == typeof(float) || type == typeof(decimal) ||
        type == typeof(int) || type == typeof(long) || type == typeof(bool);

    private static bool IsDefault(object? value) => value switch
    {
        null => true,
        bool b => !b,
        string s => s.Length == 0,
        Color c => c.A == 0,
        double d => Math.Abs(d) < 0.0001,
        float f => Math.Abs(f) < 0.0001f,
        decimal m => m == 0,
        int i => i == 0,
        long l => l == 0,
        _ => false,
    };

    private static string Format(object? value) => value switch
    {
        bool b => b ? "true" : "false",
        Color c => Hex(c),
        double d => d.ToString("0.##", CultureInfo.InvariantCulture),
        float f => f.ToString("0.##", CultureInfo.InvariantCulture),
        decimal m => m.ToString("0.##", CultureInfo.InvariantCulture),
        IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
        _ => value?.ToString() ?? "",
    };

    private static string Hex(Color c) => $"#{c.R:X2}{c.G:X2}{c.B:X2}";

    private static string Preview(string? text, int max)
    {
        if (string.IsNullOrEmpty(text)) return "";
        string singleLine = text.Replace("\r", "").Replace("\n", "\\n");
        return singleLine.Length <= max ? singleLine : singleLine[..max] + "…";
    }
}
