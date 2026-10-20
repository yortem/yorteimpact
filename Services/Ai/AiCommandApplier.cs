using System.Globalization;
using System.Text.Json;
using System.Windows;
using System.Windows.Media;
using YorteImpact.Models;
using YorteImpact.ViewModels;

namespace YorteImpact.Services.Ai;

/// <summary>A destructive action the user has to approve before it runs.</summary>
public sealed class AiDeleteRequest
{
    public IReadOnlyList<string> Ids { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> Labels { get; init; } = Array.Empty<string>();
}

public sealed class AiValidatedCommand
{
    public AiCommand Command { get; init; } = new();
    public List<DesignElement> Targets { get; init; } = new();
    public bool IsDestructive { get; init; }
}

public sealed class AiValidationResult
{
    public List<AiValidatedCommand> Commands { get; } = new();
    public List<string> Errors { get; } = new();

    public bool HasDestructive => Commands.Any(c => c.IsDestructive);
}

public sealed class AiApplyReport
{
    public List<string> Applied { get; } = new();
    public List<string> Errors { get; } = new();
    public bool CanvasChanged { get; set; }
}

/// <summary>
/// Validates a command batch against the current document and then applies it.
/// Validation runs on throwaway clones, so a rejected command leaves the
/// document untouched; application reports a failure per command and keeps
/// going. The caller supplies <c>PushUndo</c> once for the whole batch.
/// </summary>
public static class AiCommandApplier
{
    private const int MaxCommands = 40;
    private const int MaxElements = 300;

    private static readonly HashSet<string> GeometryProps =
        Set("X", "Y", "Width", "Height", "Rotation", "FlipH", "FlipV");

    private static readonly HashSet<string> CommonStyleProps = Set(
        "Opacity", "SoftEdge", "ShadowStyle", "ShadowX", "ShadowY", "ShadowBlur",
        "ShadowOpacity", "ShadowColor", "UseOpacityGradient", "OpacityDirection",
        "OpacityStart", "OpacityEnd", "OpacityStrength", "CornerRadius");

    private static readonly HashSet<string> ShapeStyleProps = Set(
        "Kind", "FillColor1", "FillColor2", "IsGradient", "GradientType",
        "GradientAngle", "StrokeColor", "StrokeThickness");

    private static readonly HashSet<string> TextStyleProps = Set(
        "Text", "FontFamilyName", "FontSize", "IsBold", "IsItalic", "IsUnderline",
        "IsStrikethrough", "Alignment", "Direction", "LetterSpacing", "LineSpacing",
        "WrapWidth", "TextColor1", "TextColor2", "IsGradient", "TextGradientAngle",
        "HasBorder", "BorderColor", "BorderThickness");

    private static readonly HashSet<string> PhotoProps = Set(
        "Reset", "Exposure", "Contrast", "Highlights", "Shadows", "Whites", "Blacks",
        "Temperature", "Tint", "Vibrance", "Saturation", "Clarity", "Vignette", "Grain",
        "Blur", "Sharpen", "Glow", "ToneAmount", "ToneColor", "RecolorAmount",
        "RecolorColor", "Intensity");

    private static readonly HashSet<string> AddShapeProps = Merge(
        GeometryProps, CommonStyleProps, ShapeStyleProps, Set("Behind"));
    private static readonly HashSet<string> AddTextProps = Merge(GeometryProps, CommonStyleProps, TextStyleProps);
    private static readonly HashSet<string> AddImageProps = Set("From", "SourceId", "X", "Y", "Width", "Height", "CornerRadius");
    private static readonly HashSet<string> StyleProps = Merge(CommonStyleProps, ShapeStyleProps, TextStyleProps,
        Set("Rotation", "FlipH", "FlipV"));
    private static readonly HashSet<string> GradientProps = Set("GradientType", "GradientAngle", "FillColor1", "FillColor2");
    private static readonly HashSet<string> ShadowProps = Set("Preset", "ShadowX", "ShadowY", "ShadowBlur", "ShadowOpacity", "ShadowColor");
    private static readonly HashSet<string> ReorderProps = Set("Mode");
    private static readonly HashSet<string> AlignProps = Set("Mode");
    private static readonly HashSet<string> DistributeProps = Set("Mode");
    private static readonly HashSet<string> ArrangeProps = Set("Mode", "Gap", "Columns", "Justify", "X", "Y", "Width", "Height");
    private static readonly HashSet<string> CanvasProps = Set("Width", "Height", "BackgroundColor", "Transparent");
    private static readonly HashSet<string> DuplicateProps = Set("OffsetX", "OffsetY");

    public static AiValidationResult Validate(
        AiResponse response, DesignDocument doc, IReadOnlyDictionary<string, DesignElement> map)
    {
        var result = new AiValidationResult();
        int count = doc.Elements.Count(e => !e.IsBackgroundLayer);

        if (response.Ops.Count > MaxCommands)
            result.Errors.Add($"המודל החזיר {response.Ops.Count} פקודות; רק {MaxCommands} הראשונות נבדקו.");

        foreach (var command in response.Ops.Take(MaxCommands))
        {
            string op = NormalizeOp(command.Op);
            try
            {
                var allowed = AllowedFor(op) ?? throw new AiPropException("פקודה לא מוכרת.");
                var props = new AiProps(command.Props);
                foreach (string key in props.Keys)
                {
                    if (!allowed.Contains(key))
                        throw new AiPropException($"מאפיין לא מוכר: {key}.");
                }

                var targets = ResolveTargets(command.Target, doc, map, op);
                int delta = ValidateCommand(op, props, targets, doc, map);

                int next = count + delta;
                if (next > MaxElements)
                    throw new AiPropException($"המסמך יעבור {MaxElements} אלמנטים.");
                count = Math.Max(0, next);

                result.Commands.Add(new AiValidatedCommand
                {
                    Command = new AiCommand
                    {
                        Op = op,
                        Target = command.Target,
                        Props = command.Props,
                        Confirm = command.Confirm,
                    },
                    Targets = targets,
                    IsDestructive = op is "delete" or "clear",
                });
            }
            catch (Exception ex)
            {
                result.Errors.Add($"{command.Op}: {ex.Message}");
            }
        }

        return result;
    }

