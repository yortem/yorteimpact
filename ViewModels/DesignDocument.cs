using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Media;
using YorteImpact.Models;

namespace YorteImpact.ViewModels;

/// <summary>
/// Document = flat element list + artboard geometry + selection + Z-order ops.
/// No tree, no layers panel: stacking is purely ZIndex (then normalized to 0..N).
/// Also owns the document name, canvas size text (shown in the top bar like
/// classic layout tools: "Untitled-1  1200×800 px"), and pasteboard extents
/// which grow automatically when a larger canvas is created.
/// </summary>
public class DesignDocument : INotifyPropertyChanged
{
    public ObservableCollection<DesignElement> Elements { get; } = new();

    private string _documentName = "Untitled-1";
    private double _artboardX = 900;
    private double _artboardY = 600;
    private double _artboardWidth = 1200;
    private double _artboardHeight = 800;
    private double _pasteboardWidth = 3000;
    private double _pasteboardHeight = 2000;

    private DesignElement? _selected;

    /// <summary>Canvas (artboard) paper color. Ignored when <see cref="CanvasTransparent"/> is true.</summary>
    private Color _canvasColor = Colors.White;

    /// <summary>When true the canvas has no background: it shows the checkerboard and exports transparent.</summary>
    private bool _canvasTransparent;

    public string DocumentName
    {
        get => _documentName;
        set => Set(ref _documentName, string.IsNullOrWhiteSpace(value) ? "Untitled" : value);
    }

    public double ArtboardX { get => _artboardX; set { if (Set(ref _artboardX, value)) { UpdatePasteboard(); Raise(nameof(ArtboardRect)); } } }
    public double ArtboardY { get => _artboardY; set { if (Set(ref _artboardY, value)) { UpdatePasteboard(); Raise(nameof(ArtboardRect)); } } }

    public double ArtboardWidth
    {
        get => _artboardWidth;
        set { if (Set(ref _artboardWidth, Math.Clamp(value, 50, 8000))) { UpdatePasteboard(); Raise(nameof(CanvasSizeText)); Raise(nameof(ArtboardRect)); } }
    }

    public double ArtboardHeight
    {
        get => _artboardHeight;
        set { if (Set(ref _artboardHeight, Math.Clamp(value, 50, 8000))) { UpdatePasteboard(); Raise(nameof(CanvasSizeText)); Raise(nameof(ArtboardRect)); } }
    }

    /// <summary>Canvas rect in pasteboard coords — elements are clipped to this.</summary>
    public Rect ArtboardRect => new(ArtboardX, ArtboardY, ArtboardWidth, ArtboardHeight);

    /// <summary>Scrollable pasteboard area. Grows when the canvas + margin needs it.</summary>
    public double PasteboardWidth { get => _pasteboardWidth; private set => Set(ref _pasteboardWidth, value); }
    public double PasteboardHeight { get => _pasteboardHeight; private set => Set(ref _pasteboardHeight, value); }

    /// <summary>Top-bar readout, e.g. "1200×800 px".</summary>
    public string CanvasSizeText => $"{ArtboardWidth:F0}×{ArtboardHeight:F0} px";

    public DesignElement? Selected
    {
        get => _selected;
        private set
        {
            if (_selected != null) _selected.IsSelected = false;
            _selected = value;
            if (_selected != null) _selected.IsSelected = true;
            Raise(nameof(Selected));
            Raise(nameof(HasSelection));
        }
    }

    public bool HasSelection => Selected != null;

    public Color CanvasColor
    {
        get => _canvasColor;
        set => Set(ref _canvasColor, value);
    }

