using System;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace YorteImpact.Models;

/// <summary>How the text block decides between LTR and RTL.</summary>
public enum TextDirection
{
    Auto,
    Ltr,
    Rtl,
}

/// <summary>Physical horizontal alignment of the text block.</summary>
public enum TextAlign
{
    Left,
    Center,
    Right,
}

/// <summary>
/// Editable text block. Whole-block styling only (font, size, color/gradient,
/// bold/italic/underline/strikethrough, direction, letter/line spacing).
/// Its Width/Height are derived from the text metrics so selection, hit-testing
/// and clipping always match what is drawn.
/// </summary>
public class TextElement : DesignElement
{
    private string _text = "";
    private string _fontFamilyName = "Segoe UI";
    private double _fontSize = 32;
    private Color _textColor1 = Color.FromRgb(0x1E, 0x29, 0x3B);
    private Color _textColor2 = Color.FromRgb(0x2E, 0x9B, 0xFF);
    private bool _isGradient;
    private double _textGradientAngle = 135;
    private bool _isBold;
    private bool _isItalic;
    private bool _isUnderline;
    private bool _isStrikethrough;
    private bool _hasBorder;
    private Color _borderColor = Colors.Black;
    private double _borderThickness = 1;
    private TextDirection _direction = TextDirection.Auto;
    private TextAlign _alignment = TextAlign.Right;
    private double _letterSpacing;
    private double _lineSpacing = 120; // percent of the font size
    private double _wrapWidth;         // 0 = auto-size, > 0 = wrap at this width
    private IReadOnlyList<string> _layoutLines = new[] { "" };
    private bool _isEditing;
    private bool _anchoring; // false until the element is placed (avoid moving during init/load)

    public TextElement()
    {
        Relayout();
    }

    /// <summary>Text content. Newlines create hard line breaks (no auto-wrap).</summary>
    public string Text
    {
        get => _text;
        set
        {
            // TextBox supplies CRLF; FormattedText treats a stray '\r' as layout
            // content, which shifted the committed block. Store one clean '\n'.
            string normalized = (value ?? "").Replace("\r\n", "\n").Replace('\r', '\n');
            if (Set(ref _text, normalized))
            {
                Raise(nameof(IsRtl));
                Raise(nameof(FlowDirectionValue));
                Raise(nameof(LogicalAlignment));
                Relayout();
            }
        }
    }

    public string FontFamilyName
    {
        get => _fontFamilyName;
        set
        {
            if (Set(ref _fontFamilyName, string.IsNullOrWhiteSpace(value) ? "Segoe UI" : value))
            {
                Raise(nameof(FontFamilyValue));
                Relayout();
            }
        }
    }

    public double FontSize
    {
        get => _fontSize;
        set
        {
            if (Set(ref _fontSize, Math.Clamp(value, 1, 500)))
            {
                Raise(nameof(LineHeight));
                Relayout();
            }
        }
    }

    public Color TextColor1
    {
        get => _textColor1;
        set { if (Set(ref _textColor1, value)) Raise(nameof(ForegroundBrush)); }
    }

    public Color TextColor2
    {
        get => _textColor2;
        set { if (Set(ref _textColor2, value)) Raise(nameof(ForegroundBrush)); }
    }

    public bool IsGradient
    {
        get => _isGradient;
        set { if (Set(ref _isGradient, value)) Raise(nameof(ForegroundBrush)); }
    }

    public double TextGradientAngle
    {
        get => _textGradientAngle;
        set { if (Set(ref _textGradientAngle, value)) Raise(nameof(ForegroundBrush)); }
    }

    public bool IsBold
    {
        get => _isBold;
        set
        {
            if (Set(ref _isBold, value))
            {
                Raise(nameof(FontWeight));
                Relayout();
            }
        }
    }

    public bool IsItalic
    {
        get => _isItalic;
        set
        {
            if (Set(ref _isItalic, value))
            {
                Raise(nameof(FontStyle));
                Relayout();
            }
        }
    }

    public bool IsUnderline
    {
        get => _isUnderline;
        set { if (Set(ref _isUnderline, value)) Raise(nameof(Decorations)); }
    }

    public bool IsStrikethrough
    {
        get => _isStrikethrough;
        set { if (Set(ref _isStrikethrough, value)) Raise(nameof(Decorations)); }
    }

    /// <summary>Draw an outline around every glyph.</summary>
    public bool HasBorder
    {
        get => _hasBorder;
        set
        {
            if (Set(ref _hasBorder, value))
            {
                Raise(nameof(HasVisibleBorder));
                Raise(nameof(UseSimpleRender));
            }
        }
    }