    public static AiApplyReport Apply(
        DesignDocument doc,
        IReadOnlyList<AiValidatedCommand> commands,
        IReadOnlyDictionary<string, DesignElement> map,
        Action? pushUndo)
    {
        var report = new AiApplyReport();
        bool undoPushed = false;

        foreach (var validated in commands)
        {
            try
            {
                if (!undoPushed)
                {
                    pushUndo?.Invoke();
                    undoPushed = true;
                }

                var props = new AiProps(validated.Command.Props);
                ApplyOne(doc, map, validated, props, report);
                report.Applied.Add(Describe(validated));
            }
            catch (Exception ex)
            {
                report.Errors.Add($"{validated.Command.Op}: {ex.Message}");
            }
        }

        doc.NormalizeZ();
        return report;
    }

    // ---------------- validation ----------------

    private static int ValidateCommand(
        string op, AiProps props, List<DesignElement> targets,
        DesignDocument doc, IReadOnlyDictionary<string, DesignElement> map)
    {
        switch (op)
        {
            case "add_shape":
                BuildShape(doc, props);
                return +1;

            case "add_text":
                BuildText(doc, props, requireText: true);
                return +1;

            case "add_image":
                BuildImage(doc, map, props);
                return +1;

            case "delete":
                RequireTargets(targets);
                return -targets.Count;

            case "clear":
                return -doc.Elements.Count(e => !e.IsBackgroundLayer);

            case "duplicate":
                RequireTargets(targets);
                props.Number("OffsetX", -2000, 2000);
                props.Number("OffsetY", -2000, 2000);
                return targets.Count;

            case "set_geometry":
                RequireTargets(targets);
                foreach (var target in targets) ApplyGeometry(target.Clone(), props, doc);
                return 0;

            case "set_style":
                RequireTargets(targets);
                foreach (var target in targets) ApplyStyle(target.Clone(), props);
                return 0;

            case "set_text":
                RequireTargets(targets);
                foreach (var target in targets)
                {
                    if (target is not TextElement)
                        throw new AiPropException("set_text חל על טקסט בלבד.");
                    ApplyText((TextElement)target.Clone(), props);
                }
                return 0;

            case "set_photo":
                RequireTargets(targets);
                foreach (var target in targets)
                {
                    if (target is not ImageElement image)
                        throw new AiPropException("set_photo חל על תמונות בלבד.");
                    ApplyPhoto(image.Adjustments.Clone(), props);
                }
                return 0;

            case "set_gradient":
                RequireTargets(targets);
                foreach (var target in targets) ApplyGradient(target.Clone(), props);
                return 0;

            case "set_shadow":
                RequireTargets(targets);
                foreach (var target in targets) ApplyShadow(target.Clone(), props);
                return 0;

            case "reorder":
                RequireTargets(targets);
                ValidateReorderMode(props);
                return 0;

            case "align":
                RequireTargets(targets);
                ValidateAlignMode(props);
                return 0;

            case "distribute":
                RequireTargets(targets);
                ValidateDistributeMode(props);
                if (targets.Count < 3)
                    throw new AiPropException("distribute דורש לפחות 3 אלמנטים.");
                return 0;

            case "arrange":
                RequireTargets(targets);
                ValidateArrange(props);
                return 0;

            case "set_canvas":
                ValidateCanvas(props);
                return 0;

            default:
                throw new AiPropException("פקודה לא מוכרת.");
        }
    }

    private static void RequireTargets(List<DesignElement> targets)
    {
        if (targets.Count == 0)
            throw new AiPropException("לא נמצאו אלמנטים מתאימים ל-target.");
    }

    private static void ValidateCanvas(AiProps props)
    {
        props.Number("Width", 1, 8000);
        props.Number("Height", 1, 8000);
        props.Color("BackgroundColor");
        props.Bool("Transparent");
    }

    private static void ValidateReorderMode(AiProps props)
    {
        string mode = NormalizeMode(props.Text("Mode")
            ?? throw new AiPropException("חסר Mode: front, forward, backward או back."));
        if (mode is not ("front" or "forward" or "backward" or "back"))
            throw new AiPropException("Mode חייב להיות front, forward, backward או back.");
    }

    private static void ValidateAlignMode(AiProps props)
    {
        string mode = NormalizeMode(props.Text("Mode")
            ?? throw new AiPropException("חסר Mode."));
        if (mode is not ("left" or "center" or "horizontalcenter" or "hcenter" or "right"
            or "top" or "middle" or "verticalcenter" or "vcenter" or "bottom"))
            throw new AiPropException("Mode חייב להיות left, center, right, top, middle או bottom.");
    }

    private static void ValidateDistributeMode(AiProps props)
    {
        string mode = NormalizeMode(props.Text("Mode")
            ?? throw new AiPropException("חסר Mode: horizontal או vertical."));
        if (mode is not ("horizontal" or "vertical"))
            throw new AiPropException("Mode חייב להיות horizontal או vertical.");
    }

    private static void ValidateArrange(AiProps props)
    {
        string mode = NormalizeMode(props.Text("Mode")
            ?? throw new AiPropException("חסר Mode: row, column או grid."));
        if (mode is not ("row" or "column" or "grid"))
            throw new AiPropException("Mode חייב להיות row, column או grid.");
        props.Number("Gap", 0, 400);
        props.Int("Columns", 1, 16);
        props.Text("Justify");
        props.Number("X", -200000, 200000);
        props.Number("Y", -200000, 200000);
        props.Number("Width", 1, 8000);
        props.Number("Height", 1, 8000);
    }

    // ---------------- target resolution ----------------