    public bool CanvasTransparent
    {
        get => _canvasTransparent;
        set => Set(ref _canvasTransparent, value);
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public DesignDocument()
    {
        Elements.CollectionChanged += OnCollectionChanged;
    }

    private void OnCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.OldItems?.Contains(Selected) == true && Selected != null && !Elements.Contains(Selected))
            Select(null);
    }

    /// <summary>Keep the pasteboard comfortably larger than the artboard.</summary>
    private void UpdatePasteboard()
    {
        PasteboardWidth = Math.Max(3000, _artboardX + _artboardWidth + 600);
        PasteboardHeight = Math.Max(2000, _artboardY + _artboardHeight + 600);
    }

    /// <summary>
    /// Start a fresh canvas (File → New): clears everything, centers an
    /// artboard of the requested pixel size on the pasteboard.
    /// </summary>
    public void NewCanvas(string name, double width, double height)
        => NewCanvas(name, width, height, Colors.White, false);

    /// <summary>
    /// Start a fresh canvas with a specific paper color, or transparent
    /// (checkerboard + transparent export) when <paramref name="transparent"/>.
    /// </summary>
    public void NewCanvas(string name, double width, double height, Color color, bool transparent)
    {
        width = Math.Clamp(width, 50, 8000);
        height = Math.Clamp(height, 50, 8000);

        Elements.Clear();
        Select(null);
        DocumentName = name;

        CanvasColor = color;
        CanvasTransparent = transparent;

        // Size the pasteboard first, then center the artboard in it.
        _pasteboardWidth = Math.Max(3000, width + 1200);
        _pasteboardHeight = Math.Max(2000, height + 1200);
        _artboardWidth = width;
        _artboardHeight = height;
        _artboardX = (_pasteboardWidth - width) / 2;
        _artboardY = (_pasteboardHeight - height) / 2;

        Raise(nameof(ArtboardX)); Raise(nameof(ArtboardY));
        Raise(nameof(ArtboardWidth)); Raise(nameof(ArtboardHeight));
        Raise(nameof(ArtboardRect));
        Raise(nameof(PasteboardWidth)); Raise(nameof(PasteboardHeight));
        Raise(nameof(CanvasSizeText));
    }

    // ---------- selection ----------

    public void Select(DesignElement? el)
    {
        if (el != null && !Elements.Contains(el)) return;
        Selected = el;
    }

    // ---------- add / remove / duplicate ----------

    public T Add<T>(T el) where T : DesignElement
    {
        el.ZIndex = Elements.Count == 0 ? 0 : Elements.Max(e => e.ZIndex) + 1;
        Elements.Add(el);
        Select(el);
        return el;
    }

    public bool RemoveElement(DesignElement el)
    {
        if (!Elements.Remove(el)) return false;
        if (ReferenceEquals(Selected, el)) Select(null);
        return true;
    }

    public void RemoveSelected()
    {
        if (Selected == null) return;
        Elements.Remove(Selected);
        Select(null);
    }

    public void DuplicateSelected()
    {
        if (Selected == null) return;
        var clone = (DesignElement)Selected.Clone();
        clone.X += 20; // Duplicate nudges so both are visible…
        clone.Y += 20; // …while Copy/Paste below stays exactly in place.
        Add(clone);
    }

    // ---------- Z-order (the "no layers" stacking model) ----------

    public void BringToFront()
    {
        if (Selected == null || Elements.Count < 2) return;
        Selected.ZIndex = Elements.Max(e => e.ZIndex) + 1;
        NormalizeZ();
    }

    public void SendToBack()
    {
        if (Selected == null || Elements.Count < 2) return;
        Selected.ZIndex = Elements.Min(e => e.ZIndex) - 1;
        NormalizeZ();
    }

    public void BringForward()
    {
        if (Selected == null) return;
        var ordered = Elements.OrderBy(e => e.ZIndex).ToList();
        int i = ordered.IndexOf(Selected);
        if (i < 0 || i == ordered.Count - 1) return;
        (ordered[i].ZIndex, ordered[i + 1].ZIndex) = (ordered[i + 1].ZIndex, ordered[i].ZIndex);
        NormalizeZ();
        var s = Selected; Selected = null; Select(s);
    }

    public void SendBackward()
    {
        if (Selected == null) return;
        var ordered = Elements.OrderBy(e => e.ZIndex).ToList();
        int i = ordered.IndexOf(Selected);
        if (i <= 0) return;
        (ordered[i].ZIndex, ordered[i - 1].ZIndex) = (ordered[i - 1].ZIndex, ordered[i].ZIndex);
        NormalizeZ();
        var s = Selected; Selected = null; Select(s);
    }

    /// <summary>Reassign 0..N in current visual order so ZIndex never drifts.</summary>
    public void NormalizeZ()
    {
        var ordered = Elements.OrderBy(e => e.ZIndex).ToList();
        for (int i = 0; i < ordered.Count; i++)
            ordered[i].ZIndex = i;
    }

    // ---------- helpers ----------

    protected bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (Equals(field, value)) return false;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        return true;
    }

    protected void Raise(string name) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    /// <summary>Seed a couple of cards so the app doesn't open empty.</summary>
    public void SeedSample()
    {
        if (Elements.Count > 0) return;
        Add(new ShapeElement
        {
            X = ArtboardX + 80, Y = ArtboardY + 80,
            Width = 420, Height = 260, CornerRadius = 20,
            FillColor1 = System.Windows.Media.Color.FromRgb(0x3B, 0x82, 0xF6),
            FillColor2 = System.Windows.Media.Color.FromRgb(0x93, 0xC5, 0xFD),
            IsGradient = true,
        });
        Add(new ShapeElement
        {
            X = ArtboardX + 560, Y = ArtboardY + 120,
            Width = 480, Height = 320, CornerRadius = 8,
            FillColor1 = System.Windows.Media.Color.FromRgb(0xF8, 0xFA, 0xFC),
            IsGradient = false,
            StrokeColor = System.Windows.Media.Color.FromRgb(0xCB, 0xD5, 0xE1),
            StrokeThickness = 2,
        });
        Select(null);
    }
}