    public Color BorderColor
    {
        get => _borderColor;
        set => Set(ref _borderColor, value);
    }

    /// <summary>Outline width in pixels.</summary>
    public double BorderThickness
    {
        get => _borderThickness;
        set
        {
            if (Set(ref _borderThickness, Math.Clamp(value, 0, 20)))
            {
                Raise(nameof(HasVisibleBorder));
                Raise(nameof(UseSimpleRender));
            }
        }
    }

    public bool HasVisibleBorder => _hasBorder && _borderThickness > 0.01;


    /// <summary>Auto / forced LTR / forced RTL.</summary>
    public TextDirection Direction
    {
        get => _direction;
        set
        {
            if (Set(ref _direction, value))
            {
                Raise(nameof(IsRtl));
                Raise(nameof(FlowDirectionValue));
                Raise(nameof(LogicalAlignment));
                Relayout();
            }
        }
    }

    /// <summary>Physical alignment of the block: left, center or right.</summary>
    public TextAlign Alignment
    {
        get => _alignment;
        set { if (Set(ref _alignment, value)) Raise(nameof(LogicalAlignment)); }
    }

    /// <summary>Extra space between characters, in pixels.</summary>
    public double LetterSpacing
    {
        get => _letterSpacing;
        set
        {
            if (Set(ref _letterSpacing, Math.Clamp(value, 0, 50)))
            {
                Raise(nameof(UseSimpleRender));
                Relayout();
            }
        }
    }

    /// <summary>Line height as a percentage of the font size (100 = font size).</summary>
    public double LineSpacing
    {
        get => _lineSpacing;
        set
        {
            if (Set(ref _lineSpacing, Math.Clamp(value, 50, 300)))
            {
                Raise(nameof(LineHeight));
                Relayout();
            }
        }
    }

    /// <summary>Wrap width in pixels. 0 = auto-size; &gt; 0 wraps long words to this width.</summary>
    public double WrapWidth
    {
        get => _wrapWidth;
        set
        {
            if (Set(ref _wrapWidth, Math.Clamp(value, 0, 4000)))
            {
                Raise(nameof(Wrapping));
                Relayout();
            }
        }
    }

    /// <summary>The lines actually drawn (explicit newlines + word wrap).</summary>
    public IReadOnlyList<string> LayoutLines => _layoutLines;

    /// <summary>True while the in-place editor sits over this element.</summary>
    public bool IsEditing
    {
        get => _isEditing;
        set => Set(ref _isEditing, value);
    }

    public double LineHeight => Math.Max(1, _fontSize * _lineSpacing / 100.0);

    public FontWeight FontWeight => _isBold ? FontWeights.Bold : FontWeights.Normal;

    public FontStyle FontStyle => _isItalic ? FontStyles.Italic : FontStyles.Normal;

    public bool IsRtl => DetectRtl(_text);

    public FontFamily FontFamilyValue =>
        new(string.IsNullOrWhiteSpace(_fontFamilyName) ? "Segoe UI" : _fontFamilyName);

    public FlowDirection FlowDirectionValue => IsRtl ? FlowDirection.RightToLeft : FlowDirection.LeftToRight;

    /// <summary>WPF TextAlignment is logical under RTL; map our physical alignment to it.</summary>
    public TextAlignment LogicalAlignment => _alignment switch
    {
        TextAlign.Left => IsRtl ? TextAlignment.Right : TextAlignment.Left,
        TextAlign.Right => IsRtl ? TextAlignment.Left : TextAlignment.Right,
        _ => TextAlignment.Center,
    };

    public TextWrapping Wrapping => _wrapWidth > 0 ? TextWrapping.Wrap : TextWrapping.NoWrap;

    public bool UseSimpleRender => _letterSpacing <= 0.01 && !HasVisibleBorder;

    /// <summary>Solid or gradient brush for the native TextBlock renderer.</summary>
    public Brush ForegroundBrush
    {
        get
        {
            if (!_isGradient)
            {
                var solid = new SolidColorBrush(_textColor1);
                if (solid.CanFreeze) solid.Freeze();
                return solid;
            }

            double rad = (_textGradientAngle - 90) * Math.PI / 180.0;
            double dx = Math.Cos(rad) / 2, dy = Math.Sin(rad) / 2;
            var brush = new LinearGradientBrush(_textColor1, _textColor2,
                new Point(0.5 - dx, 0.5 - dy), new Point(0.5 + dx, 0.5 + dy));
            if (brush.CanFreeze) brush.Freeze();
            return brush;
        }
    }