    private static List<DesignElement> ResolveTargets(
        JsonElement target, DesignDocument doc, IReadOnlyDictionary<string, DesignElement> map,
        string op)
    {
        var list = new List<DesignElement>();
        if (target.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null) return list;

        void AddTarget(DesignElement element)
        {
            if (element.IsBackgroundLayer)
                throw new AiPropException("שכבת הצביעה של הקנבס אינה ניתנת לשינוי.");
            if (!doc.Elements.Contains(element))
                throw new AiPropException("האלמנט כבר אינו קיים במסמך.");
            if (!list.Contains(element)) list.Add(element);
        }

        void AddId(string id)
        {
            if (string.IsNullOrWhiteSpace(id))
                throw new AiPropException("מזהה אלמנט ריק.");
            if (!map.TryGetValue(id.Trim(), out var element))
                throw new AiPropException($"מזהה לא מוכר: {id}.");
            AddTarget(element);
        }

        switch (target.ValueKind)
        {
            case JsonValueKind.String:
                string single = target.GetString() ?? "";
                if (string.Equals(single, "all", StringComparison.OrdinalIgnoreCase))
                {
                    foreach (var element in doc.Elements.Where(e => !e.IsBackgroundLayer))
                        AddTarget(element);
                }
                else
                {
                    AddId(single);
                }
                break;

            case JsonValueKind.Array:
                foreach (JsonElement item in target.EnumerateArray())
                {
                    if (item.ValueKind != JsonValueKind.String)
                        throw new AiPropException("רשימת target חייבת להכיל מזהים.");
                    AddId(item.GetString() ?? "");
                }
                break;

            case JsonValueKind.Object:
            {
                string type = (ReadString(target, "type") ?? "").Trim().ToLowerInvariant();
                IEnumerable<DesignElement> candidates = type switch
                {
                    "all" => doc.Elements.Where(e => !e.IsBackgroundLayer),
                    "text" => doc.Elements.OfType<TextElement>(),
                    "shape" => doc.Elements.OfType<ShapeElement>(),
                    "image" => doc.Elements.OfType<ImageElement>(),
                    _ => throw new AiPropException("סוג target לא מוכר."),
                };

                if (type == "text")
                {
                    string? contains = ReadString(target, "textContains");
                    if (!string.IsNullOrWhiteSpace(contains))
                    {
                        candidates = candidates.OfType<TextElement>()
                            .Where(t => (t.Text ?? "").Contains(contains, StringComparison.OrdinalIgnoreCase));
                    }
                }

                if (type == "shape")
                {
                    string? kind = ReadString(target, "kind");
                    if (!string.IsNullOrWhiteSpace(kind))
                    {
                        var shapeKind = ParseEnum<ShapeKind>(kind, "kind");
                        candidates = candidates.OfType<ShapeElement>().Where(s => s.Kind == shapeKind);
                    }
                }

                string? role = ReadString(target, "role");
                if (!string.IsNullOrWhiteSpace(role))
                {
                    candidates = candidates.Where(e =>
                        string.Equals(AiCanvasBrief.RoleOf(doc, e), role, StringComparison.OrdinalIgnoreCase));
                }

                var candidateList = candidates.ToList();
                int? index = ReadInt(target, "index");
                if (index is { } i)
                {
                    if (i < 0 || i >= candidateList.Count)
                        throw new AiPropException($"index {i} מחוץ לטווח.");
                    AddTarget(candidateList[i]);
                }
                else
                {
                    foreach (var element in candidateList) AddTarget(element);
                }
                break;
            }

            default:
                throw new AiPropException("target לא תקין.");
        }

        if (list.Count == 0 && op is not ("add_shape" or "add_text" or "add_image"
            or "set_canvas" or "clear"))
            throw new AiPropException("לא נמצאו אלמנטים מתאימים ל-target.");

        return list;
    }

    // ---------------- apply ----------------

    private static void ApplyOne(
        DesignDocument doc, IReadOnlyDictionary<string, DesignElement> map,
        AiValidatedCommand validated, AiProps props, AiApplyReport report)
    {
        switch (validated.Command.Op)
        {
            case "add_shape":
            {
                var shape = doc.Add(BuildShape(doc, props));
                if (props.Bool("Behind") == true)
                {
                    doc.Select(shape);
                    doc.SendToBack();
                }
                break;
            }

            case "add_text":
                doc.Add(BuildText(doc, props, requireText: true));
                break;

            case "add_image":
                doc.Add(BuildImage(doc, map, props));
                break;

            case "delete":
                foreach (var target in validated.Targets) doc.RemoveElement(target);
                break;

            case "clear":
                foreach (var target in doc.Elements.Where(e => !e.IsBackgroundLayer).ToList())
                    doc.RemoveElement(target);
                break;

            case "duplicate":
            {
                double dx = props.Number("OffsetX", -2000, 2000) ?? 20;
                double dy = props.Number("OffsetY", -2000, 2000) ?? 20;
                foreach (var target in validated.Targets)
                {
                    var copy = target.Clone();
                    copy.X = ClampX(doc, copy.X + dx);
                    copy.Y = ClampY(doc, copy.Y + dy);
                    doc.Add(copy);
                }
                break;
            }

            case "set_canvas":
                ApplyCanvas(doc, props, report);
                break;

            case "reorder":
                ApplyReorder(doc, validated.Targets, props);
                break;

            case "align":
                ApplyAlign(doc, validated.Targets, props);
                break;

            case "distribute":
                ApplyDistribute(validated.Targets, props);
                break;

            case "arrange":
                ApplyArrange(doc, validated.Targets, props);
                break;

            default:
                foreach (var target in validated.Targets)
                    ApplyToElement(doc, target, validated.Command.Op, props);
                break;
        }
    }

    private static void ApplyToElement(
        DesignDocument doc, DesignElement element, string op, AiProps props)
    {
        switch (op)
        {
            case "set_geometry":
                ApplyGeometry(element, props, doc);
                break;
            case "set_style":
                ApplyStyle(element, props);
                break;
            case "set_text":
                if (element is not TextElement text)
                    throw new AiPropException("set_text חל על טקסט בלבד.");
                ApplyText(text, props);
                break;
            case "set_photo":
                if (element is not ImageElement image)
                    throw new AiPropException("set_photo חל על תמונות בלבד.");
                ApplyPhoto(image.Adjustments, props);
                image.RefreshPhotoPreview();
                break;
            case "set_gradient":
                ApplyGradient(element, props);
                break;
            case "set_shadow":
                ApplyShadow(element, props);
                break;
            default:
                throw new AiPropException("פקודה לא מוכרת.");
        }
    }

    private static ShapeElement BuildShape(DesignDocument doc, AiProps props)
    {
        var shape = new ShapeElement
        {
            Width = props.Number("Width", 1, 8000) ?? 160,
            Height = props.Number("Height", 1, 8000) ?? 120,
        };

        ApplyCommon(shape, props);
        ApplyShape(shape, props);

        shape.X = props.Number("X", -200000, 200000) is { } x
            ? ClampX(doc, doc.ArtboardX + x)
            : doc.ArtboardX + (doc.ArtboardWidth - shape.Width) / 2;
        shape.Y = props.Number("Y", -200000, 200000) is { } y
            ? ClampY(doc, doc.ArtboardY + y)
            : doc.ArtboardY + (doc.ArtboardHeight - shape.Height) / 2;
        if (props.Number("Rotation", -3600, 3600) is { } rotation)
            shape.RotationAngle = rotation;
        if (props.Bool("FlipH") is { } flipH) shape.FlipHorizontal = flipH;
        if (props.Bool("FlipV") is { } flipV) shape.FlipVertical = flipV;
        return shape;
    }

