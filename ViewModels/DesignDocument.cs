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
    private readonly List<DesignElement> _selectedElements = new();

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
        set { if (Set(ref _artboardWidth, Math.Clamp(value, 1, 8000))) { UpdatePasteboard(); Raise(nameof(CanvasSizeText)); Raise(nameof(ArtboardRect)); } }
    }

    public double ArtboardHeight
    {
        get => _artboardHeight;
        set { if (Set(ref _artboardHeight, Math.Clamp(value, 1, 8000))) { UpdatePasteboard(); Raise(nameof(CanvasSizeText)); Raise(nameof(ArtboardRect)); } }
    }

    /// <summary>Canvas rect in pasteboard coords — elements are clipped to this.</summary>
    public Rect ArtboardRect => new(ArtboardX, ArtboardY, ArtboardWidth, ArtboardHeight);

    /// <summary>The single raster layer painted directly on the canvas, if any.</summary>
    public PaintLayerElement? CanvasPaintLayer =>
        Elements.OfType<PaintLayerElement>().FirstOrDefault();

    public PaintLayerElement EnsureCanvasPaintLayer()
    {
        var layer = CanvasPaintLayer;
        if (layer == null)
        {
            layer = new PaintLayerElement { ZIndex = PaintLayerElement.BackgroundZ };
            Elements.Add(layer);
        }
        layer.X = ArtboardX;
        layer.Y = ArtboardY;
        layer.Width = ArtboardWidth;
        layer.Height = ArtboardHeight;
        layer.ZIndex = PaintLayerElement.BackgroundZ;
        return layer;
    }

    private void SyncCanvasPaintGeometry()
    {
        if (CanvasPaintLayer is not { } layer) return;
        layer.X = ArtboardX;
        layer.Y = ArtboardY;
        layer.Width = ArtboardWidth;
        layer.Height = ArtboardHeight;
        layer.ZIndex = PaintLayerElement.BackgroundZ;
    }

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
            if (ReferenceEquals(_selected, value)) return;
            _selected = value;
            Raise(nameof(Selected));
            Raise(nameof(HasSelection));
        }
    }

    /// <summary>All selected elements; the last-clicked one is <see cref="Selected"/>.</summary>
    public IReadOnlyList<DesignElement> SelectedElements => _selectedElements;

    public int SelectionCount => _selectedElements.Count;

    public bool HasSelection => _selected != null;

    public Rect SelectionBounds
    {
        get
        {
            if (_selectedElements.Count == 0) return Rect.Empty;
            Rect bounds = _selectedElements[0].Bounds;
            for (int i = 1; i < _selectedElements.Count; i++)
                bounds.Union(_selectedElements[i].Bounds);
            return bounds;
        }
    }

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
        if (e.OldItems != null)
        {
            foreach (DesignElement el in e.OldItems)
            {
                el.IsSelected = false;
                _selectedElements.Remove(el);
                if (ReferenceEquals(Selected, el))
                    Selected = _selectedElements.LastOrDefault();
            }
            Raise(nameof(SelectedElements));
            Raise(nameof(SelectionCount));
        }
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
        width = Math.Clamp(width, 1, 8000);
        height = Math.Clamp(height, 1, 8000);

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

    /// <summary>
    /// Move and resize the artboard to an exact rectangle without the normal
    /// canvas size limits. Used by crop-to-selection / crop-to-slice.
    /// </summary>
    public void SetArtboardRect(Rect rect)
    {
        if (rect.Width < 1 || rect.Height < 1) return;
        _artboardX = rect.X;
        _artboardY = rect.Y;
        _artboardWidth = rect.Width;
        _artboardHeight = rect.Height;
        UpdatePasteboard();
        Raise(nameof(ArtboardX)); Raise(nameof(ArtboardY));
        Raise(nameof(ArtboardWidth)); Raise(nameof(ArtboardHeight));
        Raise(nameof(ArtboardRect)); Raise(nameof(CanvasSizeText));
    }

    /// <summary>
    /// Change the artboard size without touching the elements. The artboard
    /// stays centered in the pasteboard, so the design keeps its center.
    /// </summary>
    public void ResizeCanvas(double width, double height)
    {
        width = Math.Clamp(width, 1, 8000);
        height = Math.Clamp(height, 1, 8000);
        if (Math.Abs(width - _artboardWidth) < 0.001 && Math.Abs(height - _artboardHeight) < 0.001)
            return;

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
        SyncCanvasPaintGeometry();
    }

    // ---------- selection ----------

    public void Select(DesignElement? el) =>
        SelectRange(el == null ? Array.Empty<DesignElement>() : new[] { el });

    public void AddToSelection(DesignElement el)
    {
        if (el.IsBackgroundLayer || !Elements.Contains(el)) return;
        if (!_selectedElements.Contains(el))
        {
            _selectedElements.Add(el);
            el.IsSelected = true;
            Raise(nameof(SelectedElements));
            Raise(nameof(SelectionCount));
        }
        Selected = el;
    }

    public void SelectRange(IEnumerable<DesignElement> elements)
    {
        var valid = elements
            .Where(e => !e.IsBackgroundLayer && Elements.Contains(e))
            .Distinct()
            .ToList();

        foreach (var el in _selectedElements) el.IsSelected = false;
        _selectedElements.Clear();
        foreach (var el in valid)
        {
            _selectedElements.Add(el);
            el.IsSelected = true;
        }

        Selected = valid.LastOrDefault();
        Raise(nameof(SelectedElements));
        Raise(nameof(SelectionCount));
    }

    // ---------- add / remove / duplicate ----------

    public T Add<T>(T el) where T : DesignElement
    {
        var regular = Elements.Where(e => !e.IsBackgroundLayer).ToList();
        el.ZIndex = regular.Count == 0 ? 0 : regular.Max(e => e.ZIndex) + 1;
        Elements.Add(el);
        Select(el);
        return el;
    }

    public bool RemoveElement(DesignElement el)
    {
        if (!Elements.Remove(el)) return false;
        if (ReferenceEquals(Selected, el)) Selected = _selectedElements.LastOrDefault();
        return true;
    }

    public void RemoveSelected()
    {
        foreach (var el in _selectedElements.ToList())
            Elements.Remove(el);
    }

    public void DuplicateSelected()
    {
        if (Selected == null || Selected.IsBackgroundLayer) return;
        var clone = (DesignElement)Selected.Clone();
        clone.X += 20; // Duplicate nudges so both are visible…
        clone.Y += 20; // …while Copy/Paste below stays exactly in place.
        Add(clone);
    }

    public List<DesignElement> DuplicateSelectedElements()
    {
        var copies = new List<DesignElement>();
        foreach (var el in _selectedElements.ToList())
        {
            var clone = el.Clone();
            clone.X += 20;
            clone.Y += 20;
            Add(clone);
            copies.Add(clone);
        }
        SelectRange(copies);
        return copies;
    }

    // ---------- Z-order (the "no layers" stacking model) ----------

    public void BringToFront()
    {
        var regular = Elements.Where(e => !e.IsBackgroundLayer).ToList();
        if (Selected == null || Selected.IsBackgroundLayer || regular.Count < 2) return;
        Selected.ZIndex = regular.Max(e => e.ZIndex) + 1;
        NormalizeZ();
    }

    public void SendToBack()
    {
        var regular = Elements.Where(e => !e.IsBackgroundLayer).ToList();
        if (Selected == null || Selected.IsBackgroundLayer || regular.Count < 2) return;
        Selected.ZIndex = regular.Min(e => e.ZIndex) - 1;
        NormalizeZ();
    }

    public void BringForward()
    {
        if (Selected == null || Selected.IsBackgroundLayer) return;
        var ordered = Elements.Where(e => !e.IsBackgroundLayer).OrderBy(e => e.ZIndex).ToList();
        int i = ordered.IndexOf(Selected);
        if (i < 0 || i == ordered.Count - 1) return;
        (ordered[i].ZIndex, ordered[i + 1].ZIndex) = (ordered[i + 1].ZIndex, ordered[i].ZIndex);
        NormalizeZ();
        Select(Selected);
    }

    public void SendBackward()
    {
        if (Selected == null || Selected.IsBackgroundLayer) return;
        var ordered = Elements.Where(e => !e.IsBackgroundLayer).OrderBy(e => e.ZIndex).ToList();
        int i = ordered.IndexOf(Selected);
        if (i <= 0) return;
        (ordered[i].ZIndex, ordered[i - 1].ZIndex) = (ordered[i - 1].ZIndex, ordered[i].ZIndex);
        NormalizeZ();
        Select(Selected);
    }

    /// <summary>Reassign 0..N in current visual order so ZIndex never drifts.</summary>
    public void NormalizeZ()
    {
        var ordered = Elements.Where(e => !e.IsBackgroundLayer).OrderBy(e => e.ZIndex).ToList();
        for (int i = 0; i < ordered.Count; i++)
            ordered[i].ZIndex = i;
        foreach (var layer in Elements.Where(e => e.IsBackgroundLayer))
            layer.ZIndex = PaintLayerElement.BackgroundZ;
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