    public TextDecorationCollection? Decorations
    {
        get
        {
            if (!_isUnderline && !_isStrikethrough) return null;
            var decorations = new TextDecorationCollection();
            if (_isUnderline) decorations.Add(TextDecorations.Underline[0]);
            if (_isStrikethrough) decorations.Add(TextDecorations.Strikethrough[0]);
            return decorations;
        }
    }

    /// <summary>First strong letter decides the auto direction (Hebrew/Arabic = RTL).</summary>
    private static bool DetectRtl(string text)
    {
        foreach (char c in text)
        {
            if (c is >= '\u0590' and <= '\u05FF') return true; // Hebrew
            if (c is >= '\u0600' and <= '\u06FF') return true; // Arabic
            if (c is >= '\u0750' and <= '\u077F') return true;
            if (c is >= '\u08A0' and <= '\u08FF') return true;
            if (c is >= '\uFB1D' and <= '\uFB4F') return true;
            if (c is >= '\uFB50' and <= '\uFDFF') return true;
            if (c is >= '\uFE70' and <= '\uFEFF') return true;
            if (char.IsLetter(c)) return false;
        }
        return false;
    }

    /// <summary>Recompute the wrapped lines and the Width/Height from them.</summary>
    private void Relayout()
    {
        double oldWidth = Width;
        var layout = BuildLayout();
        _layoutLines = layout.Lines;
        Raise(nameof(LayoutLines));

        double lineH = LineHeight;
        double newWidth = _wrapWidth > 0
            ? Math.Max(1, Math.Ceiling(_wrapWidth))
            : Math.Max(1, Math.Ceiling(layout.MaxW));

        // Keep the anchored side still while the block grows: RTL/right-aligned
        // text should extend left, centered text both ways, LTR/left to the right.
        if (_anchoring && Math.Abs(newWidth - oldWidth) > 0.5)
        {
            double delta = newWidth - oldWidth;
            if (_alignment == TextAlign.Right) X -= delta;
            else if (_alignment == TextAlign.Center) X -= delta / 2;
        }

        Width = newWidth;
        Height = Math.Max(1, Math.Ceiling((_layoutLines.Count - 1) * lineH + layout.MaxH));
    }

    /// <summary>Turn on side-anchored growth once X/Y are final (create / load / clone).</summary>
    public void EnableAnchoring() => _anchoring = true;

    /// <summary>Can be suspended while a resize handle controls the box manually.</summary>
    public bool Anchoring
    {
        get => _anchoring;
        set => _anchoring = value;
    }

    /// <summary>Fresh lines for the renderer (never stale even mid-edit).</summary>
    public IReadOnlyList<string> ComputeLayoutLines() => BuildLayout().Lines;

    private (List<string> Lines, double MaxW, double MaxH) BuildLayout()
    {
        // No letter spacing: let WPF's own text layout measure it, exactly like
        // the TextBox editor and the TextBlock view. This keeps RTL alignment,
        // wrapping and line breaks identical before/after editing.
        if (_letterSpacing <= 0.01)
        {
            var tb = new TextBlock
            {
                Text = _text,
                FontFamily = FontFamilyValue,
                FontSize = Math.Max(1, _fontSize),
                FontWeight = FontWeight,
                FontStyle = FontStyle,
                FlowDirection = FlowDirectionValue,
                TextAlignment = LogicalAlignment,
                TextWrapping = Wrapping,
                LineHeight = LineHeight,
                LineStackingStrategy = LineStackingStrategy.MaxHeight,
            };
            if (_wrapWidth > 0) tb.Width = _wrapWidth;
            tb.Measure(new Size(
                _wrapWidth > 0 ? _wrapWidth : double.PositiveInfinity,
                double.PositiveInfinity));
            var d = tb.DesiredSize;
            return (new List<string> { _text ?? "" }, Math.Max(0, d.Width), Math.Max(0, d.Height));
        }

        string[] paragraphs = (_text ?? "").Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
        if (paragraphs.Length == 0) paragraphs = new[] { "" };

        var typeface = new Typeface(
            new FontFamily(string.IsNullOrWhiteSpace(_fontFamilyName) ? "Segoe UI" : _fontFamilyName),
            FontStyle, FontWeight, FontStretches.Normal);
        var flow = IsRtl ? FlowDirection.RightToLeft : FlowDirection.LeftToRight;
        double size = Math.Max(1, _fontSize);

        double Measure(string line, out double height)
        {
            var ft = new FormattedText(line, CultureInfo.CurrentUICulture, flow,
                typeface, size, Brushes.Black, 1.0);
            height = ft.Height;
            double w = ft.WidthIncludingTrailingWhitespace;
            if (_letterSpacing > 0 && line.Length > 1)
                w += _letterSpacing * (line.Length - 1);
            return w;
        }

        var lines = new List<string>();
        foreach (string paragraph in paragraphs)
        {
            if (_wrapWidth > 0)
                lines.AddRange(WrapParagraph(paragraph, _wrapWidth, l => Measure(l, out _)));
            else
                lines.Add(paragraph);
        }
        if (lines.Count == 0) lines.Add("");

        double maxW = 0, maxH = 0;
        foreach (string line in lines)
        {
            double w = Measure(line, out double h);
            if (w > maxW) maxW = w;
            if (h > maxH) maxH = h;
        }

        return (lines, maxW, maxH);
    }