    private static TextElement BuildText(DesignDocument doc, AiProps props, bool requireText)
    {
        var text = new TextElement();
        string? content = props.Text("Text");
        if (requireText && string.IsNullOrWhiteSpace(content))
            throw new AiPropException("add_text דורש שדה Text.");
        if (content != null) text.Text = content;

        ApplyText(text, props);

        text.X = props.Number("X", -200000, 200000) is { } x
            ? ClampX(doc, doc.ArtboardX + x)
            : doc.ArtboardX + (doc.ArtboardWidth - text.Width) / 2;
        text.Y = props.Number("Y", -200000, 200000) is { } y
            ? ClampY(doc, doc.ArtboardY + y)
            : doc.ArtboardY + (doc.ArtboardHeight - text.Height) / 2;
        if (props.Number("Rotation", -3600, 3600) is { } rotation)
            text.RotationAngle = rotation;
        if (props.Bool("FlipH") is { } flipH) text.FlipHorizontal = flipH;
        if (props.Bool("FlipV") is { } flipV) text.FlipVertical = flipV;
        ApplyRtlTextColumn(doc, text);
        text.EnableAnchoring();
        return text;
    }

    /// <summary>
    /// Hebrew/Arabic text created inside a panel is right-aligned, but an
    /// auto-width text box hugs the text and still looks left-aligned. When the
    /// model did not supply WrapWidth, match the text box to its container so
    /// Alignment=Right has a full column to align against.
    /// </summary>
    private static void ApplyRtlTextColumn(DesignDocument doc, TextElement text)
    {
        if (text.Alignment != TextAlign.Right || text.WrapWidth > 0.01 ||
            !ContainsRtlScript(text.Text))
            return;

        double x = text.X;
        double y = text.Y;
        var artboard = doc.ArtboardRect;
        double maxArea = artboard.Width * artboard.Height * 0.8;
        var container = doc.Elements
            .OfType<ShapeElement>()
            .Where(shape =>
                shape.Width >= 120 && shape.Height >= 40 &&
                shape.Width * shape.Height <= maxArea &&
                x >= shape.X && x <= shape.X + shape.Width &&
                y >= shape.Y && y <= shape.Y + shape.Height)
            .OrderBy(shape => shape.Width * shape.Height)
            .FirstOrDefault();

        if (container == null) return;

        double padding = Math.Clamp(container.Width * 0.05, 8, 24);
        double columnWidth = container.Width - padding * 2;
        if (columnWidth < 80) return;

        text.X = container.X + padding;
        text.WrapWidth = columnWidth;
    }

    private static ImageElement BuildImage(
        DesignDocument doc, IReadOnlyDictionary<string, DesignElement> map, AiProps props)
    {
        string? from = props.Text("From") ?? props.Text("SourceId");
        if (string.IsNullOrWhiteSpace(from))
            throw new AiPropException("add_image דורש From עם מזהה של תמונה קיימת.");
        if (!map.TryGetValue(from.Trim(), out var source) || source is not ImageElement sourceImage)
            throw new AiPropException($"add_image: לא נמצאה תמונה עם המזהה {from}.");

        var image = new ImageElement
        {
            Source = sourceImage.Source,
            SourcePath = sourceImage.SourcePath,
            Width = props.Number("Width", 1, 8000) ?? sourceImage.Width,
            Height = props.Number("Height", 1, 8000) ?? sourceImage.Height,
            FitMode = sourceImage.FitMode,
        };
        if (props.Number("CornerRadius", 0, 200) is { } radius) image.CornerRadius = radius;
        image.X = props.Number("X", -200000, 200000) is { } x
            ? ClampX(doc, doc.ArtboardX + x)
            : doc.ArtboardX + (doc.ArtboardWidth - image.Width) / 2;
        image.Y = props.Number("Y", -200000, 200000) is { } y
            ? ClampY(doc, doc.ArtboardY + y)
            : doc.ArtboardY + (doc.ArtboardHeight - image.Height) / 2;
        return image;
    }

    private static void ApplyGeometry(DesignElement element, AiProps props, DesignDocument? doc)
    {
        double min = doc is null ? -200000 : doc.ArtboardX - 200;
        double max = doc is null ? 200000 : doc.ArtboardX + doc.ArtboardWidth + 200;

        if (props.Number("Width", 1, 8000) is { } width && element is not TextElement)
            element.Width = width;
        if (props.Number("Height", 1, 8000) is { } height && element is not TextElement)
            element.Height = height;
        if (props.Number("X", -200000, 200000) is { } x)
            element.X = Math.Clamp(doc is null ? x : doc.ArtboardX + x, min, max);
        if (props.Number("Y", -200000, 200000) is { } y)
        {
            double minY = doc is null ? -200000 : doc.ArtboardY - 200;
            double maxY = doc is null ? 200000 : doc.ArtboardY + doc.ArtboardHeight + 200;
            element.Y = Math.Clamp(doc is null ? y : doc.ArtboardY + y, minY, maxY);
        }
        if (props.Number("Rotation", -3600, 3600) is { } rotation)
            element.RotationAngle = rotation;
        if (props.Bool("FlipH") is { } flipH) element.FlipHorizontal = flipH;
        if (props.Bool("FlipV") is { } flipV) element.FlipVertical = flipV;
    }

    private static void ApplyStyle(DesignElement element, AiProps props)
    {
        ApplyCommon(element, props);

        if (props.Number("Rotation", -3600, 3600) is { } rotation) element.RotationAngle = rotation;
        if (props.Bool("FlipH") is { } flipH) element.FlipHorizontal = flipH;
        if (props.Bool("FlipV") is { } flipV) element.FlipVertical = flipV;

        switch (element)
        {
            case ShapeElement shape:
                ApplyShape(shape, props);
                break;
            case TextElement text:
                ApplyText(text, props);
                break;
            case LineElement line:
                if (props.Color("StrokeColor") is { } stroke) line.StrokeColor = stroke;
                if (props.Number("StrokeThickness", 0.25, 200) is { } thickness)
                    line.StrokeThickness = thickness;
                break;
        }
    }

    private static void ApplyCommon(DesignElement element, AiProps props)
    {
        if (props.Number("Opacity", 0, 100) is { } opacity) element.Opacity = opacity;
        if (props.Number("SoftEdge", 0, 100) is { } softEdge) element.SoftEdge = softEdge;

        if (props.Text("ShadowStyle") is { } preset)
            element.ApplyShadowPreset(ParseEnum<ShadowStyle>(preset, "ShadowStyle"));

        if (props.Number("ShadowX", -200, 200) is { } shadowX)
        {
            element.ShadowX = shadowX;
            if (element.ShadowStyle == ShadowStyle.None) element.ShadowStyle = ShadowStyle.Custom;
        }
        if (props.Number("ShadowY", -200, 200) is { } shadowY)
        {
            element.ShadowY = shadowY;
            if (element.ShadowStyle == ShadowStyle.None) element.ShadowStyle = ShadowStyle.Custom;
        }
        if (props.Number("ShadowBlur", 0, 200) is { } shadowBlur)
        {
            element.ShadowBlur = shadowBlur;
            if (element.ShadowStyle == ShadowStyle.None) element.ShadowStyle = ShadowStyle.Custom;
        }
        if (props.Number("ShadowOpacity", 0, 100) is { } shadowOpacity)
        {
            element.ShadowOpacity = shadowOpacity;
            if (element.ShadowStyle == ShadowStyle.None) element.ShadowStyle = ShadowStyle.Custom;
        }
        if (props.Color("ShadowColor") is { } shadowColor)
        {
            element.ShadowColor = shadowColor;
            if (element.ShadowStyle == ShadowStyle.None) element.ShadowStyle = ShadowStyle.Custom;
        }

        if (props.Bool("UseOpacityGradient") is { } useGradient)
            element.UseOpacityGradient = useGradient;
        if (props.Text("OpacityDirection") is { } direction)
            element.OpacityDirection = ParseEnum<OpacityFadeDirection>(direction, "OpacityDirection");
        if (props.Number("OpacityStart", 0, 100) is { } start) element.OpacityStart = start;
        if (props.Number("OpacityEnd", 0, 100) is { } end) element.OpacityEnd = end;
        if (props.Number("OpacityStrength", 0, 100) is { } strength) element.OpacityStrength = strength;

        if (props.Number("CornerRadius", 0, 200) is { } radius)
        {
            if (element is ShapeElement shape) shape.CornerRadius = radius;
            else if (element is ImageElement image) image.CornerRadius = radius;
        }
    }

    private static void ApplyShape(ShapeElement shape, AiProps props)
    {
        if (props.Text("Kind") is { } kind)
            shape.Kind = ParseEnum<ShapeKind>(kind, "Kind");
        if (props.Color("FillColor1") is { } fill1) shape.FillColor1 = fill1;
        if (props.Color("FillColor2") is { } fill2) shape.FillColor2 = fill2;
        if (props.Bool("IsGradient") is { } gradient) shape.IsGradient = gradient;
        if (props.Text("GradientType") is { } gradientType)
            shape.GradientType = ParseEnum<GradientType>(gradientType, "GradientType");
        if (props.Number("GradientAngle", 0, 360) is { } angle) shape.GradientAngle = angle;
        if (props.Color("StrokeColor") is { } stroke) shape.StrokeColor = stroke;
        if (props.Number("StrokeThickness", 0, 20) is { } thickness) shape.StrokeThickness = thickness;
    }

    private static void ApplyText(TextElement text, AiProps props)
    {
        if (props.Text("Text") is { } content) text.Text = content;
        if (props.Text("FontFamilyName") is { } font && !string.IsNullOrWhiteSpace(font))
            text.FontFamilyName = font;
        if (props.Number("FontSize", 6, 400) is { } size) text.FontSize = size;
        if (props.Bool("IsBold") is { } bold) text.IsBold = bold;
        if (props.Bool("IsItalic") is { } italic) text.IsItalic = italic;
        if (props.Bool("IsUnderline") is { } underline) text.IsUnderline = underline;
        if (props.Bool("IsStrikethrough") is { } strike) text.IsStrikethrough = strike;
        if (props.Text("Alignment") is { } alignment)
            text.Alignment = ParseEnum<TextAlign>(alignment, "Alignment");
        if (props.Text("Direction") is { } direction)
            text.Direction = ParseEnum<TextDirection>(direction, "Direction");
        if (ContainsRtlScript(text.Text))
        {
            text.Direction = TextDirection.Rtl;
            if (text.Alignment == TextAlign.Left) text.Alignment = TextAlign.Right;
        }
        if (props.Number("LetterSpacing", 0, 50) is { } letterSpacing)
            text.LetterSpacing = letterSpacing;
        if (props.Number("LineSpacing", 50, 300) is { } lineSpacing)
            text.LineSpacing = lineSpacing;
        if (props.Number("WrapWidth", 0, 4000) is { } wrapWidth)
            text.WrapWidth = wrapWidth;
        if (props.Color("TextColor1") is { } textColor1) text.TextColor1 = textColor1;
        if (props.Color("TextColor2") is { } textColor2) text.TextColor2 = textColor2;
        if (props.Bool("IsGradient") is { } gradient) text.IsGradient = gradient;
        if (props.Number("TextGradientAngle", 0, 360) is { } gradientAngle)
            text.TextGradientAngle = gradientAngle;
        if (props.Bool("HasBorder") is { } hasBorder) text.HasBorder = hasBorder;
        if (props.Color("BorderColor") is { } borderColor) text.BorderColor = borderColor;
        if (props.Number("BorderThickness", 0, 20) is { } borderThickness)
            text.BorderThickness = borderThickness;
        text.RefreshLayout();
    }