    /// <summary>Greedy word wrap; a single word wider than the limit is broken by characters.</summary>
    private static IEnumerable<string> WrapParagraph(string paragraph, double maxWidth,
        Func<string, double> measure)
    {
        var output = new List<string>();
        if (paragraph.Length == 0)
        {
            output.Add("");
            return output;
        }

        // A hair of slack: the editor's TextBox measures slightly differently,
        // and a word exactly at the edge must not jump to the next line on commit.
        double limit = maxWidth + 2;

        string current = "";
        foreach (string word in paragraph.Split(' '))
        {
            if (word.Length == 0)
            {
                if (current.Length > 0) current += " ";
                continue;
            }

            string candidate = current.Length == 0 ? word : current + " " + word;
            if (measure(candidate) <= limit)
            {
                current = candidate;
                continue;
            }

            if (current.Length > 0)
            {
                output.Add(current);
                current = "";
            }

            if (measure(word) <= limit)
            {
                current = word;
                continue;
            }

            // Too long for a whole line: break it across lines by characters.
            string piece = "";
            foreach (char ch in word)
            {
                string next = piece + ch;
                if (piece.Length > 0 && measure(next) > limit)
                {
                    output.Add(piece);
                    piece = ch.ToString();
                }
                else
                {
                    piece = next;
                }
            }
            current = piece;
        }

        if (current.Length > 0 || output.Count == 0) output.Add(current);
        return output;
    }

    /// <summary>Force a re-measure (used after the in-place editor commits).</summary>
    public void RefreshLayout() => Relayout();

    /// <summary>Copy all appearance settings (not geometry or text content) onto another text element.</summary>
    public void CopyStyleTo(TextElement t)
    {
        t.FontFamilyName = FontFamilyName;
        t.FontSize = FontSize;
        t.TextColor1 = TextColor1;
        t.TextColor2 = TextColor2;
        t.IsGradient = IsGradient;
        t.TextGradientAngle = TextGradientAngle;
        t.IsBold = IsBold;
        t.IsItalic = IsItalic;
        t.IsUnderline = IsUnderline;
        t.IsStrikethrough = IsStrikethrough;
        t.HasBorder = HasBorder;
        t.BorderColor = BorderColor;
        t.BorderThickness = BorderThickness;
        t.Direction = Direction;
        t.Alignment = Alignment;
        t.LetterSpacing = LetterSpacing;
        t.LineSpacing = LineSpacing;
        t.WrapWidth = WrapWidth;
        t.ShadowStyle = ShadowStyle;
        t.ShadowX = ShadowX;
        t.ShadowY = ShadowY;
        t.ShadowBlur = ShadowBlur;
        t.ShadowOpacity = ShadowOpacity;
        t.ShadowColor = ShadowColor;
        t.Opacity = Opacity;
        t.SoftEdge = SoftEdge;
    }

    /// <summary>Copy appearance settings; text-specific settings apply to text targets only.</summary>
    public override void CopyStyleTo(DesignElement target)
    {
        base.CopyStyleTo(target);
        if (target is TextElement text) CopyStyleTo(text);
    }

    public override DesignElement Clone()
    {
        var c = new TextElement
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
            Text = Text,
            FontFamilyName = FontFamilyName,
            FontSize = FontSize,
            TextColor1 = TextColor1, TextColor2 = TextColor2,
            IsGradient = IsGradient, TextGradientAngle = TextGradientAngle,
            IsBold = IsBold, IsItalic = IsItalic,
            IsUnderline = IsUnderline, IsStrikethrough = IsStrikethrough,
            HasBorder = HasBorder, BorderColor = BorderColor, BorderThickness = BorderThickness,
            Direction = Direction,
            Alignment = Alignment,
            LetterSpacing = LetterSpacing, LineSpacing = LineSpacing,
            WrapWidth = WrapWidth,
        };
        c.EnableAnchoring();
        return c;
    }
}