    private static void ApplyPhoto(PhotoAdjustments adjustments, AiProps props)
    {
        if (props.Bool("Reset") == true) adjustments.Reset();

        if (props.Number("Exposure", -5, 5) is { } exposure) adjustments.Exposure = exposure;
        if (props.Number("Contrast", -100, 100) is { } contrast) adjustments.Contrast = contrast;
        if (props.Number("Highlights", -100, 100) is { } highlights) adjustments.Highlights = highlights;
        if (props.Number("Shadows", -100, 100) is { } shadows) adjustments.Shadows = shadows;
        if (props.Number("Whites", -100, 100) is { } whites) adjustments.Whites = whites;
        if (props.Number("Blacks", -100, 100) is { } blacks) adjustments.Blacks = blacks;
        if (props.Number("Temperature", -100, 100) is { } temperature) adjustments.Temperature = temperature;
        if (props.Number("Tint", -100, 100) is { } tint) adjustments.Tint = tint;
        if (props.Number("Vibrance", -100, 100) is { } vibrance) adjustments.Vibrance = vibrance;
        if (props.Number("Saturation", -100, 100) is { } saturation) adjustments.Saturation = saturation;
        if (props.Number("Clarity", -100, 100) is { } clarity) adjustments.Clarity = clarity;
        if (props.Number("Vignette", -100, 100) is { } vignette) adjustments.Vignette = vignette;
        if (props.Number("Grain", 0, 100) is { } grain) adjustments.Grain = grain;
        if (props.Number("Blur", 0, 100) is { } blur) adjustments.Blur = blur;
        if (props.Number("Sharpen", 0, 100) is { } sharpen) adjustments.Sharpen = sharpen;
        if (props.Number("Glow", 0, 100) is { } glow) adjustments.Glow = glow;
        if (props.Number("ToneAmount", 0, 100) is { } toneAmount) adjustments.ToneAmount = toneAmount;
        if (props.Color("ToneColor") is { } toneColor) adjustments.ToneColor = toneColor;
        if (props.Number("RecolorAmount", 0, 100) is { } recolorAmount) adjustments.RecolorAmount = recolorAmount;
        if (props.Color("RecolorColor") is { } recolorColor) adjustments.RecolorColor = recolorColor;
        if (props.Number("Intensity", 0, 100) is { } intensity) adjustments.Intensity = intensity;
    }

    private static void ApplyGradient(DesignElement element, AiProps props)
    {
        switch (element)
        {
            case ShapeElement shape:
                shape.IsGradient = true;
                if (props.Text("GradientType") is { } gradientType)
                    shape.GradientType = ParseEnum<GradientType>(gradientType, "GradientType");
                if (props.Number("GradientAngle", 0, 360) is { } angle) shape.GradientAngle = angle;
                if (props.Color("FillColor1") is { } fill1) shape.FillColor1 = fill1;
                if (props.Color("FillColor2") is { } fill2) shape.FillColor2 = fill2;
                break;

            case TextElement text:
                text.IsGradient = true;
                if (props.Number("GradientAngle", 0, 360) is { } textAngle) text.TextGradientAngle = textAngle;
                if (props.Color("FillColor1") is { } color1) text.TextColor1 = color1;
                if (props.Color("FillColor2") is { } color2) text.TextColor2 = color2;
                break;

            default:
                throw new AiPropException("set_gradient חל על צורות וטקסט בלבד.");
        }
    }

    private static void ApplyShadow(DesignElement element, AiProps props)
    {
        if (props.Text("Preset") is { } preset)
            element.ApplyShadowPreset(ParseEnum<ShadowStyle>(preset, "Preset"));
        else if (props.Has("ShadowX") || props.Has("ShadowY") || props.Has("ShadowBlur")
                 || props.Has("ShadowOpacity") || props.Has("ShadowColor"))
            element.ShadowStyle = ShadowStyle.Custom;

        if (props.Number("ShadowX", -200, 200) is { } x) element.ShadowX = x;
        if (props.Number("ShadowY", -200, 200) is { } y) element.ShadowY = y;
        if (props.Number("ShadowBlur", 0, 200) is { } blur) element.ShadowBlur = blur;
        if (props.Number("ShadowOpacity", 0, 100) is { } opacity) element.ShadowOpacity = opacity;
        if (props.Color("ShadowColor") is { } color) element.ShadowColor = color;
    }

    private static void ApplyCanvas(DesignDocument doc, AiProps props, AiApplyReport report)
    {
        double width = props.Number("Width", 1, 8000) ?? doc.ArtboardWidth;
        double height = props.Number("Height", 1, 8000) ?? doc.ArtboardHeight;

        if (props.Color("BackgroundColor") is { } background)
        {
            doc.CanvasColor = background;
            doc.CanvasTransparent = false;
            report.CanvasChanged = true;
        }
        if (props.Bool("Transparent") is { } transparent)
        {
            doc.CanvasTransparent = transparent;
            report.CanvasChanged = true;
        }
        if (Math.Abs(width - doc.ArtboardWidth) > 0.01 || Math.Abs(height - doc.ArtboardHeight) > 0.01)
        {
            doc.ResizeCanvas(width, height);
            report.CanvasChanged = true;
        }
    }

    private static void ApplyReorder(
        DesignDocument doc, IReadOnlyList<DesignElement> targets, AiProps props)
    {
        string mode = NormalizeMode(props.Text("Mode") ?? "");
        var ordered = doc.Elements.Where(e => !e.IsBackgroundLayer).OrderBy(e => e.ZIndex).ToList();

        foreach (var element in targets)
        {
            int index = ordered.IndexOf(element);
            if (index < 0) continue;
            ordered.RemoveAt(index);

            switch (mode)
            {
                case "front":
                    ordered.Add(element);
                    break;
                case "back":
                    ordered.Insert(0, element);
                    break;
                case "forward":
                    ordered.Insert(Math.Min(ordered.Count, index + 1), element);
                    break;
                case "backward":
                    ordered.Insert(Math.Max(0, index - 1), element);
                    break;
                default:
                    throw new AiPropException("Mode חייב להיות front, forward, backward או back.");
            }
        }

        for (int i = 0; i < ordered.Count; i++) ordered[i].ZIndex = i;
    }

    private static void ApplyAlign(
        DesignDocument doc, IReadOnlyList<DesignElement> targets, AiProps props)
    {
        string mode = NormalizeMode(props.Text("Mode") ?? "");
        Rect artboard = doc.ArtboardRect;

        foreach (var element in targets)
        {
            switch (mode)
            {
                case "left":
                    element.X = artboard.X;
                    break;
                case "center":
                case "horizontalcenter":
                case "hcenter":
                    element.X = artboard.X + (artboard.Width - element.Width) / 2;
                    break;
                case "right":
                    element.X = artboard.Right - element.Width;
                    break;
                case "top":
                    element.Y = artboard.Y;
                    break;
                case "middle":
                case "verticalcenter":
                case "vcenter":
                    element.Y = artboard.Y + (artboard.Height - element.Height) / 2;
                    break;
                case "bottom":
                    element.Y = artboard.Bottom - element.Height;
                    break;
                default:
                    throw new AiPropException("Mode לא נתמך.");
            }
        }
    }

    private static void ApplyDistribute(IReadOnlyList<DesignElement> targets, AiProps props)
    {
        string mode = NormalizeMode(props.Text("Mode") ?? "");
        if (targets.Count < 3) throw new AiPropException("distribute דורש לפחות 3 אלמנטים.");

        if (mode == "horizontal")
        {
            var ordered = targets.OrderBy(e => e.X).ToList();
            double start = ordered[0].X;
            double end = ordered.Max(e => e.X + e.Width);
            double totalWidth = ordered.Sum(e => e.Width);
            double gap = (end - start - totalWidth) / (ordered.Count - 1);
            double x = start;
            foreach (var element in ordered)
            {
                element.X = x;
                x += element.Width + gap;
            }
        }
        else if (mode == "vertical")
        {
            var ordered = targets.OrderBy(e => e.Y).ToList();
            double start = ordered[0].Y;
            double end = ordered.Max(e => e.Y + e.Height);
            double totalHeight = ordered.Sum(e => e.Height);
            double gap = (end - start - totalHeight) / (ordered.Count - 1);
            double y = start;
            foreach (var element in ordered)
            {
                element.Y = y;
                y += element.Height + gap;
            }
        }
        else
        {
            throw new AiPropException("Mode חייב להיות horizontal או vertical.");
        }
    }

    private static void ApplyArrange(
        DesignDocument doc, IReadOnlyList<DesignElement> targets, AiProps props)
    {
        string mode = NormalizeMode(props.Text("Mode") ?? "");
        if (targets.Count == 0) return;

        double gap = props.Number("Gap", 0, 400) ?? 24;
        double containerX = props.Number("X", -200000, 200000) is { } propX
            ? doc.ArtboardX + propX
            : targets.Min(e => e.X);
        double containerY = props.Number("Y", -200000, 200000) is { } propY
            ? doc.ArtboardY + propY
            : targets.Min(e => e.Y);
        double containerWidth = props.Number("Width", 1, 8000)
            ?? Math.Max(1, targets.Max(e => e.X + e.Width) - containerX);
        double containerHeight = props.Number("Height", 1, 8000)
            ?? Math.Max(1, targets.Max(e => e.Y + e.Height) - containerY);
        string justify = (props.Text("Justify") ?? "start").Trim().ToLowerInvariant();

        if (mode == "row")
        {
            var ordered = targets.OrderBy(e => e.X).ToList();
            double total = ordered.Sum(e => e.Width) + gap * (ordered.Count - 1);
            double x = containerX;
            if (justify is "center" or "middle") x += (containerWidth - total) / 2;
            else if (justify is "end" or "right") x += containerWidth - total;

            foreach (var element in ordered)
            {
                element.X = x;
                element.Y = containerY;
                x += element.Width + gap;
            }
        }
        else if (mode == "column")
        {
            var ordered = targets.OrderBy(e => e.Y).ToList();
            double total = ordered.Sum(e => e.Height) + gap * (ordered.Count - 1);
            double y = containerY;
            if (justify is "center" or "middle") y += (containerHeight - total) / 2;
            else if (justify is "end" or "bottom") y += containerHeight - total;

            foreach (var element in ordered)
            {
                element.Y = y;
                element.X = containerX;
                y += element.Height + gap;
            }
        }
        else if (mode == "grid")
        {
            int columns = props.Int("Columns", 1, 16)
                ?? Math.Max(1, (int)Math.Ceiling(Math.Sqrt(targets.Count)));
            int rows = (int)Math.Ceiling(targets.Count / (double)columns);
            double cellWidth = Math.Max(1, (containerWidth - gap * (columns - 1)) / columns);
            double cellHeight = Math.Max(1, (containerHeight - gap * (rows - 1)) / rows);

            for (int i = 0; i < targets.Count; i++)
            {
                int column = i % columns;
                int row = i / columns;
                var element = targets[i];
                element.X = containerX + column * (cellWidth + gap) + (cellWidth - element.Width) / 2;
                element.Y = containerY + row * (cellHeight + gap) + (cellHeight - element.Height) / 2;
            }
        }
        else
        {
            throw new AiPropException("Mode חייב להיות row, column או grid.");
        }

        foreach (var element in targets)
        {
            element.X = ClampX(doc, element.X);
            element.Y = ClampY(doc, element.Y);
        }
    }

    // ---------------- helpers ----------------

    private static HashSet<string>? AllowedFor(string op) => op switch
    {
        "add_shape" => AddShapeProps,
        "add_text" => AddTextProps,
        "add_image" => AddImageProps,
        "delete" => new HashSet<string>(),
        "clear" => new HashSet<string>(),
        "duplicate" => DuplicateProps,
        "set_geometry" => GeometryProps,
        "set_style" => StyleProps,
        "set_text" => TextStyleProps,
        "set_photo" => PhotoProps,
        "set_gradient" => GradientProps,
        "set_shadow" => ShadowProps,
        "reorder" => ReorderProps,
        "align" => AlignProps,
        "distribute" => DistributeProps,
        "arrange" => ArrangeProps,
        "set_canvas" => CanvasProps,
        _ => null,
    };

    private static string NormalizeOp(string op) =>
        (op ?? "").Trim().ToLowerInvariant().Replace('-', '_');

    private static string NormalizeMode(string mode) =>
        (mode ?? "").Trim().ToLowerInvariant().Replace("_", "").Replace("-", "").Replace(" ", "");

    private static string? ReadString(JsonElement node, string name) =>
        node.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static int? ReadInt(JsonElement node, string name)
    {
        if (!node.TryGetProperty(name, out JsonElement value)) return null;
        if (value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out int number)) return number;
        if (value.ValueKind == JsonValueKind.String &&
            int.TryParse(value.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out number))
            return number;
        throw new AiPropException($"'{name}' חייב להיות מספר שלם.");
    }

    private static TEnum ParseEnum<TEnum>(string text, string name) where TEnum : struct, Enum
    {
        if (!Enum.TryParse(text, true, out TEnum value) || !Enum.IsDefined(value))
            throw new AiPropException(
                $"'{name}' חייב להיות אחד מ: {string.Join(", ", Enum.GetNames<TEnum>())}.");
        return value;
    }

    private static bool ContainsRtlScript(string? text)
    {
        foreach (char c in text ?? "")
        {
            if (c is >= '\u0590' and <= '\u05FF') return true; // Hebrew
            if (c is >= '\u0600' and <= '\u06FF') return true; // Arabic
            if (c is >= '\u0750' and <= '\u077F') return true;
            if (c is >= '\u08A0' and <= '\u08FF') return true;
            if (c is >= '\uFB1D' and <= '\uFB4F') return true;
            if (c is >= '\uFB50' and <= '\uFDFF') return true;
            if (c is >= '\uFE70' and <= '\uFEFF') return true;
        }
        return false;
    }

    private static double ClampX(DesignDocument doc, double x) =>
        Math.Clamp(x, doc.ArtboardX - 200, doc.ArtboardX + doc.ArtboardWidth + 200);

    private static double ClampY(DesignDocument doc, double y) =>
        Math.Clamp(y, doc.ArtboardY - 200, doc.ArtboardY + doc.ArtboardHeight + 200);

    private static string Describe(AiValidatedCommand validated)
    {
        int count = validated.Targets.Count;
        return validated.Command.Op switch
        {
            "add_shape" => "נוספה צורה",
            "add_text" => "נוסף טקסט",
            "add_image" => "נוספה תמונה",
            "delete" => $"נמחקו {count} אלמנטים",
            "clear" => "הקנבס נוקה",
            "duplicate" => $"שוכפלו {count} אלמנטים",
            "set_geometry" => $"עודכנה גיאומטריה ({count})",
            "set_style" => $"עודכן עיצוב ({count})",
            "set_text" => $"עודכן טקסט ({count})",
            "set_photo" => $"עודכנה תמונה ({count})",
            "set_gradient" => $"עודכן גרדיאנט ({count})",
            "set_shadow" => $"עודכן צל ({count})",
            "reorder" => $"עודכן סדר שכבות ({count})",
            "align" => $"יושרו {count} אלמנטים",
            "distribute" => $"פוזרו {count} אלמנטים",
            "arrange" => $"סודרו {count} אלמנטים",
            "set_canvas" => "עודכן הקנבס",
            _ => validated.Command.Op,
        };
    }

    private static HashSet<string> Set(params string[] names) => new(names, StringComparer.OrdinalIgnoreCase);

    private static HashSet<string> Merge(params HashSet<string>[] sets)
    {
        var merged = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var set in sets)
            foreach (string item in set) merged.Add(item);
        return merged;
    }

    private sealed class AiPropException : Exception
    {
        public AiPropException(string message) : base(message) { }
    }

    /// <summary>Typed, validating access to a command's props object.</summary>
    private sealed class AiProps
    {
        private readonly Dictionary<string, JsonElement> _values;

        public AiProps(Dictionary<string, JsonElement> values) =>
            _values = new Dictionary<string, JsonElement>(values, StringComparer.OrdinalIgnoreCase);

        public IEnumerable<string> Keys => _values.Keys;

        public bool Has(string name) => _values.ContainsKey(name);

        public string? Text(string name)
        {
            if (!_values.TryGetValue(name, out JsonElement value) || value.ValueKind == JsonValueKind.Null)
                return null;
            if (value.ValueKind != JsonValueKind.String)
                throw new AiPropException($"'{name}' חייב להיות מחרוזת.");
            return value.GetString();
        }

        public bool? Bool(string name)
        {
            if (!_values.TryGetValue(name, out JsonElement value) || value.ValueKind == JsonValueKind.Null)
                return null;
            if (value.ValueKind is JsonValueKind.True or JsonValueKind.False)
                return value.GetBoolean();
            if (value.ValueKind == JsonValueKind.String &&
                bool.TryParse(value.GetString(), out bool parsed))
                return parsed;
            throw new AiPropException($"'{name}' חייב להיות true או false.");
        }

        public double? Number(string name, double min, double max)
        {
            if (!_values.TryGetValue(name, out JsonElement value) || value.ValueKind == JsonValueKind.Null)
                return null;

            double number;
            if (value.ValueKind == JsonValueKind.Number)
            {
                number = value.GetDouble();
            }
            else if (value.ValueKind == JsonValueKind.String &&
                     double.TryParse(value.GetString(), NumberStyles.Float,
                         CultureInfo.InvariantCulture, out double parsed))
            {
                number = parsed;
            }
            else
            {
                throw new AiPropException($"'{name}' חייב להיות מספר.");
            }

            if (!double.IsFinite(number))
                throw new AiPropException($"'{name}' חייב להיות מספר סופי.");
            return Math.Clamp(number, min, max);
        }

        public int? Int(string name, int min, int max)
        {
            double? number = Number(name, min, max);
            return number is null ? null : (int)Math.Round(number.Value);
        }

        public System.Windows.Media.Color? Color(string name)
        {
            string? text = Text(name);
            return text is null ? null : ParseColor(text, name);
        }

        public TEnum? EnumValue<TEnum>(string name) where TEnum : struct, Enum
        {
            string? text = Text(name);
            return text is null ? null : ParseEnum<TEnum>(text, name);
        }

        private static System.Windows.Media.Color ParseColor(string text, string name)
        {
            string hex = text.Trim();
            if (!hex.StartsWith('#') || (hex.Length != 7 && hex.Length != 9))
                throw new AiPropException($"'{name}' חייב להיות #RRGGBB או #AARRGGBB.");

            try
            {
                if (hex.Length == 7)
                {
                    return System.Windows.Media.Color.FromRgb(
                        Convert.ToByte(hex.Substring(1, 2), 16),
                        Convert.ToByte(hex.Substring(3, 2), 16),
                        Convert.ToByte(hex.Substring(5, 2), 16));
                }

                return System.Windows.Media.Color.FromArgb(
                    Convert.ToByte(hex.Substring(1, 2), 16),
                    Convert.ToByte(hex.Substring(3, 2), 16),
                    Convert.ToByte(hex.Substring(5, 2), 16),
                    Convert.ToByte(hex.Substring(7, 2), 16));
            }
            catch (Exception)
            {
                throw new AiPropException($"'{name}' מכיל צבע hex לא תקין.");
            }
        }
    }
}
