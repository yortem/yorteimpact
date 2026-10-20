using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using System.Windows.Shell;
using Microsoft.Win32;
using YorteImpact.Controls;
using YorteImpact.Models;
using YorteImpact.Services;
using YorteImpact.ViewModels;

namespace YorteImpact;

/// <summary>
/// Controller / behavior for the design surface (code-behind MVVM-lite).
/// Owns: active tool, mouse state machine (move / resize / rotate / slice),
/// zoom, copy/paste (internal prototype + system clipboard image), punch
/// Delete, new/open/save project and PNG/GIF/WebP export.
/// Stacking + selection state live in <see cref="DesignDocument"/>.
/// </summary>
public partial class MainWindow : Window
{
    private enum ActiveTool { Pick, Slice, Line, Rectangle, Text, Erase, Paintbrush, Elements }
    private enum DragMode { None, Move, Resize, Rotate, Slice, Draw, DrawLine, Paint, Marquee, TextPress }
    private enum LineToolMode { Line, Polyline, Freehand }
    private enum SliceToolMode { Rectangle, Bezier }
    private enum RightPanelMode { Inspector, Photo, Grid }

    private sealed class AddConverter : IValueConverter
    {
        private readonly double _offset;
        public AddConverter(double offset) => _offset = offset;

        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
            => value is double d ? d + _offset : value;

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
            => throw new NotSupportedException();
    }

    /// <summary>One open canvas/project tab. Holds everything that is per-document.</summary>
    private sealed class EditorTab
    {
        public DesignDocument Document { get; } = new();
        public UndoService Undo { get; } = new();
        public string? ProjectPath { get; set; }
        public bool IsDirty { get; set; }
        public Color PrimaryColor { get; set; } = Color.FromRgb(0x3B, 0x82, 0xF6);
        public List<SliceRegion> Slices { get; set; } = new();
        public SliceRegion? ActiveSlice { get; set; }
        public bool SliceVisible { get; set; } = true;
        public double Zoom { get; set; }
        public double ScrollX { get; set; }
        public double ScrollY { get; set; }
    }

    private sealed class BrushPresetItem
    {
        public string Name { get; init; } = "";
        public BrushDefaults Settings { get; init; } = new();
        public bool IsBuiltIn { get; init; }
        public bool IsCustom { get; init; }
        public bool CanDelete => !IsBuiltIn && !IsCustom;
        public ImageSource? Preview { get; init; }
    }

    private const double MinSize = 1;
    private const double MinZoom = 0.05;
    private const double MaxZoom = 8.0;

    /// <summary>Radius (screen px) in which a guide grabs the object.</summary>
    private const double SnapCapturePx = 14;
    /// <summary>Radius (screen px) in which a grabbed guide lets the object go.</summary>
    private const double SnapReleasePx = 26;

    /// <summary>Width of the Elements panel (must match the XAML).</summary>
    private const double ElementsSidebarWidth = 280;
    private static readonly Duration ElementsSidebarDuration = new(TimeSpan.FromMilliseconds(220));
    private int _elementsSidebarVersion;

    [DllImport("user32.dll")]
    private static extern uint GetClipboardSequenceNumber();

    private DesignDocument _doc = new();
    private readonly List<EditorTab> _tabs = new();
    private int _activeTab = -1;
    private TextBlock? _activeTabMeta;
    private ActiveTool _tool = ActiveTool.Pick;
    private DragMode _drag = DragMode.None;

    // Drag start snapshot.
    private Point _dragStartCanvas;
    private Point _drawStart;
    private readonly List<Point> _polygonDraft = new();
    private readonly List<Point> _lineDraft = new();
    private LineToolMode _lineMode = LineToolMode.Line;
    private double _origX, _origY, _origW, _origH;
    private double _origFontSize;
    private Point _origCenter;
    private double _startPointerAngle;
    private double _startElementAngle;
    private HandleKind _activeHandle = HandleKind.None;
    private readonly Dictionary<DesignElement, Point> _moveOrigins = new();
    private bool _moveUndoPending;
    private bool _ctrlGesturePending;
    private DesignElement? _ctrlGestureElement;
    private Point _ctrlGestureStart;
    private bool _ctrlGestureWasSelected;
    private bool _marqueeAdditive;

    // In-place text editing.
    private TextElement? _editingText;
    private TextElement? _textPressTarget;
    private TextBox? _textEditor;
    private double _editScrollX, _editScrollY;
    private string? _fontOriginal;
    private readonly List<string> _allFonts = new();
    private bool _fontPreviewed;
    private bool _fontCommitted;
    private bool _fontUndoPushed;

    // Zoom + slices + copy state.
    private double _zoom = 1.0;
    private double _pasteOffsetX; // ScrollHost coordinate of Pasteboard model origin
    private double _pasteOffsetY;
    private List<SliceRegion> _slices = new(); // every drawn region (add more freely)
    private SliceRegion? _activeSlice;                  // target for export / copy / punch
    private Rect? _pendingSlice;                        // live drag preview
    private bool _sliceVisible = true;
    private bool _sliceAddMode;                         // false = each drag replaces; true = accumulate (+)
    private SliceToolMode _sliceToolMode;
    private readonly List<Point> _sliceBezierDraft = new();
    private Point? _sliceBezierCursor;
    private DesignElement? _copiedPrototype;
    private uint _copyClipboardSeq;
    private int _untitledCounter = 1;
    private string? _projectPath;
    private UndoService _undo = new();
    private bool _inspectorArmed;

    // Paintbrush
    private readonly BrushSettings _brushSettings = new();
    private readonly BrushEngine _brushEngine = new();
    private BrushSurface? _brushSurface;
    private ImageElement? _brushImage;
    private bool _brushOutside;
    private bool _suppressBrushToolbar;
    private readonly List<BrushPresetItem> _brushPresetItems = new();
    private byte[]? _mergePixels;
    private int _mergeWidth, _mergeHeight, _mergeStride;
    private Color _mergeFallback = Colors.White;

    /// <summary>Project primary color: sampled with the eyedropper, used for new shapes, saved in .yimpact.</summary>
    private System.Windows.Media.Color _primaryColor = System.Windows.Media.Color.FromRgb(0x3B, 0x82, 0xF6);

    /// <summary>Persisted preferences, including the last-used style for new shapes.</summary>
    private readonly AppSettings _settings = AppSettings.Load();

    /// <summary>Style used for the next text element when no text is selected.</summary>
    private readonly TextElement _textDefaults = new();
    private bool _suppressTextToolbar;
    private bool _suppressShapeToolbar;
    private bool _suppressLineToolbar;

    private bool _suppressInspector;
    private bool _suppressPhoto;
    private bool _suppressGrid;
    private RightPanelMode _rightPanelMode = RightPanelMode.Inspector;
    private ImageSource? _photoPresetSource;
    private readonly List<ShadowPresetItem> _shadowPresets = new()
    {
        ShadowPreset("None", ShadowStyle.None),
        ShadowPreset("Around", ShadowStyle.Ambient),
        ShadowPreset("Bottom soft", ShadowStyle.Soft),
        ShadowPreset("Card", ShadowStyle.Card),
        ShadowPreset("Raised", ShadowStyle.Raised),
        ShadowPreset("Crisp", ShadowStyle.Crisp),
        ShadowPreset("Float", ShadowStyle.Float),
        ShadowPreset("Hard", ShadowStyle.Hard),
        ShadowPreset("Glow", ShadowStyle.Glow),
    };
    private readonly List<TransparencyPresetItem> _transparencyPresets = new()
    {
        TransparencyPreset("Top", "Top to bottom", OpacityFadeDirection.TopToBottom,
            new Point(0.5, 0), new Point(0.5, 1)),
        TransparencyPreset("Bottom", "Bottom to top", OpacityFadeDirection.BottomToTop,
            new Point(0.5, 1), new Point(0.5, 0)),
        TransparencyPreset("Left", "Left to right", OpacityFadeDirection.LeftToRight,
            new Point(0, 0.5), new Point(1, 0.5)),
        TransparencyPreset("Right", "Right to left", OpacityFadeDirection.RightToLeft,
            new Point(1, 0.5), new Point(0, 0.5)),
        TransparencyPreset("↘", "Top-left to bottom-right", OpacityFadeDirection.DiagonalDown,
            new Point(0, 0), new Point(1, 1)),
        TransparencyPreset("↗", "Top-right to bottom-left", OpacityFadeDirection.DiagonalUp,
            new Point(1, 0), new Point(0, 1)),
        TransparencyPreset("↙", "Bottom-left to top-right", OpacityFadeDirection.DiagonalDownReverse,
            new Point(0, 1), new Point(1, 0)),
        TransparencyPreset("↖", "Bottom-right to top-left", OpacityFadeDirection.DiagonalUpReverse,
            new Point(1, 1), new Point(0, 0)),
    };
    private readonly List<PhotoPreset> _photoPresets = new()
    {
        new PhotoPreset("Original", new PhotoAdjustments()),
        new PhotoPreset("Auto", new PhotoAdjustments(), isAuto: true),
        new PhotoPreset("B&W", new PhotoAdjustments { Saturation = -100, Contrast = 16, Grain = 8 }),
        new PhotoPreset("HDR", new PhotoAdjustments { Contrast = 28, Highlights = -35, Shadows = 38, Clarity = 22, Vibrance = 12 }),
        new PhotoPreset("Golden", new PhotoAdjustments { Temperature = 30, Exposure = 0.1, Highlights = -18, Shadows = 16, Vibrance = 20 }),
        new PhotoPreset("Matte", new PhotoAdjustments { Contrast = -15, Blacks = 20, Highlights = -10, Saturation = -10 }),
        new PhotoPreset("Cool", new PhotoAdjustments { Temperature = -28, Tint = -5, Contrast = 10, Saturation = -5 }),
        new PhotoPreset("Vivid", new PhotoAdjustments { Contrast = 15, Vibrance = 35, Saturation = 12, Clarity = 10 }),
        new PhotoPreset("Film", new PhotoAdjustments { Temperature = 8, Contrast = -5, Vibrance = -8, Grain = 18, Vignette = 12 }),
        new PhotoPreset("Amber", new PhotoAdjustments { ToneColor = Color.FromRgb(0xFF, 0x8A, 0x3D), ToneAmount = 65, Contrast = 8 }),
        new PhotoPreset("Violet", new PhotoAdjustments { ToneColor = Color.FromRgb(0xA8, 0x55, 0xF7), ToneAmount = 65, Contrast = 8 }),
        new PhotoPreset("Portrait", new PhotoAdjustments { Contrast = 6, Highlights = -12, Shadows = 10, Temperature = 8, Vibrance = 15, Clarity = -8, Glow = 8, Vignette = 8 }),
        new PhotoPreset("Landscape", new PhotoAdjustments { Contrast = 18, Highlights = -25, Shadows = 20, Whites = 8, Blacks = -10, Vibrance = 25, Clarity = 25, Sharpen = 18 }),
        new PhotoPreset("Faded", new PhotoAdjustments { Contrast = -20, Blacks = 25, Highlights = -15, Saturation = -20, Grain = 8 }),
        new PhotoPreset("Noir", new PhotoAdjustments { Saturation = -100, Contrast = 32, Blacks = -20, Vignette = 25, Grain = 12 }),
        new PhotoPreset("Sepia", new PhotoAdjustments { ToneColor = Color.FromRgb(0xC0, 0x8A, 0x5A), ToneAmount = 55, Contrast = 8, Grain = 6 }),
        new PhotoPreset("Cinema", new PhotoAdjustments { Temperature = 12, Tint = -8, Contrast = 15, Vibrance = 20, Shadows = 8, Highlights = -10, Saturation = 8 }),
        new PhotoPreset("Sunset", new PhotoAdjustments { Temperature = 32, Exposure = 0.12, Highlights = -20, Shadows = 15, Vibrance = 25, Glow = 12 }),
        new PhotoPreset("Silver", new PhotoAdjustments { Saturation = -100, ToneColor = Color.FromRgb(0xB8, 0xC4, 0xD4), ToneAmount = 25, Contrast = 20, Grain = 10 }),
        new PhotoPreset("Soft", new PhotoAdjustments { Contrast = -8, Shadows = 15, Highlights = -10, Clarity = -20, Glow = 18, Saturation = -5 }),
        new PhotoPreset("Dramatic", new PhotoAdjustments { Contrast = 35, Highlights = -45, Shadows = 25, Blacks = -25, Clarity = 35, Vignette = 25, Sharpen = 22 }),
        new PhotoPreset("Vintage", new PhotoAdjustments { Temperature = 18, Contrast = -10, Blacks = 15, Highlights = -12, Saturation = -12, Grain = 20, Vignette = 20 }),
        new PhotoPreset("Dream", new PhotoAdjustments { Contrast = -12, Shadows = 20, Highlights = -10, Clarity = -15, Glow = 30, Saturation = -8, Vignette = 10 }),
    };
    private bool _lockAspectRatio;
    private bool _lockDrawRatio;
    private bool _rememberMaximized;
    private bool _startMaximized;
    private bool _windowClosing;

    /// <summary>Classic transparency checkerboard shown when the canvas has no background.</summary>
    private static readonly Brush CheckerBrush = BuildCheckerBrush();

    /// <summary>Editor startup with the default canvas; optionally opens a project file as the first tab.</summary>
    public MainWindow(int width, int height, Color background, bool transparent, string? startupProject = null)
    {
        _suppressBrushToolbar = true; // XAML construction raises slider/selection events early
        _suppressLineToolbar = true;
        _suppressPhoto = true;
        _suppressGrid = true;
        InitializeComponent();
        PhotoPresetList.ItemsSource = _photoPresets;
        ShadowPresetList.ItemsSource = _shadowPresets;
        TransparencyPresetList.ItemsSource = _transparencyPresets;
        _rememberMaximized = _settings.WindowMaximized;
        _startMaximized = _settings.WindowMaximized;
        ApplySavedWindowPlacement();
        AttrRatioLockBtn.IsChecked = _lockAspectRatio;
        InspRatioLockBtn.IsChecked = _lockAspectRatio;
        UpdateRatioLockIcon();
        AttrDrawRatioLockBtn.IsChecked = _lockDrawRatio;
        UpdateDrawRatioLockIcon();
        if (TryParseColor(_settings.Shape.Fill1, out var savedFill)) _primaryColor = savedFill;
        if (_settings.Shape.ShapeKind == nameof(ShapeKind.CustomPath))
            _settings.Shape.ShapeKind = nameof(ShapeKind.Rectangle);
        if (_settings.Text is { } savedText) ApplyTextDefaults(savedText);
        else _textDefaults.TextColor1 = _primaryColor;
        _settings.Brush ??= new BrushDefaults();
        _settings.BrushPresets ??= new List<BrushDefaults>();
        _settings.Line ??= new LineDefaults();
        _lineMode = ParseLineToolMode(_settings.Line.Mode);
        ApplyBrushDefaults(_settings.Brush);
        RebuildBrushPresetList();
        _suppressBrushToolbar = false;
        _suppressLineToolbar = false;
        _suppressPhoto = false;
        _suppressGrid = false;

        EditorTab first;
        if (!string.IsNullOrWhiteSpace(startupProject))
        {
            try
            {
                first = LoadProjectTab(startupProject);
            }
            catch (Exception ex)
            {
                MessageBox.Show("Could not open project:\n" + ex.Message, "YorteImpact",
                    MessageBoxButton.OK, MessageBoxImage.Error);
                first = new EditorTab { PrimaryColor = _primaryColor };
                first.Document.NewCanvas("Untitled-1", width, height, background, transparent);
            }
        }
        else
        {
            first = new EditorTab { PrimaryColor = _primaryColor };
            first.Document.NewCanvas("Untitled-1", width, height, background, transparent);
        }
        HookDocument(first.Document);
        _tabs.Add(first);
        _activeTab = 0;
        _doc = first.Document;
        _undo = first.Undo;
        _projectPath = first.ProjectPath;
        _primaryColor = first.PrimaryColor;
        _slices = first.Slices;
        _activeSlice = first.ActiveSlice;
        _sliceVisible = first.SliceVisible;
        RefreshPrimaryColorSwatch();

        DataContext = _doc;
        SourceInitialized += (_, _) =>
        {
            DarkTitleBar.Apply(this);
            if (PresentationSource.FromVisual(this) is HwndSource source)
                source.AddHook(WmGetMinMaxInfo);
            if (_startMaximized) WindowState = WindowState.Maximized;
        };
        StateChanged += (_, _) =>
        {
            if (!_windowClosing)
            {
                if (WindowState == WindowState.Maximized) _rememberMaximized = true;
                else if (WindowState == WindowState.Normal) _rememberMaximized = false;
            }
            UpdateMaxRestoreGlyph();
        };
        Closing += Window_Closing;
        RebuildTabs();
        UpdateEmptyState();
        OnSelectionChanged();
        UpdateArtboardFill();
        UpdateToolUI();
        UpdateZoomUI();
        foreach (var tb in new[] { Fill1Text, Fill2Text, StrokeText,
                                   ShadowText, ShadowX.Editor, ShadowY.Editor, ShadowBlur.Editor, ShadowOpacity.Editor,
                                   TextColor1Text, TextColor2Text, TextShadowText,
                                   TextSize.Editor, TextGradAngle.Editor, TextLetterSpacing.Editor, TextLineSpacing.Editor,
                                   TextWrapWidth.Editor,
                                   TextShadowX.Editor, TextShadowY.Editor, TextShadowBlur.Editor, TextShadowOpacity.Editor,
                                   OpacityStrength.Editor, OpacityStart.Editor, OpacityEnd.Editor,
                                   InspX.Editor, InspY.Editor, InspW.Editor, InspH.Editor,
                                   InspRot.Editor, InspRadius.Editor, InspGradAngle.Editor, InspStrokeThick.Editor,
                                   AttrOpacity.Editor, AttrCorner.Editor, AttrSoft.Editor,
                                   PhotoExposure.Editor, PhotoContrast.Editor, PhotoHighlights.Editor,
                                   PhotoShadows.Editor, PhotoWhites.Editor, PhotoBlacks.Editor,
                                   PhotoTemperature.Editor, PhotoTint.Editor, PhotoVibrance.Editor,
                                   PhotoSaturation.Editor, PhotoClarity.Editor, PhotoVignette.Editor,
                                   PhotoGrain.Editor,
                                   AttrTextSize.Editor, AttrTextBorderThickness.Editor, AttrTextSpacing.Editor })
            tb.GotFocus += (_, _) => _inspectorArmed = true; // next keystroke snapshots first
        // Any click on the pick-tool settings (sliders, steppers) arms one undo snapshot.
        AttrPickPanel.PreviewMouseLeftButtonDown += (_, _) => _inspectorArmed = true;
        AttrTextPanel.PreviewMouseLeftButtonDown += (_, _) => _inspectorArmed = true;
        InspectorPanel.PreviewMouseLeftButtonDown += (_, _) => _inspectorArmed = true;
        PhotoPanel.PreviewMouseLeftButtonDown += (_, _) => _inspectorArmed = true;
        ShadowCard.PreviewMouseLeftButtonDown += (_, _) => _inspectorArmed = true;
        TextCard.PreviewMouseLeftButtonDown += (_, _) => _inspectorArmed = true;

        _allFonts.AddRange(Fonts.SystemFontFamilies
            .Select(f => f.Source).Distinct()
            .OrderBy(n => n, StringComparer.CurrentCultureIgnoreCase));
        TextFontCombo.ItemsSource = _allFonts;
        TextFontCombo.SelectedItem = "Segoe UI";
        AttrTextFontCombo.ItemsSource = _allFonts;
        AttrEraseTolerance.SetValue(25); // sensible default magic-wand tolerance
        PreviewMouseDown += (_, _) => ValueSlider.CloseAllPopups();
        Deactivated += (_, _) => ValueSlider.CloseAllPopups();
        Loaded += (_, _) => ZoomToFit(); // show 100% of the canvas once layout exists
        Closed += (_, _) =>
        {
            PersistShapeDefaults();
            PersistLineDefaults();
            PersistTextDefaults();
            PersistBrushDefaults();
            PersistWindowPlacement();
            ScreenColorSampler.Stop();
        };
    }

    // =================================================================
    // Tabs (one canvas/project per tab)
    // =================================================================

    private void HookDocument(DesignDocument doc) => doc.PropertyChanged += OnDocumentPropertyChanged;

    private void OnDocumentPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (!ReferenceEquals(sender, _doc)) return;
        if (e.PropertyName is nameof(DesignDocument.Selected) or nameof(DesignDocument.HasSelection)
            or nameof(DesignDocument.SelectedElements) or nameof(DesignDocument.SelectionCount))
            OnSelectionChanged();
        else if (e.PropertyName is nameof(DesignDocument.CanvasColor) or nameof(DesignDocument.CanvasTransparent))
        {
            UpdateArtboardFill();
            RefreshGridPanel();
        }
        else if (e.PropertyName is nameof(DesignDocument.ArtboardWidth) or nameof(DesignDocument.ArtboardHeight))
            RefreshActiveTabMeta();
        else if (e.PropertyName is nameof(DesignDocument.DocumentName))
        {
            UpdateWindowTitle();
            RebuildTabs();
        }
    }

    private void StoreActiveTabState()
    {
        if (_activeTab < 0 || _activeTab >= _tabs.Count) return;
        var tab = _tabs[_activeTab];
        tab.ProjectPath = _projectPath;
        tab.PrimaryColor = _primaryColor;
        tab.Slices = _slices;
        tab.ActiveSlice = _activeSlice;
        tab.SliceVisible = _sliceVisible;
        tab.Zoom = _zoom;
        tab.ScrollX = Viewport.HorizontalOffset;
        tab.ScrollY = Viewport.VerticalOffset;
    }

    private void ActivateTab(int index)
    {
        if (index < 0 || index >= _tabs.Count) return;
        if (index == _activeTab)
        {
            RebuildTabs();
            return;
        }

        CommitTextEdit();
        if (_activeTab >= 0) StoreActiveTabState();

        if (_brushEngine.IsActive) EndBrushStroke();
        BrushCursor.Visibility = Visibility.Collapsed;
        if (Pasteboard.IsMouseCaptured) Pasteboard.ReleaseMouseCapture();
        CancelCtrlGesture();
        _drag = DragMode.None;
        DrawShapeVisual.Visibility = Visibility.Collapsed;
        ResetLineDraft();
        MarqueeVisual.Visibility = Visibility.Collapsed;
        ResetPolygonDraft();
        _pendingSlice = null;

        _activeTab = index;
        var tab = _tabs[index];

        _doc = tab.Document;
        _undo = tab.Undo;
        _projectPath = tab.ProjectPath;
        _primaryColor = tab.PrimaryColor;
        _slices = tab.Slices;
        _activeSlice = tab.ActiveSlice;
        _sliceVisible = tab.SliceVisible;
        _zoom = tab.Zoom > 0.01 ? Math.Clamp(tab.Zoom, MinZoom, MaxZoom) : 1.0;
        Pasteboard.LayoutTransform = new ScaleTransform(_zoom, _zoom);

        RefreshPrimaryColorSwatch();
        DataContext = _doc;
        OnSelectionChanged();
        UpdateArtboardFill();
        RefreshSliceLayer();
        UpdateZoomUI();
        RebuildTabs();
        UpdateEmptyState();

        Dispatcher.BeginInvoke(new Action(() =>
        {
            if (_activeTab >= 0 && _tabs[_activeTab] == tab) RestoreViewState(tab);
        }));
    }

    private void RestoreViewState(EditorTab tab)
    {
        if (tab.Zoom <= 0.01)
        {
            ZoomToFit();
            return;
        }
        LayoutViewport();
        Pasteboard.UpdateLayout();
        ScrollHost.UpdateLayout();
        Viewport.ScrollToHorizontalOffset(tab.ScrollX);
        Viewport.ScrollToVerticalOffset(tab.ScrollY);
        UpdateZoomUI();
    }

    private void RebuildTabs()
    {
        if (TabStrip == null) return;
        TabStrip.Children.Clear();
        _activeTabMeta = null;

        for (int i = 0; i < _tabs.Count; i++)
        {
            int index = i;
            var tab = _tabs[i];
            bool active = i == _activeTab;

            var border = new Border
            {
                Height = 32,
                MinWidth = active ? 272 : 152,
                MaxWidth = active ? 448 : 272,
                Margin = new Thickness(2, 4, 6, 4),
                Padding = new Thickness(10, 0, 4, 0),
                CornerRadius = new CornerRadius(6),
                BorderThickness = new Thickness(0),
                Background = new SolidColorBrush(active
                    ? Color.FromRgb(0x1A, 0x26, 0x36)
                    : Colors.Transparent),
                BorderBrush = Brushes.Transparent,
                VerticalAlignment = VerticalAlignment.Bottom,
                Cursor = Cursors.Hand,
                ToolTip = tab.ProjectPath ?? tab.Document.DocumentName,
            };
            WindowChrome.SetIsHitTestVisibleInChrome(border, true);

            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            Color badgeColor = TabContentColor(tab) ?? TabNameColor(tab.Document.DocumentName);
            bool lightBadge = IsLightColor(badgeColor);

            var icon = new Border
            {
                Width = 16,
                Height = 16,
                CornerRadius = new CornerRadius(4),
                Background = new SolidColorBrush(badgeColor),
                BorderBrush = new SolidColorBrush(lightBadge
                    ? Color.FromArgb(0x38, 0, 0, 0)
                    : Color.FromArgb(0x55, 0xFF, 0xFF, 0xFF)),
                BorderThickness = new Thickness(1),
                Opacity = active ? 1 : 0.75,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 8, 0),
                Child = new TextBlock
                {
                    Text = TabInitial(tab.Document.DocumentName),
                    Foreground = new SolidColorBrush(lightBadge
                        ? Color.FromRgb(0x0B, 0x12, 0x1B)
                        : Colors.White),
                    FontFamily = new FontFamily("Segoe UI"),
                    FontWeight = FontWeights.Bold,
                    FontSize = 9,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center,
                },
            };

            var title = new TextBlock
            {
                Text = tab.Document.DocumentName,
                FontSize = 12,
                FontWeight = active ? FontWeights.SemiBold : FontWeights.Normal,
                Foreground = new SolidColorBrush(active
                    ? Color.FromRgb(0xE6, 0xEE, 0xF6)
                    : Color.FromRgb(0x8E, 0xA0, 0xB4)),
                VerticalAlignment = VerticalAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis,
            };

            var meta = new TextBlock
            {
                Text = $"{tab.Document.CanvasSizeText}  ·  {_zoom * 100:F0}%",
                FontSize = 12,
                FontWeight = FontWeights.SemiBold,
                Foreground = new SolidColorBrush(Color.FromRgb(0xE6, 0xEE, 0xF6)),
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(12, 0, 0, 0),
                Visibility = active ? Visibility.Visible : Visibility.Collapsed,
            };
            if (active) _activeTabMeta = meta;

            var divider = new Border
            {
                Width = 1,
                Height = 14,
                Background = new SolidColorBrush(Color.FromRgb(0x2E, 0x3F, 0x54)),
                Opacity = active ? 1 : 0.6,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(8, 0, 8, 0),
            };

            var close = new Button
            {
                Style = (Style)FindResource("TabCloseBtn"),
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 2, 0),
            };
            close.Click += (_, e) => { e.Handled = true; CloseTab(index); };
            WindowChrome.SetIsHitTestVisibleInChrome(close, true);

            Grid.SetColumn(icon, 0);
            Grid.SetColumn(title, 1);
            Grid.SetColumn(meta, 2);
            Grid.SetColumn(divider, 3);
            Grid.SetColumn(close, 4);
            grid.Children.Add(icon);
            grid.Children.Add(title);
            grid.Children.Add(meta);
            grid.Children.Add(divider);
            grid.Children.Add(close);
            border.Child = grid;

            if (!active)
            {
                border.MouseEnter += (_, _) =>
                {
                    if (index != _activeTab)
                        border.Background = new SolidColorBrush(Color.FromRgb(0x16, 0x20, 0x2D));
                };
                border.MouseLeave += (_, _) =>
                {
                    if (index != _activeTab) border.Background = Brushes.Transparent;
                };
            }

            border.MouseLeftButtonDown += (_, e) =>
            {
                if (e.ChangedButton == MouseButton.Left) ActivateTab(index);
            };
            TabStrip.Children.Add(border);
        }
    }

    private static readonly Color[] TabBadgePalette =
    {
        Color.FromRgb(0xE5, 0x48, 0x4D), Color.FromRgb(0xF7, 0x6B, 0x15),
        Color.FromRgb(0xD4, 0xA1, 0x06), Color.FromRgb(0x30, 0xA4, 0x6C),
        Color.FromRgb(0x12, 0xA5, 0x94), Color.FromRgb(0x3B, 0x82, 0xF6),
        Color.FromRgb(0x8E, 0x4E, 0xC6), Color.FromRgb(0xDB, 0x3F, 0xA0),
    };

    private static Color? TabContentColor(EditorTab tab)
    {
        try
        {
            var colors = ColorPickerDialog.CanvasColors(tab.Document);
            if (colors.Count == 0) return null;

            var first = colors[0];
            if (!tab.Document.CanvasTransparent &&
                NearColor(first, tab.Document.CanvasColor, 14))
            {
                return colors.Count > 1 ? colors[1] : null;
            }
            return first;
        }
        catch
        {
            return null;
        }
    }

    private static Color TabNameColor(string? name)
    {
        uint hash = 2166136261;
        foreach (char ch in name ?? string.Empty)
            hash = unchecked((hash ^ ch) * 16777619);
        return TabBadgePalette[(int)(hash % (uint)TabBadgePalette.Length)];
    }

    private static string TabInitial(string? name)
    {
        string trimmed = name?.Trim() ?? string.Empty;
        return trimmed.Length == 0
            ? "?"
            : char.ToUpperInvariant(trimmed[0]).ToString();
    }

    private static bool IsLightColor(Color c) =>
        0.299 * c.R + 0.587 * c.G + 0.114 * c.B > 160;

    private static bool NearColor(Color a, Color b, int tolerance) =>
        Math.Abs(a.R - b.R) <= tolerance &&
        Math.Abs(a.G - b.G) <= tolerance &&
        Math.Abs(a.B - b.B) <= tolerance;

    private void NewTab_Click(object sender, RoutedEventArgs e) => AddBlankTab();

    private void AddBlankTab()
    {
        CommitTextEdit();
        var s = AppSettings.Load();
        var tab = new EditorTab { PrimaryColor = _primaryColor, Zoom = 0 };
        HookDocument(tab.Document);
        var bg = s.DefaultCanvasTransparent ? Colors.Transparent : Colors.White;
        tab.Document.NewCanvas($"Untitled-{++_untitledCounter}",
            s.DefaultCanvasWidth, s.DefaultCanvasHeight, bg, s.DefaultCanvasTransparent);
        _tabs.Add(tab);
        ActivateTab(_tabs.Count - 1);
        SetTool(ActiveTool.Pick);
        StatusText.Text = s.DefaultCanvasTransparent
            ? $"New transparent canvas tab {s.DefaultCanvasWidth}×{s.DefaultCanvasHeight}px."
            : $"New canvas tab {s.DefaultCanvasWidth}×{s.DefaultCanvasHeight}px.";
    }

    private void DuplicateProjectTab()
    {
        if (_activeTab < 0 || _activeTab >= _tabs.Count) return;
        CommitTextEdit();
        StoreActiveTabState();

        var source = _tabs[_activeTab];
        var copy = new EditorTab { PrimaryColor = _primaryColor, Zoom = 0, IsDirty = true };
        HookDocument(copy.Document);
        YorteProjectService.ApplyTo(YorteProjectService.Capture(source.Document), copy.Document);
        copy.Document.DocumentName = source.Document.DocumentName + " copy";

        _tabs.Add(copy);
        ActivateTab(_tabs.Count - 1);
        SetTool(ActiveTool.Pick);
        StatusText.Text = $"Duplicated the project into a new tab ({copy.Document.DocumentName}).";
    }

    private void CloseTab(int index)
    {
        if (index < 0 || index >= _tabs.Count) return;
        var tab = _tabs[index];
        if (!ConfirmCloseTab(tab, index)) return;

        bool wasActive = index == _activeTab;
        if (wasActive)
        {
            CommitTextEdit();
            StoreActiveTabState();
        }

        _tabs.RemoveAt(index);

        if (_tabs.Count == 0)
        {
            EnterEmptyState();
            return;
        }

        if (index < _activeTab) _activeTab--;
        if (wasActive)
        {
            _activeTab = -1;
            ActivateTab(Math.Min(index, _tabs.Count - 1));
        }
        else
        {
            RebuildTabs();
        }
    }

    private void Window_Closing(object? sender, CancelEventArgs e)
    {
        if (_windowClosing) return;

        CommitTextEdit();
        foreach (var tab in _tabs.ToList())
        {
            if (!tab.IsDirty) continue;
            int index = _tabs.IndexOf(tab);
            if (index < 0) continue;
            if (!ConfirmCloseTab(tab, index))
            {
                e.Cancel = true;
                return;
            }
        }

        _windowClosing = true;
    }

    /// <summary>Returns false when the user cancels (or a save is not completed).</summary>
    private bool ConfirmCloseTab(EditorTab tab, int index)
    {
        if (!tab.IsDirty) return true;

        var result = MessageBox.Show(this,
            $"Save changes to \"{tab.Document.DocumentName}\" before closing?",
            "YorteImpact", MessageBoxButton.YesNoCancel, MessageBoxImage.Warning);
        if (result == MessageBoxResult.Cancel) return false;
        if (result == MessageBoxResult.No) return true;

        if (index != _activeTab) ActivateTab(index);
        SaveDocument(forceDialog: false);
        return !tab.IsDirty;
    }

    /// <summary>Leave the editor open with no tabs and show the empty-state card.</summary>
    private void EnterEmptyState()
    {
        _activeTab = -1;
        _doc = new DesignDocument();
        var emptySettings = AppSettings.Load();
        var emptyBg = emptySettings.DefaultCanvasTransparent ? Colors.Transparent : Colors.White;
        _doc.NewCanvas("Untitled", emptySettings.DefaultCanvasWidth, emptySettings.DefaultCanvasHeight,
            emptyBg, emptySettings.DefaultCanvasTransparent);
        DataContext = _doc;
        _undo = new UndoService();
        _slices = new List<SliceRegion>();
        _activeSlice = null;
        _sliceVisible = true;
        _projectPath = null;

        if (Pasteboard.IsMouseCaptured) Pasteboard.ReleaseMouseCapture();
        CancelCtrlGesture();
        _drag = DragMode.None;
        DrawShapeVisual.Visibility = Visibility.Collapsed;
        ResetLineDraft();
        MarqueeVisual.Visibility = Visibility.Collapsed;
        ResetPolygonDraft();

        OnSelectionChanged();
        UpdateArtboardFill();
        RefreshSliceLayer();
        UpdateZoomUI();
        RebuildTabs();
        UpdateEmptyState();
        SetTool(ActiveTool.Pick);
        StatusText.Text = "No project open — create one, or paste/drop an image.";
    }

    private void UpdateEmptyState()
    {
        bool empty = _tabs.Count == 0;
        EmptyState.Visibility = empty ? Visibility.Visible : Visibility.Collapsed;
        InspectorBorder.Visibility = empty ? Visibility.Collapsed : Visibility.Visible;
        InspectorSplitter.Visibility = empty ? Visibility.Collapsed : Visibility.Visible;
        DocInfo.Visibility = empty ? Visibility.Collapsed : Visibility.Visible;
        GridToggleBtn.Visibility = empty ? Visibility.Collapsed : Visibility.Visible;
        GuideOverlay.Visibility = !empty && GridToggleBtn.IsChecked == true
            ? Visibility.Visible
            : Visibility.Collapsed;
    }

    private void GridToggle_Click(object sender, RoutedEventArgs e)
    {
        bool show = GridToggleBtn.IsChecked == true && _tabs.Count > 0;
        GuideOverlay.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        GuideOverlay.InvalidateVisual();
        _suppressGrid = true;
        GridShowCheck.IsChecked = show;
        _suppressGrid = false;
        StatusText.Text = show
            ? $"{GuideOverlay.Columns}-column guides and measurements shown."
            : "Guides hidden.";
    }

    private void GridColumns_Click(object sender, RoutedEventArgs e)
    {
        if (sender is MenuItem { Tag: string tag } && int.TryParse(tag, out int columns))
        {
            GuideOverlay.Columns = columns;
            GuideOverlay.InvalidateVisual();
            RefreshGridPanel();
            StatusText.Text = $"Grid set to {columns} columns.";
        }
    }

    private void WindowMinimize_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void WindowMaximizeRestore_Click(object sender, RoutedEventArgs e) =>
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

    private void TitleBar_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is not DependencyObject src) return;
        if (IsInsideVisual(src, TabStrip) || IsInsideVisual(src, NewTabBtn)) return;
        if (IsInsideButton(src)) return;
        if (e.ClickCount == 2)
        {
            WindowMaximizeRestore_Click(sender, e);
            e.Handled = true;
            return;
        }
        try { DragMove(); } catch { }
    }

    private static bool IsInsideButton(DependencyObject node)
    {
        var current = node;
        while (current != null)
        {
            if (current is Button) return true;
            current = VisualTreeHelper.GetParent(current);
        }
        return false;
    }

    private void WindowClose_Click(object sender, RoutedEventArgs e) => Close();

    private void UpdateMaxRestoreGlyph()
    {
        if (MaxRestoreGlyph == null) return;
        MaxRestoreGlyph.Text = WindowState == WindowState.Maximized ? "\uE923" : "\uE922";
    }

    // The custom chrome otherwise maximizes to the monitor (plus the hidden
    // resize frame), which pushes the top row off-screen. Clamp the maximized
    // window to the monitor work area so the title bar stays fully visible.
    private const int WM_GETMINMAXINFO = 0x0024;
    private const uint MONITOR_DEFAULTTONEAREST = 2;

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MINMAXINFO
    {
        public NativePoint ptReserved;
        public NativePoint ptMaxSize;
        public NativePoint ptMaxPosition;
        public NativePoint ptMinTrackSize;
        public NativePoint ptMaxTrackSize;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MONITORINFO
    {
        public int cbSize;
        public NativeRect rcMonitor;
        public NativeRect rcWork;
        public uint dwFlags;
    }

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint dwFlags);

    [DllImport("user32.dll")]
    private static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFO lpmi);

    private IntPtr WmGetMinMaxInfo(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg != WM_GETMINMAXINFO) return IntPtr.Zero;

        IntPtr monitor = MonitorFromWindow(hwnd, MONITOR_DEFAULTTONEAREST);
        if (monitor == IntPtr.Zero) return IntPtr.Zero;

        var info = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
        if (!GetMonitorInfo(monitor, ref info)) return IntPtr.Zero;

        var mmi = Marshal.PtrToStructure<MINMAXINFO>(lParam);
        mmi.ptMaxPosition.X = info.rcWork.Left - info.rcMonitor.Left;
        mmi.ptMaxPosition.Y = info.rcWork.Top - info.rcMonitor.Top;
        mmi.ptMaxSize.X = info.rcWork.Right - info.rcWork.Left;
        mmi.ptMaxSize.Y = info.rcWork.Bottom - info.rcWork.Top;
        Marshal.StructureToPtr(mmi, lParam, true);
        handled = true;
        return IntPtr.Zero;
    }

    /// <summary>Restore the last window size/position (and maximized state).</summary>
    private void ApplySavedWindowPlacement()
    {
        if (_settings.WindowLeft is not double left || _settings.WindowTop is not double top ||
            _settings.WindowWidth is not double width || _settings.WindowHeight is not double height)
            return;
        if (!double.IsFinite(left) || !double.IsFinite(top) ||
            !double.IsFinite(width) || !double.IsFinite(height))
            return;
        if (width < 400 || height < 300) return;

        var virtualScreen = new Rect(SystemParameters.VirtualScreenLeft, SystemParameters.VirtualScreenTop,
            SystemParameters.VirtualScreenWidth, SystemParameters.VirtualScreenHeight);
        var visible = Rect.Intersect(new Rect(left, top, width, height), virtualScreen);
        if (visible.IsEmpty || visible.Width < 120 || visible.Height < 60)
        {
            // The old monitor is gone or the window was mostly off-screen:
            // keep the size but let Windows center it.
            Width = width;
            Height = height;
            if (_settings.WindowMaximized) WindowState = WindowState.Maximized;
            return;
        }

        WindowStartupLocation = WindowStartupLocation.Manual;
        Left = left;
        Top = top;
        Width = width;
        Height = height;
        if (_settings.WindowMaximized) WindowState = WindowState.Maximized;
    }

    private void PersistWindowPlacement()
    {
        Rect bounds = WindowState == WindowState.Normal && !_rememberMaximized
            ? new Rect(Left, Top, Width, Height)
            : RestoreBounds;
        if (bounds.IsEmpty || bounds.Width < 1 || bounds.Height < 1)
            bounds = new Rect(Left, Top, Width, Height);

        var fresh = AppSettings.Load();
        if (bounds.Width >= 1 && bounds.Height >= 1 &&
            double.IsFinite(bounds.Left) && double.IsFinite(bounds.Top))
        {
            fresh.WindowLeft = bounds.Left;
            fresh.WindowTop = bounds.Top;
            fresh.WindowWidth = bounds.Width;
            fresh.WindowHeight = bounds.Height;
        }
        fresh.WindowMaximized = _rememberMaximized;
        fresh.Save();
    }

    /// <summary>Snapshot pre-mutation state for Ctrl+Z. Call BEFORE changing the document.</summary>
    private void PushUndo()
    {
        _undo.Capture(_doc);
        if (_activeTab >= 0 && _activeTab < _tabs.Count)
            _tabs[_activeTab].IsDirty = true;
    }

    /// <summary>Snapshot once per inspector focus session (called before applying the edit).</summary>
    private void ArmInspectorUndo()
    {
        if (!_inspectorArmed) return;
        _inspectorArmed = false;
        PushUndo();
    }

    // =================================================================
    // Canvas background (right-click): solid color or transparent
    // =================================================================

    /// <summary>Repaint the artboard paper: solid color, or the transparency checkerboard.</summary>
    private void UpdateArtboardFill()
    {
        PaintPaper(Artboard, _doc.CanvasTransparent, _doc.CanvasColor);
        CanvasTransparentMenuItem.IsChecked = _doc.CanvasTransparent;
    }

    /// <summary>Shared paper fill so the artboard and thumbnails stay consistent.</summary>
    private static void PaintPaper(System.Windows.Shapes.Rectangle paper, bool transparent, Color color)
    {
        if (transparent)
        {
            paper.Fill = CheckerBrush;
            return;
        }
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        paper.Fill = brush;
    }

    private static Brush BuildCheckerBrush()
    {
        var light = new SolidColorBrush(Color.FromRgb(0xFF, 0xFF, 0xFF));
        var dark = new SolidColorBrush(Color.FromRgb(0xCB, 0xCB, 0xCB));
        var group = new DrawingGroup();
        group.Children.Add(new GeometryDrawing(light, null, new RectangleGeometry(new Rect(0, 0, 8, 8))));
        group.Children.Add(new GeometryDrawing(light, null, new RectangleGeometry(new Rect(8, 8, 8, 8))));
        group.Children.Add(new GeometryDrawing(dark, null, new RectangleGeometry(new Rect(8, 0, 8, 8))));
        group.Children.Add(new GeometryDrawing(dark, null, new RectangleGeometry(new Rect(0, 8, 8, 8))));
        var brush = new DrawingBrush(group)
        {
            TileMode = TileMode.Tile,
            Viewport = new Rect(0, 0, 16, 16),
            ViewportUnits = BrushMappingMode.Absolute,
            Stretch = Stretch.None,
        };
        brush.Freeze();
        return brush;
    }

    private void CanvasBgColor_Click(object sender, RoutedEventArgs e) => PickCanvasColor();

    private void PickCanvasColor()
    {
        bool originalTransparent = _doc.CanvasTransparent;
        Color original = _doc.CanvasColor;

        PushUndo();
        var dlg = new ColorPickerDialog(original, CanvasColorsWithPrimary()) { Owner = this };
        dlg.PreviewColorChanged += c =>
        {
            _doc.CanvasTransparent = false;
            _doc.CanvasColor = c;
            UpdateArtboardFill();
        };

        if (dlg.ShowDialog() == true)
        {
            _doc.CanvasTransparent = false;
            _doc.CanvasColor = dlg.SelectedColor;
            UpdateArtboardFill();
            StatusText.Text = $"Canvas background set to {ToHex(_doc.CanvasColor)}.";
        }
        else
        {
            _doc.CanvasTransparent = originalTransparent;
            _doc.CanvasColor = original;
            UpdateArtboardFill();
        }
    }

    private List<Color> CanvasColorsWithPrimary()
    {
        var colors = ColorPickerDialog.CanvasColors(_doc);
        colors.RemoveAll(c => Math.Abs(c.R - _primaryColor.R) <= 12 &&
                              Math.Abs(c.G - _primaryColor.G) <= 12 &&
                              Math.Abs(c.B - _primaryColor.B) <= 12);
        colors.Insert(0, _primaryColor);
        return colors;
    }

    private void RefreshPrimaryColorSwatch()
    {
        if (PrimaryColorSwatch == null) return;
        PrimaryColorSwatch.Background = new SolidColorBrush(_primaryColor);
        PrimaryColorSwatch.ToolTip = $"Main color {ToHex(_primaryColor)} - click to open the color picker";
    }

    private void PrimaryColor_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new ColorPickerDialog(_primaryColor, CanvasColorsWithPrimary()) { Owner = this };
        if (dlg.ShowDialog() != true) return;
        SetPrimaryColor(dlg.SelectedColor);
    }

    private void PrimaryColorPick_Click(object sender, RoutedEventArgs e)
    {
        if (ColorPickerDialog.PickColorFromScreen(this) is { } color)
            SetPrimaryColor(color);
    }

    private void SetPrimaryColor(Color color)
    {
        _primaryColor = color;
        if (_activeTab >= 0 && _activeTab < _tabs.Count)
        {
            _tabs[_activeTab].PrimaryColor = color;
            _tabs[_activeTab].IsDirty = true;
        }
        RefreshPrimaryColorSwatch();
        StatusText.Text = $"Main color set to {ToHex(color)}.";
    }

    private void CanvasTransparent_Click(object sender, RoutedEventArgs e)
    {
        PushUndo();
        _doc.CanvasTransparent = CanvasTransparentMenuItem.IsChecked;
        StatusText.Text = _doc.CanvasTransparent
            ? "Canvas background is transparent - PNG export keeps no background."
            : $"Canvas background set to {ToHex(_doc.CanvasColor)}.";
    }

    private void CanvasBgSwatch_Click(object sender, RoutedEventArgs e) => PickCanvasColor();

    private void CanvasTransparent_Toggled(object sender, RoutedEventArgs e)
    {
        if (_suppressGrid) return;
        PushUndo();
        _doc.CanvasTransparent = CanvasTransparentCheck.IsChecked == true;
        CanvasTransparentMenuItem.IsChecked = _doc.CanvasTransparent;
        StatusText.Text = _doc.CanvasTransparent
            ? "Canvas background is transparent - PNG export keeps no background."
            : $"Canvas background set to {ToHex(_doc.CanvasColor)}.";
    }

    private void ProjectSettings_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new NewCanvasDialog((int)Math.Round(_doc.ArtboardWidth),
            (int)Math.Round(_doc.ArtboardHeight), projectSettings: true) { Owner = this };
        if (dlg.ShowDialog() != true) return;

        int w = dlg.CanvasWidth, h = dlg.CanvasHeight;
        if ((int)Math.Round(_doc.ArtboardWidth) == w && (int)Math.Round(_doc.ArtboardHeight) == h)
            return;

        PushUndo();
        _doc.ResizeCanvas(w, h);
        ZoomToFit();
        RefreshActiveTabMeta();
        StatusText.Text = $"Project resized to {w}×{h} px.";
    }

    /// <summary>The background used for export/copy: null when transparent, else the paper color.</summary>
    private Color? ExportBackground => _doc.CanvasTransparent ? (Color?)null : _doc.CanvasColor;

    // =================================================================
    // Selection
    // =================================================================

    private DesignElement? _watched;

    /// <summary>Guide the dragged object is currently glued to (NaN = free).</summary>
    private double _snapLockX = double.NaN;
    private double _snapLockY = double.NaN;
    private int _snapLockXFeature; // 0 = leading edge, 1 = middle, 2 = trailing edge
    private int _snapLockYFeature; // 0 = top, 1 = middle, 2 = bottom
    private SnapAxis _snapResizeX;
    private SnapAxis _snapResizeY;

    /// <summary>
    /// Rebuild the selection outline. While dragging, images get the cheap
    /// bounding outline (no pixel re-trace per mousemove); the exact contour
    /// is restored on mouse-up. Shapes are analytic and always exact.
    /// </summary>
    private void RefreshContour()
    {
        if (_doc.SelectionCount > 1)
        {
            SelectionAdorner.Contour = null;
            return;
        }
        try
        {
            var sel = _doc.Selected;
            SelectionAdorner.Contour = sel switch
            {
                null => null,
                ImageElement when _drag != DragMode.None => ContourService.Box(sel),
                _ => ContourService.BuildContour(sel),
            };
        }
        catch
        {
            SelectionAdorner.Contour = null;
        }
    }

    private void OnSelectionChanged()
    {
        bool selectionChanged = !ReferenceEquals(_watched, _doc.Selected);
        if (_watched != null) _watched.PropertyChanged -= Watched_PropertyChanged;
        _watched = _doc.Selected;
        if (_watched != null) _watched.PropertyChanged += Watched_PropertyChanged;
        if (_doc.SelectionCount == 1 && _doc.Selected is ShapeElement selectedShape)
            CaptureShapeDefaults(selectedShape);
        else if (_doc.SelectionCount == 1 && _doc.Selected is LineElement selectedLine)
            CaptureLineDefaults(selectedLine, captureMode: selectionChanged);
        else if (_doc.SelectionCount == 1 && _doc.Selected is TextElement selectedText)
            CaptureTextDefaults(selectedText);

        SelectionAdorner.Targets = _doc.SelectedElements;
        SelectionAdorner.Target = _doc.SelectionCount == 1 ? _doc.Selected : null;
        RefreshContour();
        SelectionAdorner.InvalidateVisual();
        GuideOverlay.InvalidateVisual();
        var sel = _doc.Selected;
        SelectionText.Text = _doc.SelectionCount switch
        {
            1 when sel != null => $"{sel.GetType().Name}  {sel.Width:F0}×{sel.Height:F0}  @({sel.X:F0},{sel.Y:F0})  ∠{sel.RotationAngle:F0}°  Z={sel.ZIndex}",
            0 => "Nothing selected",
            _ => $"{_doc.SelectionCount} selected",
        };
        RefreshInspector();
        if (_tool == ActiveTool.Text) RefreshTextToolbar();
        if (_tool == ActiveTool.Pick) UpdatePickToolbarState();
        if (_tool == ActiveTool.Rectangle) RefreshShapeToolbar();
        if (_tool == ActiveTool.Line) RefreshLineToolbar();
    }

    private void Watched_PropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        SelectionAdorner.InvalidateVisual();
        GuideOverlay.InvalidateVisual();
        if (sender is ShapeElement shape && IsShapeStyleProperty(e.PropertyName))
        {
            CaptureShapeDefaults(shape);
            if (_tool == ActiveTool.Rectangle) RefreshShapeToolbar();
        }
        if (sender is LineElement line && IsLineStyleProperty(e.PropertyName))
        {
            CaptureLineDefaults(line);
            if (_tool == ActiveTool.Line) RefreshLineToolbar();
        }
        if (sender is TextElement text && IsTextStyleProperty(e.PropertyName))
        {
            CaptureTextDefaults(text);
            if (_tool == ActiveTool.Text) RefreshTextToolbar();
        }
        if (e.PropertyName is nameof(DesignElement.X) or nameof(DesignElement.Y)
            or nameof(DesignElement.Width) or nameof(DesignElement.Height)
            or nameof(DesignElement.RotationAngle) or nameof(DesignElement.ZIndex)
            or nameof(ImageElement.Source)
            or nameof(ImageElement.FitMode)
            or nameof(ShapeElement.FillColor1) or nameof(ShapeElement.FillColor2)
            or nameof(ShapeElement.IsGradient) or nameof(ShapeElement.GradientAngle)
            or nameof(ShapeElement.StrokeColor) or nameof(ShapeElement.StrokeThickness)
            or nameof(ShapeElement.CornerRadius)
            or nameof(LineElement.StrokeColor) or nameof(LineElement.StrokeThickness)
            or nameof(LineElement.Pattern) or nameof(LineElement.StartCap)
            or nameof(LineElement.EndCap) or nameof(LineElement.CurveStrength))
        {
            OnSelectionChanged();
        }
    }

    // =================================================================
    // Mouse: Pick (select / move / resize / rotate) + Slice rectangle
    // =================================================================

    private void Pasteboard_ContextMenuOpening(object sender, ContextMenuEventArgs e)
    {
        var hit = PickTopmost(Mouse.GetPosition(Pasteboard));
        if (hit != null && !_doc.SelectedElements.Contains(hit)) _doc.Select(hit);

        // Actions that do not apply right now are removed from the menu
        // instead of showing up as dead entries.
        bool onObject = hit != null;
        bool hasSelection = _doc.SelectionCount > 0;
        var fillShape = _doc.Selected as ShapeElement;

        SaveElementTransparentMenuItem.Visibility = onObject ? Visibility.Visible : Visibility.Collapsed;
        MergeMenuItem.Visibility = _doc.SelectionCount >= 2 ? Visibility.Visible : Visibility.Collapsed;
        CopyStyleMenuItem.Visibility = hasSelection ? Visibility.Visible : Visibility.Collapsed;
        PasteStyleMenuItem.Visibility = hasSelection ? Visibility.Visible : Visibility.Collapsed;
        PasteStyleMenuItem.IsEnabled = _styleClipboard != null;

        ReplaceBackgroundMenuItem.Visibility = fillShape != null ? Visibility.Visible : Visibility.Collapsed;
        if (fillShape != null)
        {
            RebuildProjectImageMenu();
            BackgroundFromProjectMenuItem.IsEnabled =
                BackgroundFromProjectMenuItem.Items[0] is MenuItem first && first.IsEnabled;
            ReplaceBackgroundFromFileMenuItem.IsEnabled = true;
            RemoveBackgroundImageMenuItem.IsEnabled = fillShape.FillImage != null;
        }

        // The separator only makes sense when object actions are listed.
        ElementMenuSeparator.Visibility = onObject || hasSelection
            ? Visibility.Visible : Visibility.Collapsed;

        CanvasTransparentMenuItem.IsChecked = _doc.CanvasTransparent;
    }

    private DesignElement? _styleClipboard;

    private void CopyStyle_Click(object sender, RoutedEventArgs e)
    {
        if (_doc.Selected is not { } source) return;
        _styleClipboard = source.Clone();
        StatusText.Text = "Style copied - right-click another object and choose Paste style.";
    }

    private void PasteStyle_Click(object sender, RoutedEventArgs e)
    {
        if (_styleClipboard == null || _doc.Selected is not { } target) return;
        PushUndo();
        _styleClipboard.CopyStyleTo(target);
        RefreshInspector();
        RefreshShapeToolbar();
        RefreshTextToolbar();
        RefreshPhotoPanel();
        StatusText.Text = "Style pasted.";
    }

    private void Merge_Click(object sender, RoutedEventArgs e)
    {
        var selected = _doc.SelectedElements.ToList();
        if (selected.Count < 2) return;

        PushUndo();

        double minX = selected.Min(el => el.X);
        double minY = selected.Min(el => el.Y);
        double maxX = selected.Max(el => el.X + el.Width);
        double maxY = selected.Max(el => el.Y + el.Height);

        var group = new GroupElement
        {
            X = minX,
            Y = minY,
            Width = maxX - minX,
            Height = maxY - minY,
        };
        group.CaptureOriginalSize();

        foreach (var el in selected)
        {
            el.X -= minX;
            el.Y -= minY;
            group.Children.Add(el);
        }

        foreach (var el in selected)
            _doc.Elements.Remove(el);

        _doc.Add(group);
        StatusText.Text = $"Merged {selected.Count} objects into a group.";
    }

    private void RebuildProjectImageMenu()
    {
        BackgroundFromProjectMenuItem.Items.Clear();
        var images = _doc.Elements.OfType<ImageElement>()
            .Where(im => im.Source is BitmapSource)
            .OrderBy(im => im.ZIndex)
            .ToList();
        if (images.Count == 0)
        {
            BackgroundFromProjectMenuItem.Items.Add(new MenuItem
            {
                Header = "No images in the project",
                IsEnabled = false,
            });
            return;
        }

        int index = 1;
        foreach (var image in images)
        {
            var bmp = (BitmapSource)image.Source!;
            string name = string.IsNullOrWhiteSpace(image.SourcePath)
                ? $"Image {index}"
                : System.IO.Path.GetFileNameWithoutExtension(image.SourcePath);
            var item = new MenuItem
            {
                Header = $"{name} ({bmp.PixelWidth} \u00D7 {bmp.PixelHeight})",
                Tag = image,
            };
            item.Click += ProjectImageFill_Click;
            BackgroundFromProjectMenuItem.Items.Add(item);
            index++;
        }
    }

    private void ProjectImageFill_Click(object sender, RoutedEventArgs e)
    {
        if (_doc.Selected is not ShapeElement shape) return;
        if (sender is not MenuItem { Tag: ImageElement source }) return;
        SetShapeFillImage(shape, source.DisplaySource as BitmapSource ?? source.Source as BitmapSource);
    }

    private void ReplaceBackgroundFromFile_Click(object sender, RoutedEventArgs e)
    {
        if (_doc.Selected is not ShapeElement shape) return;

        var dlg = new OpenFileDialog
        {
            Filter = "Images|*.png;*.jpg;*.jpeg;*.bmp;*.gif;*.webp;*.tif;*.tiff|All files|*.*",
        };
        if (dlg.ShowDialog(this) != true) return;

        try
        {
            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.UriSource = new Uri(dlg.FileName, UriKind.Absolute);
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            bmp.EndInit();
            bmp.Freeze();
            SetShapeFillImage(shape, bmp);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, "Could not load that image:\n" + ex.Message, "YorteImpact",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void RemoveBackgroundImage_Click(object sender, RoutedEventArgs e)
    {
        if (_doc.Selected is not ShapeElement shape || shape.FillImage == null) return;
        PushUndo();
        shape.FillImage = null;
        RefreshInspector();
        StatusText.Text = "Shape background image removed.";
    }

    private void SetShapeFillImage(ShapeElement shape, BitmapSource? bmp)
    {
        if (bmp == null) return;
        PushUndo();
        shape.FillImage = bmp;
        RefreshInspector();
        StatusText.Text = "Shape background replaced with an image.";
    }

    private void SaveElementTransparent_Click(object sender, RoutedEventArgs e) =>
        SaveSelectedElementAsImage();

    private void SaveSelectedElementAsImage()
    {
        if (_doc.Selected is not { } el) return;

        var dlg = new SaveFileDialog
        {
            Filter = "PNG image (*.png)|*.png|WebP image (*.webp)|*.webp",
            DefaultExt = "png",
            AddExtension = true,
            FileName = SuggestedElementFileName(el),
        };
        if (dlg.ShowDialog(this) != true) return;

        try
        {
            var bmp = SliceExportService.RenderElement(el, null);
            SliceExportService.SaveBitmap(bmp, dlg.FileName);
            StatusText.Text = $"Saved {el.GetType().Name.Replace("Element", "").ToLowerInvariant()} to {dlg.FileName}.";
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, "Could not save the object:\n" + ex.Message, "YorteImpact",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private string SuggestedElementFileName(DesignElement el)
    {
        string doc = string.IsNullOrWhiteSpace(_doc.DocumentName) ? "object" : _doc.DocumentName;
        string suffix = el switch
        {
            ImageElement im when !string.IsNullOrWhiteSpace(im.SourcePath) =>
                Path.GetFileNameWithoutExtension(im.SourcePath) is { Length: > 0 } n ? n : "image",
            TextElement => "text",
            ShapeElement => "shape",
            _ => "object",
        };
        string name = $"{doc}-{suffix}";
        foreach (char invalid in Path.GetInvalidFileNameChars())
            name = name.Replace(invalid, '-');
        return name;
    }

    private void Pasteboard_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (_textEditor != null && e.OriginalSource is DependencyObject src
            && IsInsideVisual(src, _textEditor))
            return; // clicks inside the in-place editor belong to the TextBox

        Viewport.Focus(); // keep Ctrl+V / Delete shortcuts working
        var pos = e.GetPosition(Pasteboard);

        if (_tool == ActiveTool.Paintbrush && e.ChangedButton == MouseButton.Left)
        {
            BeginBrushStroke(pos, e);
            e.Handled = true;
            return;
        }

        // Background remover: erase the connected color under the cursor on an image.
        if (_tool == ActiveTool.Erase && e.ChangedButton == MouseButton.Left)
        {
            EraseAt(pos);
            e.Handled = true;
            return;
        }

        if (_tool == ActiveTool.Slice && e.ChangedButton == MouseButton.Left)
        {
            if (_sliceToolMode == SliceToolMode.Bezier)
            {
                AddBezierSlicePoint(pos, e.ClickCount);
                e.Handled = true;
                return;
            }
            _drag = DragMode.Slice;
            _dragStartCanvas = pos;
            _sliceVisible = true; // a fresh drag always shows its marker
            _pendingSlice = new Rect(pos, new Size(0, 0));
            RefreshSliceLayer();
            Pasteboard.CaptureMouse();
            e.Handled = true;
            return;
        }

        if (_tool == ActiveTool.Line && e.ChangedButton == MouseButton.Left)
        {
            if (_lineMode == LineToolMode.Polyline) AddPolylinePoint(pos, e.ClickCount);
            else BeginLineDraw(pos);
            e.Handled = true;
            return;
        }

        if (_tool == ActiveTool.Rectangle && e.ChangedButton == MouseButton.Left)
        {
            if (ParseEnum(_settings.Shape.ShapeKind, ShapeKind.Rectangle) == ShapeKind.Polygon)
            {
                if (e.ClickCount >= 2) FinishPolygonDraft();
                else
                {
                    _polygonDraft.Add(pos);
                    UpdatePolygonDraftPreview(pos);
                    StatusText.Text = _polygonDraft.Count < 3
                        ? "Polygon: click at least 3 points; Enter or double-click finishes."
                        : "Polygon: click to add points; Enter or double-click finishes, Esc cancels.";
                }
                e.Handled = true;
                return;
            }

            _drag = DragMode.Draw;
            _drawStart = pos;
            PositionDrawVisual(new Rect(pos, new Size(0, 0)));
            Pasteboard.CaptureMouse();
            e.Handled = true;
            return;
        }

        if (_tool == ActiveTool.Text && e.ChangedButton == MouseButton.Left)
        {
            if (_editingText != null)
            {
                CommitTextEdit(); // click outside finishes the text; don't spawn another one
                e.Handled = true;
                return;
            }
            if (PickTopmost(pos) is TextElement hitText)
            {
                // A click opens the editor; dragging the frame moves the text.
                _textPressTarget = hitText;
                _dragStartCanvas = pos;
                _drag = DragMode.TextPress;
                Pasteboard.CaptureMouse();
            }
            else CreateTextAt(pos);
            e.Handled = true;
            return;
        }

        if (e.ChangedButton != MouseButton.Left) return;

        // Pick: double-click a text object to edit it in place.
        if (_tool == ActiveTool.Pick && e.ClickCount == 2 &&
            !IsAdditiveSelection && (Keyboard.Modifiers & ModifierKeys.Alt) == 0 &&
            PickTopmost(pos) is TextElement dbl)
        {
            SetTool(ActiveTool.Text);
            BeginTextEdit(dbl);
            e.Handled = true;
            return;
        }

        // 1. Handles first (single selection only).
        if (_doc.SelectionCount == 1 && _doc.Selected != null)
        {
            var hit = SelectionAdorner.HitTestHandle(pos);
            if (hit is HandleKind.Rotate)
            {
                BeginRotate(_doc.Selected, pos);
                e.Handled = true;
                return;
            }
            if (hit is not (HandleKind.None or HandleKind.Move))
            {
                BeginResize(_doc.Selected, pos, hit);
                e.Handled = true;
                return;
            }
        }

        // 2. Rotation-aware visual pick (topmost element under cursor).
        var picked = PickTopmost(pos);
        if (picked != null)
        {
            bool ctrlHeld = (Keyboard.Modifiers & ModifierKeys.Control) != 0;
            bool altHeld = (Keyboard.Modifiers & ModifierKeys.Alt) != 0;
            if (ctrlHeld && !altHeld)
            {
                _ctrlGesturePending = true;
                _ctrlGestureElement = picked;
                _ctrlGestureStart = pos;
                _ctrlGestureWasSelected = _doc.SelectedElements.Contains(picked);
                Pasteboard.CaptureMouse();
                e.Handled = true;
                return;
            }
        }

        if (picked != null && IsAdditiveSelection)
        {
            bool alreadySelected = _doc.SelectedElements.Contains(picked);
            _doc.AddToSelection(picked);
            // Shift-drag also moves (and snaps) an object that the same click
            // just added; Ctrl stays pure additive selection.
            if (alreadySelected)
            {
                BeginMove(picked, pos);
            }
            else if ((Keyboard.Modifiers & ModifierKeys.Shift) != 0)
            {
                // Defer the undo snapshot until the pointer actually moves, so a
                // plain Shift+click does not add an empty undo step.
                BeginMove(picked, pos, snapshot: false);
                _moveUndoPending = true;
            }
            e.Handled = true;
            return;
        }

        if (picked != null && (Keyboard.Modifiers & ModifierKeys.Alt) != 0)
        {
            if (!_doc.SelectedElements.Contains(picked)) _doc.Select(picked);
            PushUndo();
            var copies = _doc.DuplicateSelectedElements();
            if (copies.Count > 0) BeginMove(copies[0], pos, snapshot: false);
            StatusText.Text = "Duplicated - drag to place (Shift = snap).";
            e.Handled = true;
            return;
        }

        if (picked != null)
        {
            if ((Keyboard.Modifiers & ModifierKeys.Control) != 0)
            {
                // Ctrl+drag duplicates the element and drags the copy.
                PushUndo();
                var copy = picked.Clone();
                _doc.Add(copy);
                BeginMove(copy, pos, snapshot: false);
                StatusText.Text = "Duplicated — drag to place (Shift = snap).";
            }
            else
            {
                if (!_doc.SelectedElements.Contains(picked)) _doc.Select(picked);
                BeginMove(picked, pos);
            }
            e.Handled = true;
            return;
        }

        // 3. Empty pasteboard: drag a marquee to select several objects.
        BeginMarquee(pos);
    }

    private static bool IsAdditiveSelection =>
        (Keyboard.Modifiers & (ModifierKeys.Control | ModifierKeys.Shift)) != 0;

    private void CancelCtrlGesture()
    {
        if (!_ctrlGesturePending) return;
        _ctrlGesturePending = false;
        _ctrlGestureElement = null;
        if (Pasteboard.IsMouseCaptured) Pasteboard.ReleaseMouseCapture();
    }

    private void BeginMarquee(Point pos)
    {
        _marqueeAdditive = IsAdditiveSelection;
        _drag = DragMode.Marquee;
        _dragStartCanvas = pos;
        Pasteboard.CaptureMouse();
        if (!_marqueeAdditive) _doc.Select(null);
        UpdateMarqueeVisual(pos);
        Cursor = Cursors.Cross;
    }

    private void UpdateMarqueeVisual(Point pos)
    {
        var rect = NormalizedRect(_dragStartCanvas, pos);
        Canvas.SetLeft(MarqueeVisual, rect.X);
        Canvas.SetTop(MarqueeVisual, rect.Y);
        MarqueeVisual.Width = Math.Max(0, rect.Width);
        MarqueeVisual.Height = Math.Max(0, rect.Height);
        MarqueeVisual.Visibility = Visibility.Visible;
    }

    private void FinishMarquee(Point pos)
    {
        var rect = NormalizedRect(_dragStartCanvas, pos);
        MarqueeVisual.Visibility = Visibility.Collapsed;
        _drag = DragMode.None;
        if (Pasteboard.IsMouseCaptured) Pasteboard.ReleaseMouseCapture();
        Cursor = Cursors.Arrow;

        if (rect.Width < 5 || rect.Height < 5)
        {
            if (!_marqueeAdditive) _doc.Select(null);
        }
        else
        {
            var hits = _doc.Elements
                .Where(e => !e.IsBackgroundLayer && rect.Contains(e.Bounds))
                .ToList();
            if (_marqueeAdditive)
            {
                foreach (var el in hits) _doc.AddToSelection(el);
            }
            else
            {
                _doc.SelectRange(hits);
            }
        }

        StatusText.Text = $"{_doc.SelectionCount} objects selected.";
    }

    private void Pasteboard_MouseMove(object sender, MouseEventArgs e)
    {
        var pos = e.GetPosition(Pasteboard);

        if (_tool == ActiveTool.Slice && _sliceToolMode == SliceToolMode.Bezier && _sliceBezierDraft.Count > 0)
        {
            _sliceBezierCursor = pos;
            RefreshSliceLayer();
        }

        if (_ctrlGesturePending && _ctrlGestureElement is { } pending)
        {
            double threshold = 3 / Math.Max(0.25, _zoom);
            if (Math.Abs(pos.X - _ctrlGestureStart.X) >= threshold ||
                Math.Abs(pos.Y - _ctrlGestureStart.Y) >= threshold)
            {
                _ctrlGesturePending = false;
                _ctrlGestureElement = null;

                if (!_ctrlGestureWasSelected) _doc.Select(pending);
                PushUndo();
                var copies = _doc.DuplicateSelectedElements();
                if (copies.Count > 0)
                {
                    BeginMove(copies[0], _ctrlGestureStart, snapshot: false);
                    DoMove(copies[0], pos);
                }
                StatusText.Text = "Duplicated - drag to place (Shift = snap).";
                e.Handled = true;
                return;
            }

            e.Handled = true;
            return;
        }

        if (_drag == DragMode.TextPress && _textPressTarget is { } pressText)
        {
            double threshold = 3 / Math.Max(0.25, _zoom);
            if (Math.Abs(pos.X - _dragStartCanvas.X) >= threshold ||
                Math.Abs(pos.Y - _dragStartCanvas.Y) >= threshold)
            {
                _textPressTarget = null;
                BeginMove(pressText, _dragStartCanvas);
                DoMove(pressText, pos);
                StatusText.Text = "Text moved - release to place (Shift = snap).";
            }
            e.Handled = true;
            return;
        }

        if (_drag == DragMode.Marquee)
        {
            UpdateMarqueeVisual(pos);
            e.Handled = true;
            return;
        }

        if (_drag == DragMode.Paint)
        {
            ContinueBrushStroke(pos, e);
            return;
        }

        if (_drag == DragMode.DrawLine)
        {
            if (_lineMode == LineToolMode.Freehand && _lineDraft.Count > 0 &&
                (pos - _lineDraft[^1]).Length >= Math.Max(1, 2 / Math.Max(0.25, _zoom)))
                _lineDraft.Add(pos);
            UpdateLinePreview(pos);
            e.Handled = true;
            return;
        }

        if (_tool == ActiveTool.Line && _lineMode == LineToolMode.Polyline
            && _lineDraft.Count > 0 && _drag == DragMode.None)
            UpdateLinePreview(pos);

        if (_polygonDraft.Count > 0) UpdatePolygonDraftPreview(pos);

        if (_drag == DragMode.None && e.LeftButton == MouseButtonState.Released)
        {
            if (_tool == ActiveTool.Paintbrush)
                UpdateBrushCursor(pos);

            if (_tool is ActiveTool.Pick or ActiveTool.Elements)
            {
                Cursor = _doc.Selected != null
                    ? SelectionAdorner.HitTestHandle(pos) switch
                    {
                        HandleKind.Rotate => Cursors.Hand,
                        HandleKind.Move => Cursors.SizeAll,
                        HandleKind.N or HandleKind.S => Cursors.SizeNS,
                        HandleKind.E or HandleKind.W => Cursors.SizeWE,
                        HandleKind.NW or HandleKind.SE => Cursors.SizeNWSE,
                        HandleKind.NE or HandleKind.SW => Cursors.SizeNESW,
                        _ => Cursors.Arrow,
                    }
                    : Cursors.Arrow;
            }
        }

        if (_drag == DragMode.None || (_doc.Selected == null && _drag is not (DragMode.Slice or DragMode.Draw or DragMode.DrawLine)))
            return;

        switch (_drag)
        {
            case DragMode.Move: DoMove((DesignElement)_doc.Selected!, pos); break;
            case DragMode.Resize: DoResize((DesignElement)_doc.Selected!, pos); break;
            case DragMode.Rotate: DoRotate((DesignElement)_doc.Selected!, pos); break;
            case DragMode.Slice: DoSlice(pos); break;
            case DragMode.Draw: UpdateDrawVisual(pos); break;
        }
    }

    private void Pasteboard_MouseLeave(object sender, MouseEventArgs e)
    {
        if (_drag != DragMode.Paint)
            BrushCursor.Visibility = Visibility.Collapsed;
        if (_drag is DragMode.Draw or DragMode.DrawLine or DragMode.Resize || _lineDraft.Count > 0)
            HideDrawSizeTip();
    }

    private void Pasteboard_MouseUp(object sender, MouseButtonEventArgs e)
    {
        if (_ctrlGesturePending)
        {
            var element = _ctrlGestureElement;
            _ctrlGesturePending = false;
            _ctrlGestureElement = null;
            if (element != null) _doc.AddToSelection(element);
            if (Pasteboard.IsMouseCaptured) Pasteboard.ReleaseMouseCapture();
            e.Handled = true;
            return;
        }

        if (_drag == DragMode.TextPress)
        {
            var target = _textPressTarget;
            _textPressTarget = null;
            _drag = DragMode.None;
            if (Pasteboard.IsMouseCaptured) Pasteboard.ReleaseMouseCapture();
            if (target != null) BeginTextEdit(target);
            e.Handled = true;
            return;
        }

        if (_drag == DragMode.Marquee)
        {
            FinishMarquee(e.GetPosition(Pasteboard));
            e.Handled = true;
            return;
        }

        if (_drag == DragMode.Paint)
        {
            EndBrushStroke();
            e.Handled = true;
            return;
        }

        if (_drag == DragMode.Slice)
        {
            var rect = NormalizedRect(_dragStartCanvas, e.GetPosition(Pasteboard));
            _pendingSlice = null;
            if (rect.Width >= 5 && rect.Height >= 5)
            {
                // Replace by default; only the "+Add slice" toggle accumulates.
                if (!_sliceAddMode) _slices.Clear();
                var region = new SliceRegion { Rect = rect, Name = $"slice{_slices.Count + 1}" };
                _slices.Add(region);
                _activeSlice = region;
                StatusText.Text = _sliceAddMode
                    ? $"Slice {rect.Width:F0}×{rect.Height:F0} added ({_slices.Count} total) — Export / Ctrl+C copy / Delete-cut / Ctrl+R crop."
                    : $"Slice {rect.Width:F0}×{rect.Height:F0} — Export / Ctrl+C copy / Delete-cut / Ctrl+R crop. Turn on +Add slice to keep several.";
            }
            else
            {
                // Plain click: activate the topmost region under the cursor, if any.
                var pos = e.GetPosition(Pasteboard);
                var hit = _slices.LastOrDefault(r => r.SelectionGeometry?.FillContains(pos) ?? r.Rect.Contains(pos));
                if (hit != null)
                {
                    _activeSlice = hit;
                    StatusText.Text = $"Active slice {hit.Rect.Width:F0}×{hit.Rect.Height:F0} @({hit.Rect.X:F0},{hit.Rect.Y:F0}).";
                }
            }
            RefreshSliceLayer();
        }
        if (_drag == DragMode.Draw)
        {
            FinishDraw(e.GetPosition(Pasteboard));
        }
        if (_drag == DragMode.DrawLine)
        {
            FinishLineDraw(e.GetPosition(Pasteboard));
        }
        HideDrawSizeTip();
        _drag = DragMode.None;
        _activeHandle = HandleKind.None;
        _moveUndoPending = false;
        ResetSnapLock();
        HideSnapGuides();
        if (Pasteboard.IsMouseCaptured) Pasteboard.ReleaseMouseCapture();
        if (_doc.Selected != null) RefreshContour(); // exact ants back after the drag
    }

    // ----- drag primitives -----

    private void BeginMove(DesignElement el, Point pos, bool snapshot = true)
    {
        if (snapshot) PushUndo(); // one undo step per gesture, not per mousemove
        _moveUndoPending = false;
        if (!_doc.SelectedElements.Contains(el)) _doc.Select(el);
        _moveOrigins.Clear();
        foreach (var selected in _doc.SelectedElements)
            _moveOrigins[selected] = new Point(selected.X, selected.Y);

        _drag = DragMode.Move;
        _dragStartCanvas = pos;
        _origX = el.X; _origY = el.Y;
        ResetSnapLock();
        Pasteboard.CaptureMouse();
        Cursor = Cursors.SizeAll;
    }

    private void DoMove(DesignElement el, Point pos)
    {
        if (!_moveOrigins.TryGetValue(el, out var start))
            start = new Point(_origX, _origY);

        double x = start.X + (pos.X - _dragStartCanvas.X);
        double y = start.Y + (pos.Y - _dragStartCanvas.Y);

        if ((Keyboard.Modifiers & ModifierKeys.Shift) != 0)
        {
            (x, y) = ApplySnap(el, x, y);
        }
        else
        {
            ResetSnapLock();
            HideSnapGuides();
        }

        double dx = x - start.X;
        double dy = y - start.Y;
        if (_moveUndoPending && (dx != 0 || dy != 0))
        {
            PushUndo();
            _moveUndoPending = false;
        }
        foreach (var (element, origin) in _moveOrigins)
        {
            element.X = origin.X + dx;
            element.Y = origin.Y + dy;
        }
    }

    /// <summary>
    /// Shift-drag snapping: canvas edges/centers, other elements' edges/centers
    /// and repeated gaps between elements (grid-like spacing).
    /// A guide grabs the object inside <see cref="SnapCapturePx"/> and keeps it
    /// until the pointer leaves <see cref="SnapReleasePx"/> — the wider release
    /// radius is what makes the snap feel firm instead of flickering.
    /// </summary>
    private (double X, double Y) ApplySnap(DesignElement moving, double x, double y)
    {
        double capture = SnapCapturePx / Math.Max(0.25, _zoom);
        double release = SnapReleasePx / Math.Max(0.25, _zoom);

        double bestX = double.NaN, guideX = 0;
        int featureX = 0;
        double bestY = double.NaN, guideY = 0;
        int featureY = 0;

        // 0 = leading edge/center, 1 = middle, 2 = trailing edge/bottom.
        double FeatureX(int f) => f == 0 ? x : f == 1 ? x + moving.Width / 2 : x + moving.Width;
        double FeatureY(int f) => f == 0 ? y : f == 1 ? y + moving.Height / 2 : y + moving.Height;

        void ConsiderX(double target, int feature)
        {
            double delta = target - FeatureX(feature);
            if (Math.Abs(delta) <= capture &&
                (double.IsNaN(bestX) || Math.Abs(delta) < Math.Abs(bestX)))
            {
                bestX = delta;
                guideX = target;
                featureX = feature;
            }
        }

        void ConsiderY(double target, int feature)
        {
            double delta = target - FeatureY(feature);
            if (Math.Abs(delta) <= capture &&
                (double.IsNaN(bestY) || Math.Abs(delta) < Math.Abs(bestY)))
            {
                bestY = delta;
                guideY = target;
                featureY = feature;
            }
        }

        // An active lock wins over any closer guide: the object stays glued
        // to the line it caught until it is dragged clearly away from it.
        double? lockX = null, lockY = null;
        if (!double.IsNaN(_snapLockX))
        {
            double delta = _snapLockX - FeatureX(_snapLockXFeature);
            if (Math.Abs(delta) <= release)
            {
                lockX = delta;
                bestX = delta;
                guideX = _snapLockX;
                featureX = _snapLockXFeature;
            }
        }
        if (!double.IsNaN(_snapLockY))
        {
            double delta = _snapLockY - FeatureY(_snapLockYFeature);
            if (Math.Abs(delta) <= release)
            {
                lockY = delta;
                bestY = delta;
                guideY = _snapLockY;
                featureY = _snapLockYFeature;
            }
        }

        {
            var art = _doc.ArtboardRect;
            ConsiderX(art.Left, 0);
            ConsiderX(art.Left + art.Width / 2, 1);
            ConsiderX(art.Right, 2);
            ConsiderY(art.Top, 0);
            ConsiderY(art.Top + art.Height / 2, 1);
            ConsiderY(art.Bottom, 2);

            if (GuideOverlay.Visibility == Visibility.Visible)
            {
                foreach (double guide in GuideOverlay.GetVerticalLines())
                    ConsiderX(guide, 0);
                foreach (double guide in GuideOverlay.GetHorizontalLines())
                    ConsiderY(guide, 0);
            }

            var others = _doc.Elements
                .Where(e => !ReferenceEquals(e, moving) && !e.IsBackgroundLayer
                            && !_moveOrigins.ContainsKey(e))
                .ToList();
            foreach (var other in others)
            {
                ConsiderX(other.X, 0);
                ConsiderX(other.X + other.Width / 2, 1);
                ConsiderX(other.X + other.Width, 2);
                ConsiderY(other.Y, 0);
                ConsiderY(other.Y + other.Height / 2, 1);
                ConsiderY(other.Y + other.Height, 2);
            }

            // Existing gaps repeated to the side create grid-like snap lines.
            var byX = others.OrderBy(e => e.X).ToList();
            for (int i = 0; i < byX.Count; i++)
            {
                for (int j = i + 1; j < byX.Count; j++)
                {
                    var a = byX[i];
                    var b = byX[j];
                    if (a.Y >= b.Y + b.Height || b.Y >= a.Y + a.Height) continue; // no vertical overlap
                    double gap = b.X - (a.X + a.Width);
                    if (gap <= 0.5) continue;
                    ConsiderX(a.X + a.Width + gap, 0);
                    ConsiderX(b.X - gap, 0);
                }
            }

            var byY = others.OrderBy(e => e.Y).ToList();
            for (int i = 0; i < byY.Count; i++)
            {
                for (int j = i + 1; j < byY.Count; j++)
                {
                    var a = byY[i];
                    var b = byY[j];
                    if (a.X >= b.X + b.Width || b.X >= a.X + a.Width) continue; // no horizontal overlap
                    double gap = b.Y - (a.Y + a.Height);
                    if (gap <= 0.5) continue;
                    ConsiderY(a.Y + a.Height + gap, 0);
                    ConsiderY(b.Y - gap, 0);
                }
            }
        }

        // The lock overrides the search result for its axis.
        if (lockX is double lx)
        {
            bestX = lx;
            guideX = _snapLockX;
            featureX = _snapLockXFeature;
        }
        if (lockY is double ly)
        {
            bestY = ly;
            guideY = _snapLockY;
            featureY = _snapLockYFeature;
        }

        if (!double.IsNaN(bestX))
        {
            x += bestX;
            ShowSnapX(guideX);
        }
        else
        {
            SnapGuideV.Visibility = Visibility.Collapsed;
        }

        if (!double.IsNaN(bestY))
        {
            y += bestY;
            ShowSnapY(guideY);
        }
        else
        {
            SnapGuideH.Visibility = Visibility.Collapsed;
        }

        _snapLockX = double.IsNaN(bestX) ? double.NaN : guideX;
        _snapLockY = double.IsNaN(bestY) ? double.NaN : guideY;
        _snapLockXFeature = featureX;
        _snapLockYFeature = featureY;

        return (x, y);
    }

    /// <summary>Forget the active snap locks (drag ended or the modifier was released).</summary>
    private void ResetSnapLock()
    {
        _snapLockX = double.NaN;
        _snapLockY = double.NaN;
        _snapResizeX.Reset();
        _snapResizeY.Reset();
    }

    private void ShowSnapX(double x)
    {
        var art = _doc.ArtboardRect;
        SnapGuideV.X1 = SnapGuideV.X2 = x;
        SnapGuideV.Y1 = art.Top;
        SnapGuideV.Y2 = art.Bottom;
        SnapGuideV.Visibility = Visibility.Visible;
    }

    private void ShowSnapY(double y)
    {
        var art = _doc.ArtboardRect;
        SnapGuideH.X1 = art.Left;
        SnapGuideH.X2 = art.Right;
        SnapGuideH.Y1 = SnapGuideH.Y2 = y;
        SnapGuideH.Visibility = Visibility.Visible;
    }

    private void HideSnapGuides()
    {
        SnapGuideV.Visibility = Visibility.Collapsed;
        SnapGuideH.Visibility = Visibility.Collapsed;
    }

    private void BeginResize(DesignElement el, Point pos, HandleKind handle)
    {
        PushUndo();
        _drag = DragMode.Resize;
        _activeHandle = handle;
        _dragStartCanvas = pos;
        _origX = el.X; _origY = el.Y; _origW = el.Width; _origH = el.Height;
        _origFontSize = el is TextElement t ? t.FontSize : 0;
        _origCenter = el.Center;
        if (el is GroupElement ge) ge.CaptureOriginalSize();
        Pasteboard.CaptureMouse();
    }

    /// <summary>
    /// Rotation-aware resize. Pointer positions are mapped into the element's
    /// local (unrotated) frame; the rect edge(s) for the active handle follow
    /// the pointer while the opposite edge stays pinned.
    /// </summary>
    private void DoResize(DesignElement el, Point pos)
    {
        bool ctrlSnap = (Keyboard.Modifiers & ModifierKeys.Control) != 0 &&
                        GuideOverlay.Visibility == Visibility.Visible &&
                        Math.Abs(el.RotationAngle) < 0.01;
        if (ctrlSnap)
            pos = SnapResizePointer(pos, _activeHandle);
        else
            HideSnapGuides();

        double theta = el.RotationAngle * Math.PI / 180.0;
        double cos = Math.Cos(-theta), sin = Math.Sin(-theta);

        Point toLocal(Point c)
        {
            double vx = c.X - _origCenter.X, vy = c.Y - _origCenter.Y;
            return new Point(vx * cos - vy * sin + _origW / 2, vx * sin + vy * cos + _origH / 2);
        }

        var ls = toLocal(_dragStartCanvas);
        var ln = toLocal(pos);
        double dx = ln.X - ls.X, dy = ln.Y - ls.Y;

        double left = 0, top = 0, right = _origW, bottom = _origH;
        switch (_activeHandle)
        {
            case HandleKind.E: right = Math.Max(MinSize, _origW + dx); break;
            case HandleKind.W: left = Math.Min(_origW - MinSize, dx); break;
            case HandleKind.S: bottom = Math.Max(MinSize, _origH + dy); break;
            case HandleKind.N: top = Math.Min(_origH - MinSize, dy); break;
            case HandleKind.SE: right = Math.Max(MinSize, _origW + dx); bottom = Math.Max(MinSize, _origH + dy); break;
            case HandleKind.SW: left = Math.Min(_origW - MinSize, dx); bottom = Math.Max(MinSize, _origH + dy); break;
            case HandleKind.NE: right = Math.Max(MinSize, _origW + dx); top = Math.Min(_origH - MinSize, dy); break;
            case HandleKind.NW: left = Math.Min(_origW - MinSize, dx); top = Math.Min(_origH - MinSize, dy); break;
            default: return;
        }

        // Shift on any handle (or the lock toggle) keeps the original aspect
        // ratio. Corners pin the opposite corner; side handles pin the opposite
        // edge and grow/shrink the other axis symmetrically.
        bool lockAspect = (_lockAspectRatio || Keyboard.IsKeyDown(Key.LeftShift)
            || Keyboard.IsKeyDown(Key.RightShift)) && _origW > 0 && _origH > 0;
        if (lockAspect)
        {
            if (_activeHandle is HandleKind.E or HandleKind.W)
            {
                double scaledW = Math.Max(MinSize, right - left);
                double scaledH = Math.Max(MinSize, scaledW * _origH / _origW);
                double cy = (top + bottom) / 2;
                top = cy - scaledH / 2;
                bottom = cy + scaledH / 2;
            }
            else if (_activeHandle is HandleKind.N or HandleKind.S)
            {
                double scaledH = Math.Max(MinSize, bottom - top);
                double scaledW = Math.Max(MinSize, scaledH * _origW / _origH);
                double cx = (left + right) / 2;
                left = cx - scaledW / 2;
                right = cx + scaledW / 2;
            }
            else
            {
                double ax, ay; // pinned corner
                switch (_activeHandle)
                {
                    case HandleKind.NW: ax = right; ay = bottom; break;
                    case HandleKind.NE: ax = left; ay = bottom; break;
                    case HandleKind.SW: ax = right; ay = top; break;
                    default: ax = left; ay = top; break; // SE
                }
                double freeX = _activeHandle is HandleKind.NW or HandleKind.SW ? left : right;
                double freeY = _activeHandle is HandleKind.NW or HandleKind.NE ? top : bottom;
                double w = Math.Abs(freeX - ax), h = Math.Abs(freeY - ay);
                double scale = Math.Min(w / _origW, h / _origH);
                double nw = Math.Max(MinSize, _origW * scale);
                double nh = Math.Max(MinSize, _origH * scale);
                switch (_activeHandle)
                {
                    case HandleKind.NW: right = ax; bottom = ay; left = right - nw; top = bottom - nh; break;
                    case HandleKind.NE: left = ax; bottom = ay; right = left + nw; top = bottom - nh; break;
                    case HandleKind.SW: right = ax; top = ay; left = right - nw; bottom = top + nh; break;
                    default: left = ax; top = ay; right = left + nw; bottom = top + nh; break; // SE
                }
            }
        }

        double newW = Math.Max(MinSize, right - left);
        double newH = Math.Max(MinSize, bottom - top);

        // Text is auto-sized from the font: resize by scaling the font size so the
        // glyphs grow/shrink instead of being stretched or leaving empty space.
        if (el is TextElement te)
        {
            double factor = _activeHandle switch
            {
                HandleKind.N or HandleKind.S => newH / Math.Max(1, _origH),
                HandleKind.E or HandleKind.W => newW / Math.Max(1, _origW),
                _ => Math.Max(newW / Math.Max(1, _origW), newH / Math.Max(1, _origH)),
            };
            double oldW = te.Width, oldH = te.Height;
            bool anchoring = te.Anchoring;
            te.Anchoring = false; // the handle controls the box; anchor afterwards
            te.FontSize = Math.Clamp(_origFontSize * factor, 6, 300);
            te.Anchoring = anchoring;
            double dw = te.Width - oldW, dh = te.Height - oldH;
            if (_activeHandle is HandleKind.W or HandleKind.NW or HandleKind.SW) te.X -= dw;
            if (_activeHandle is HandleKind.N or HandleKind.NW or HandleKind.NE) te.Y -= dh;
            ShowSizeTipText($"{te.Width:F0} \u00D7 {te.Height:F0} px");
            return;
        }

        double ox = (left + right) / 2 - _origW / 2;
        double oy = (top + bottom) / 2 - _origH / 2;
        double c2 = Math.Cos(theta), s2 = Math.Sin(theta);
        var newCenter = new Point(
            _origCenter.X + ox * c2 - oy * s2,
            _origCenter.Y + ox * s2 + oy * c2);

        el.Width = newW;
        el.Height = newH;
        el.X = newCenter.X - newW / 2;
        el.Y = newCenter.Y - newH / 2;
        ShowSizeTipText($"{el.Width:F0} \u00D7 {el.Height:F0} px");
    }

    private Point SnapResizePointer(Point pos, HandleKind handle)
    {
        double capture = SnapCapturePx / Math.Max(0.25, _zoom);
        double release = SnapReleasePx / Math.Max(0.25, _zoom);
        bool horizontalEdge = handle is HandleKind.E or HandleKind.W or
            HandleKind.NE or HandleKind.SE or HandleKind.NW or HandleKind.SW;
        bool verticalEdge = handle is HandleKind.N or HandleKind.S or
            HandleKind.NE or HandleKind.SE or HandleKind.NW or HandleKind.SW;

        double? snapX = horizontalEdge
            ? _snapResizeX.Lock(pos.X, GuideOverlay.GetVerticalLines(), capture, release)
            : null;
        double? snapY = verticalEdge
            ? _snapResizeY.Lock(pos.Y, GuideOverlay.GetHorizontalLines(), capture, release)
            : null;

        if (snapX is double x) ShowSnapX(x); else SnapGuideV.Visibility = Visibility.Collapsed;
        if (snapY is double y) ShowSnapY(y); else SnapGuideH.Visibility = Visibility.Collapsed;

        return new Point(snapX ?? pos.X, snapY ?? pos.Y);
    }

    /// <summary>
    /// One axis of a snap lock. A guide that caught the pointer keeps it until
    /// the pointer moves clearly past it, so the edge does not flicker between
    /// two nearby guides.
    /// </summary>
    private struct SnapAxis
    {
        private double _guide;

        public SnapAxis()
        {
            _guide = double.NaN;
        }

        public double? Lock(double value, IEnumerable<double> guides, double capture, double release)
        {
            if (!double.IsNaN(_guide) && Math.Abs(_guide - value) <= release) return _guide;
            _guide = NearestGuide(guides, value, capture) ?? double.NaN;
            return double.IsNaN(_guide) ? null : _guide;
        }

        public void Reset() => _guide = double.NaN;
    }

    private static double? NearestGuide(IEnumerable<double> guides, double value, double tolerance)
    {
        double? best = null;
        double bestDelta = double.MaxValue;
        foreach (double guide in guides)
        {
            double delta = Math.Abs(guide - value);
            if (delta <= tolerance && delta < bestDelta)
            {
                best = guide;
                bestDelta = delta;
            }
        }
        return best;
    }

    private void BeginRotate(DesignElement el, Point pos)
    {
        PushUndo();
        _drag = DragMode.Rotate;
        _origCenter = el.Center;
        _startElementAngle = el.RotationAngle;
        _startPointerAngle = PointerAngle(_origCenter, pos);
        Pasteboard.CaptureMouse();
        Cursor = Cursors.Hand;
    }

    private void DoRotate(DesignElement el, Point pos)
    {
        double current = PointerAngle(_origCenter, pos);
        double next = _startElementAngle + (current - _startPointerAngle);
        if (Keyboard.IsKeyDown(Key.LeftShift) || Keyboard.IsKeyDown(Key.RightShift))
            next = Math.Round(next / 15) * 15; // snap
        el.RotationAngle = next;
    }

    private static double PointerAngle(Point center, Point p) =>
        Math.Atan2(p.Y - center.Y, p.X - center.X) * 180 / Math.PI + 90;

    // ----- slice regions: white underlay + cyan (active) / dim (rest) dashes -----

    private void DoSlice(Point pos)
    {
        _pendingSlice = NormalizedRect(_dragStartCanvas, pos);
        RefreshSliceLayer();
    }

    /// <summary>Rebuild all slice markers. Active region is cyan, the rest dim.</summary>
    private void RefreshSliceLayer()
    {
        SliceLayer.Children.Clear();
        if (!_sliceVisible) return;
        foreach (var region in _slices)
            AddSliceVisual(region, ReferenceEquals(region, _activeSlice));
        if (_drag == DragMode.Slice && _pendingSlice is Rect pr && pr.Width >= 1 && pr.Height >= 1)
            AddSliceVisual(new SliceRegion { Rect = pr }, true);
        if (_sliceBezierDraft.Count > 0)
            AddSliceDraftVisual();
    }

    private void AddSliceVisual(SliceRegion region, bool active)
    {
        var rect = region.Rect;
        Geometry geometry = region.SelectionGeometry ?? (Geometry)new RectangleGeometry(rect);
        var back = new System.Windows.Shapes.Path
        {
            Data = geometry,
            Stroke = Brushes.White,
            StrokeThickness = 1,
            StrokeDashArray = new DoubleCollection { 7, 5 },
            StrokeDashOffset = 6,
            Fill = Brushes.Transparent,
            IsHitTestVisible = false,
        };
        var front = new System.Windows.Shapes.Path
        {
            Data = geometry,
            Stroke = new SolidColorBrush(active
                ? Color.FromRgb(0x00, 0xBF, 0xFF)   // active: cyan
                : Color.FromRgb(0x8A, 0x94, 0xA6)), // inactive: dim gray
            StrokeThickness = 1,
            StrokeDashArray = new DoubleCollection { 7, 5 },
            Fill = Brushes.Transparent,
            IsHitTestVisible = false,
        };
        foreach (var path in new[] { back, front })
        {
            SliceLayer.Children.Add(path);
        }
    }

    private void AddBezierSlicePoint(Point point, int clickCount)
    {
        if (_sliceBezierDraft.Count >= 3 && DistanceSquared(point, _sliceBezierDraft[0]) < 100)
        {
            FinishBezierSlice();
            return;
        }
        if (_sliceBezierDraft.Count > 0 && DistanceSquared(point, _sliceBezierDraft[^1]) < 4)
        {
            if (clickCount >= 2) FinishBezierSlice();
            return;
        }
        _sliceBezierDraft.Add(point);
        _sliceBezierCursor = point;
        RefreshSliceLayer();
        if (clickCount >= 2) FinishBezierSlice();
        else StatusText.Text = "Bezier slice: click to add exact corner points; click the first point, double-click or press Enter to close; Esc cancels.";
    }

    private void FinishBezierSlice()
    {
        if (_sliceBezierDraft.Count < 3)
        {
            StatusText.Text = "A Bezier slice needs at least 3 points.";
            return;
        }
        var points = _sliceBezierDraft.ToArray();
        var geometry = CreateAnchoredPolygonGeometry(points);
        var bounds = geometry.Bounds;
        if (bounds.Width < 5 || bounds.Height < 5) return;
        if (!_sliceAddMode) _slices.Clear();
        var region = new SliceRegion { Rect = bounds, Name = $"slice{_slices.Count + 1}", SelectionGeometry = geometry };
        _slices.Add(region);
        _activeSlice = region;
        _sliceBezierDraft.Clear();
        _sliceBezierCursor = null;
        StatusText.Text = $"Bezier slice {bounds.Width:F0}×{bounds.Height:F0} — Ctrl+C copies the selected area.";
        RefreshSliceLayer();
    }

    private static PathGeometry CreateAnchoredPolygonGeometry(IReadOnlyList<Point> points)
    {
        var geometry = new PathGeometry();
        var figure = new PathFigure { StartPoint = points[0], IsClosed = true, IsFilled = true };
        figure.Segments.Add(new PolyLineSegment(points.Skip(1).ToArray(), true));
        geometry.Figures.Add(figure);
        geometry.Freeze();
        return geometry;
    }

    private void AddSliceDraftVisual()
    {
        var draft = _sliceBezierDraft.ToList();
        if (_sliceBezierCursor is Point cursor && DistanceSquared(cursor, draft[^1]) > 1)
            draft.Add(cursor);
        if (draft.Count < 2) return;
        var geometry = new StreamGeometry();
        using (var ctx = geometry.Open())
        {
            ctx.BeginFigure(draft[0], false, false);
            ctx.PolyLineTo(draft.Skip(1).ToArray(), true, false);
        }
        geometry.Freeze();
        SliceLayer.Children.Add(new System.Windows.Shapes.Path
        {
            Data = geometry, Stroke = new SolidColorBrush(Color.FromRgb(0, 0xBF, 0xFF)),
            StrokeThickness = 1.5, StrokeDashArray = new DoubleCollection { 6, 3 },
            IsHitTestVisible = false,
        });
        foreach (var anchor in _sliceBezierDraft)
            SliceLayer.Children.Add(new System.Windows.Shapes.Ellipse
            {
                Width = 6, Height = 6, Fill = Brushes.White, Stroke = Brushes.DeepSkyBlue,
                StrokeThickness = 1, IsHitTestVisible = false,
                RenderTransform = new TranslateTransform(anchor.X - 3, anchor.Y - 3),
            });
    }

    private void ClearSlice()
    {
        _slices.Clear();
        _activeSlice = null;
        _pendingSlice = null;
        _sliceBezierDraft.Clear();
        _sliceBezierCursor = null;
        _sliceVisible = true;
        RefreshSliceLayer();
    }

    /// <summary>
    /// Arrow keys nudge the selection (1px, Shift = 10px). With nothing
    /// selected they move the active slice region instead.
    /// Returns false when there is nothing to nudge (view scrolls instead).
    /// </summary>
    private bool NudgeWithArrows(Key key)
    {
        if (_drag != DragMode.None) return true;
        int step = (Keyboard.IsKeyDown(Key.LeftShift) || Keyboard.IsKeyDown(Key.RightShift)) ? 10 : 1;
        double dx = key == Key.Left ? -step : key == Key.Right ? step : 0;
        double dy = key == Key.Up ? -step : key == Key.Down ? step : 0;

        if (_doc.SelectionCount > 0)
        {
            PushUndo();
            foreach (var el in _doc.SelectedElements.ToList())
            {
                el.X += dx;
                el.Y += dy;
            }
            return true;
        }
        if (_activeSlice != null)
        {
            var r = _activeSlice.Rect;
            r.Offset(dx, dy);
            _activeSlice.Rect = r;
            RefreshSliceLayer();
            StatusText.Text = $"Slice @({r.X:F0},{r.Y:F0}).";
            return true;
        }
        return false;
    }

    /// <summary>Space: show/hide all slice markers (regions are kept).</summary>
    private void ToggleSliceVisibility()
    {
        if (_slices.Count == 0)
        {
            StatusText.Text = "No slices to show — draw one with the Slice tool (M).";
            return;
        }
        _sliceVisible = !_sliceVisible;
        RefreshSliceLayer();
        StatusText.Text = _sliceVisible
            ? "Slice markers shown — Delete / Ctrl+C act on the active slice again."
            : "Slice markers hidden — Delete / Ctrl+C now ignore the slice (Space to show it again).";
    }

    // ----- rectangle drawing -----

    private static Rect NormalizedRect(Point a, Point b) => new(
        Math.Min(a.X, b.X), Math.Min(a.Y, b.Y),
        Math.Abs(b.X - a.X), Math.Abs(b.Y - a.Y));

    private void PositionDrawVisual(Rect rect)
    {
        // Show the shape exactly as it will be created on mouse-up.
        var preview = CreateShapeFromDefaults(rect);
        DrawShapeVisual.Data = preview.ShapeGeometry;
        DrawShapeVisual.Fill = preview.FillBrush;
        DrawShapeVisual.Stroke = preview.BorderBrush;
        DrawShapeVisual.StrokeThickness = preview.StrokeThickness;
        DrawShapeVisual.Opacity = preview.OpacityFactor;

        Canvas.SetLeft(DrawShapeVisual, rect.X);
        Canvas.SetTop(DrawShapeVisual, rect.Y);
        DrawShapeVisual.Clip = new RectangleGeometry(new Rect(
            _doc.ArtboardX - rect.X, _doc.ArtboardY - rect.Y,
            _doc.ArtboardWidth, _doc.ArtboardHeight));
        DrawShapeVisual.Visibility = Visibility.Visible;
    }

    private void UpdateDrawVisual(Point pos)
    {
        var rect = CurrentDrawRect(pos);
        PositionDrawVisual(rect);
        ShowDrawSizeTip(rect);
    }

    private void ShowDrawSizeTip(Rect rect)
    {
        if (rect.Width < 0.5 && rect.Height < 0.5)
        {
            HideDrawSizeTip();
            return;
        }

        ShowSizeTipText($"{rect.Width:F0} \u00D7 {rect.Height:F0} px");
    }

    private void ShowLineSizeTip(Point start, Point end)
    {
        var delta = end - start;
        if (delta.Length < 0.5)
        {
            HideDrawSizeTip();
            return;
        }

        ShowSizeTipText($"{Math.Abs(delta.X):F0} \u00D7 {Math.Abs(delta.Y):F0} px");
    }

    private void ShowSizeTipText(string text)
    {
        DrawSizeTipText.Text = text;
        DrawSizeTip.Visibility = Visibility.Visible;
        DrawSizeTip.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        var size = DrawSizeTip.DesiredSize;

        var p = Mouse.GetPosition(InteractionOverlay);
        double x = p.X + 16;
        double y = p.Y + 18;
        if (x + size.Width + 6 > InteractionOverlay.ActualWidth) x = p.X - 16 - size.Width;
        if (y + size.Height + 6 > InteractionOverlay.ActualHeight) y = p.Y - 18 - size.Height;
        Canvas.SetLeft(DrawSizeTip, Math.Max(4, x));
        Canvas.SetTop(DrawSizeTip, Math.Max(4, y));
    }

    private void HideDrawSizeTip() => DrawSizeTip.Visibility = Visibility.Collapsed;

    private Rect CurrentDrawRect(Point pos)
    {
        var rect = NormalizedRect(_drawStart, pos);
        bool constrain = _lockDrawRatio
            || Keyboard.IsKeyDown(Key.LeftShift) || Keyboard.IsKeyDown(Key.RightShift);
        if (!constrain) return rect;

        double size = Math.Max(rect.Width, rect.Height);
        double x = pos.X >= _drawStart.X ? _drawStart.X : _drawStart.X - size;
        double y = pos.Y >= _drawStart.Y ? _drawStart.Y : _drawStart.Y - size;
        return new Rect(x, y, size, size);
    }

    private ShapeElement CreateShapeFromDefaults(Rect rect)
    {
        var d = _settings.Shape;
        var shapeKind = ParseEnum(d.ShapeKind, ShapeKind.Rectangle);
        if (shapeKind == ShapeKind.CustomPath) shapeKind = ShapeKind.Rectangle;
        var el = new ShapeElement
        {
            X = rect.X, Y = rect.Y,
            Width = Math.Max(1, rect.Width), Height = Math.Max(1, rect.Height),
            Kind = shapeKind,
            CornerRadius = d.CornerRadius,
            UseIndividualCornerRadii = d.IndividualCorners,
            IndividualCornerRadii = new CornerRadius(d.CornerTopLeft, d.CornerTopRight,
                d.CornerBottomRight, d.CornerBottomLeft),
            IsGradient = d.IsGradient,
            GradientType = ParseEnum(d.GradientType, GradientType.Linear),
            GradientAngle = d.GradientAngle,
            StrokeThickness = d.StrokeThickness,
            Opacity = d.Opacity,
            SoftEdge = d.SoftEdge,
            ShadowStyle = ParseEnum(d.ShadowStyle, ShadowStyle.None),
            ShadowX = d.ShadowX, ShadowY = d.ShadowY,
            ShadowBlur = d.ShadowBlur, ShadowOpacity = d.ShadowOpacity,
        };
        if (TryParseColor(d.Fill1, out var fill1)) el.FillColor1 = fill1;
        if (TryParseColor(d.Fill2, out var fill2)) el.FillColor2 = fill2;
        if (TryParseColor(d.Stroke, out var stroke)) el.StrokeColor = stroke;
        if (TryParseColor(d.ShadowColor, out var shadow)) el.ShadowColor = shadow;
        return el;
    }

    private static T ParseEnum<T>(string? value, T fallback) where T : struct, Enum =>
        Enum.TryParse<T>(value, ignoreCase: true, out var parsed) ? parsed : fallback;

    private static LineToolMode ParseLineToolMode(string? value) =>
        string.Equals(value, "Arrow", StringComparison.OrdinalIgnoreCase)
            ? LineToolMode.Line
            : ParseEnum(value, LineToolMode.Line);

    private static LineEndpointStyle ParseLineEndpointStyle(string? value) =>
        string.Equals(value, "Triangle", StringComparison.OrdinalIgnoreCase)
            ? LineEndpointStyle.OpenArrow
            : ParseEnum(value, LineEndpointStyle.Flat);

    private void CaptureShapeDefaults(ShapeElement sh)
    {
        var d = _settings.Shape;
        d.Fill1 = ToHex(sh.FillColor1);
        d.Fill2 = ToHex(sh.FillColor2);
        d.IsGradient = sh.IsGradient;
        d.GradientType = sh.GradientType.ToString();
        d.GradientAngle = sh.GradientAngle;
        d.Stroke = ToHex(sh.StrokeColor);
        d.StrokeThickness = sh.StrokeThickness;
        if (sh.Kind != ShapeKind.CustomPath)
            d.ShapeKind = sh.Kind.ToString();
        d.CornerRadius = sh.CornerRadius;
        d.IndividualCorners = sh.UseIndividualCornerRadii;
        d.CornerTopLeft = sh.IndividualCornerRadii.TopLeft;
        d.CornerTopRight = sh.IndividualCornerRadii.TopRight;
        d.CornerBottomRight = sh.IndividualCornerRadii.BottomRight;
        d.CornerBottomLeft = sh.IndividualCornerRadii.BottomLeft;
        d.Opacity = sh.Opacity;
        d.SoftEdge = sh.SoftEdge;
        d.ShadowStyle = sh.ShadowStyle.ToString();
        d.ShadowX = sh.ShadowX;
        d.ShadowY = sh.ShadowY;
        d.ShadowBlur = sh.ShadowBlur;
        d.ShadowOpacity = sh.ShadowOpacity;
        d.ShadowColor = ToHex(sh.ShadowColor);
    }

    /// <summary>
    /// Save the shape defaults without clobbering canvas defaults changed in
    /// Settings while the editor was open.
    /// </summary>
    private void PersistShapeDefaults()
    {
        var fresh = AppSettings.Load();
        fresh.Shape = _settings.Shape;
        fresh.Save();
    }

    private static bool IsShapeStyleProperty(string? name) => name is
        nameof(ShapeElement.FillColor1) or nameof(ShapeElement.FillColor2)
        or nameof(ShapeElement.IsGradient) or nameof(ShapeElement.GradientType)
        or nameof(ShapeElement.GradientAngle) or nameof(ShapeElement.StrokeColor)
        or nameof(ShapeElement.StrokeThickness) or nameof(ShapeElement.CornerRadius)
        or nameof(ShapeElement.Kind)
        or nameof(DesignElement.Opacity) or nameof(DesignElement.SoftEdge)
        or nameof(DesignElement.ShadowStyle) or nameof(DesignElement.ShadowX)
        or nameof(DesignElement.ShadowY) or nameof(DesignElement.ShadowBlur)
        or nameof(DesignElement.ShadowOpacity) or nameof(DesignElement.ShadowColor)
        or nameof(DesignElement.UseOpacityGradient) or nameof(DesignElement.OpacityDirection)
        or nameof(DesignElement.OpacityStart) or nameof(DesignElement.OpacityEnd)
        or nameof(DesignElement.OpacityStrength);

    private void FinishDraw(Point pos)
    {
        var rect = CurrentDrawRect(pos);
        DrawShapeVisual.Visibility = Visibility.Collapsed;
        HideDrawSizeTip();
        if (rect.Width < 1 || rect.Height < 1) return; // plain click: draw nothing

        PushUndo();
        var el = CreateShapeFromDefaults(rect);
        _doc.Add(el); // selects the new shape
        PersistShapeDefaults();
        StatusText.Text = $"Drew {el.Kind.ToString().ToLowerInvariant()} {rect.Width:F0}x{rect.Height:F0}. Drag another, or press V for Pick.";
        NoteIfOutsideCanvas();
    }

    private void BeginLineDraw(Point pos)
    {
        ResetLineDraft();
        if (_lineMode == LineToolMode.Line && (Keyboard.Modifiers & ModifierKeys.Control) != 0)
            pos = SnapLinePoint(pos);
        _lineDraft.Add(pos);
        _drawStart = pos;
        _drag = DragMode.DrawLine;
        Pasteboard.CaptureMouse();
        UpdateLinePreview(pos);
        StatusText.Text = _lineMode == LineToolMode.Freehand
            ? "Freehand line: draw and release. Esc cancels."
            : "Drag to draw a straight line. Shift snaps the angle to 15°; Ctrl snaps to guides, canvas and objects. Esc cancels.";
    }

    private void UpdateLinePreview(Point cursor)
    {
        if (_lineMode == LineToolMode.Polyline && _lineDraft.Count > 0)
            cursor = AdjustLineEndpoint(_lineDraft[^1], cursor);
        else if (_lineMode == LineToolMode.Line && _drag == DragMode.DrawLine)
            cursor = AdjustLineEndpoint(_drawStart, cursor);

        if (_lineMode is LineToolMode.Line or LineToolMode.Polyline)
        {
            var anchor = _lineMode == LineToolMode.Polyline && _lineDraft.Count > 0
                ? _lineDraft[^1]
                : _drawStart;
            ShowLineSizeTip(anchor, cursor);
        }

        var points = (_lineMode is LineToolMode.Freehand or LineToolMode.Polyline)
            ? _lineDraft.ToList()
            : new List<Point> { _drawStart, cursor };
        if ((_lineMode is LineToolMode.Freehand or LineToolMode.Polyline) && points.Count > 0 &&
            (cursor - points[^1]).Length > 0.1)
            points.Add(cursor);
        if (points.Count < 2) return;

        var line = CreateLineFromPoints(points,
            freehand: _lineMode == LineToolMode.Freehand,
            polyline: _lineMode == LineToolMode.Polyline);
        LinePreview.Data = line.PathGeometry;
        LinePreview.Stroke = line.StrokeBrush;
        LinePreview.StrokeThickness = line.StrokeThickness;
        LinePreview.StrokeDashArray = line.DashArray;
        LinePreview.StrokeStartLineCap = line.StrokeStartLineCap;
        LinePreview.StrokeEndLineCap = line.StrokeEndLineCap;
        LinePreview.Width = line.Width;
        LinePreview.Height = line.Height;
        LineStartArrowPreview.Data = line.StartMarkerGeometry;
        LineStartArrowPreview.Fill = line.StrokeBrush;
        LineStartArrowPreview.Stroke = line.StrokeBrush;
        LineStartArrowPreview.StrokeThickness = line.StrokeThickness;
        LineStartArrowPreview.Width = line.Width;
        LineStartArrowPreview.Height = line.Height;
        LineEndArrowPreview.Data = line.EndMarkerGeometry;
        LineEndArrowPreview.Fill = line.StrokeBrush;
        LineEndArrowPreview.Stroke = line.StrokeBrush;
        LineEndArrowPreview.StrokeThickness = line.StrokeThickness;
        LineEndArrowPreview.Width = line.Width;
        LineEndArrowPreview.Height = line.Height;
        LinePreviewGroup.Width = line.Width;
        LinePreviewGroup.Height = line.Height;
        LinePreviewGroup.Effect = line.ShadowEffect;
        Canvas.SetLeft(LinePreviewGroup, line.X);
        Canvas.SetTop(LinePreviewGroup, line.Y);
        LinePreviewGroup.Visibility = Visibility.Visible;
    }

    private Point AdjustLineEndpoint(Point start, Point target)
    {
        var delta = target - start;
        if (delta.Length > 0.001 &&
            (Keyboard.Modifiers & ModifierKeys.Shift) != 0)
        {
            double angleStep = Math.PI / 12; // 15-degree increments
            double angle = Math.Round(Math.Atan2(delta.Y, delta.X) / angleStep) * angleStep;
            target = start + new Vector(Math.Cos(angle), Math.Sin(angle)) * delta.Length;
        }

        return (Keyboard.Modifiers & ModifierKeys.Control) != 0
            ? SnapLinePoint(target)
            : HideSnapGuidesAndReturn(target);
    }

    private Point SnapLinePoint(Point point)
    {
        double tolerance = SnapCapturePx / Math.Max(0.25, _zoom);
        var xGuides = new List<double>();
        var yGuides = new List<double>();
        var art = _doc.ArtboardRect;
        xGuides.AddRange(new[] { art.Left, art.Left + art.Width / 2, art.Right });
        yGuides.AddRange(new[] { art.Top, art.Top + art.Height / 2, art.Bottom });

        if (GuideOverlay.Visibility == Visibility.Visible)
        {
            xGuides.AddRange(GuideOverlay.GetVerticalLines());
            yGuides.AddRange(GuideOverlay.GetHorizontalLines());
        }

        foreach (var element in _doc.Elements.Where(element => !element.IsBackgroundLayer))
        {
            xGuides.AddRange(new[] { element.X, element.X + element.Width / 2, element.X + element.Width });
            yGuides.AddRange(new[] { element.Y, element.Y + element.Height / 2, element.Y + element.Height });
        }

        double? snapX = NearestGuide(xGuides, point.X, tolerance);
        double? snapY = NearestGuide(yGuides, point.Y, tolerance);
        if (snapX is double x) ShowSnapX(x); else SnapGuideV.Visibility = Visibility.Collapsed;
        if (snapY is double y) ShowSnapY(y); else SnapGuideH.Visibility = Visibility.Collapsed;
        return new Point(snapX ?? point.X, snapY ?? point.Y);
    }

    private Point HideSnapGuidesAndReturn(Point point)
    {
        HideSnapGuides();
        return point;
    }

    private LineElement CreateLineFromPoints(IReadOnlyList<Point> points, bool freehand, bool polyline = false)
    {
        var defaults = _settings.Line;
        double thickness = Math.Clamp(defaults.Thickness, 1, 30);
        double minX = points.Min(p => p.X);
        double minY = points.Min(p => p.Y);
        double maxX = points.Max(p => p.X);
        double maxY = points.Max(p => p.Y);
        bool hasEndpointMarker = ParseLineEndpointStyle(defaults.StartCap) != LineEndpointStyle.Flat
            || ParseLineEndpointStyle(defaults.EndCap) != LineEndpointStyle.Flat;
        double padding = hasEndpointMarker
            ? Math.Max(10, thickness * 2.5)
            : Math.Max(2, thickness * 1.5);
        double width = Math.Max(1, maxX - minX) + padding * 2;
        double height = Math.Max(1, maxY - minY) + padding * 2;
        var line = new LineElement
        {
            X = minX - padding,
            Y = minY - padding,
            Width = width,
            Height = height,
            StrokeColor = ReadLineColor(defaults.Color),
            StrokeThickness = thickness,
            Pattern = ParseEnum(defaults.Pattern, LinePattern.Solid),
            StartCap = ParseLineEndpointStyle(defaults.StartCap),
            EndCap = ParseLineEndpointStyle(defaults.EndCap),
            IsFreehand = freehand,
            IsPolyline = polyline,
            CurveStrength = defaults.CurveStrength,
        };
        line.SetPathPoints(points.Select(p => new Point(
            (p.X - minX + padding) / width,
            (p.Y - minY + padding) / height)));
        if (defaults.Shadow)
        {
            line.ApplyShadowPreset(ShadowStyle.Medium);
            line.ShadowColor = ReadLineColor(defaults.ShadowColor);
        }
        return line;
    }

    private void AddPolylinePoint(Point pos, int clickCount)
    {
        if (_lineDraft.Count == 0)
        {
            if ((Keyboard.Modifiers & ModifierKeys.Control) != 0)
                pos = SnapLinePoint(pos);
            _lineDraft.Add(pos);
            UpdateLinePreview(pos);
            StatusText.Text = "Polyline: click to add points; Shift snaps each segment to 15°, Ctrl snaps to guides, canvas and objects. Double-click or Enter finishes; Esc cancels.";
            return;
        }

        pos = AdjustLineEndpoint(_lineDraft[^1], pos);
        double minDistance = Math.Max(1, 2 / Math.Max(0.25, _zoom));
        if ((pos - _lineDraft[^1]).Length >= minDistance)
            _lineDraft.Add(pos);

        if (clickCount >= 2)
        {
            FinishPolylineDraw();
            return;
        }

        UpdateLinePreview(pos);
        StatusText.Text = _lineDraft.Count < 2
            ? "Polyline: click another point; double-click or Enter finishes, Esc cancels."
            : $"Polyline: {_lineDraft.Count} points; click to add a corner, double-click or Enter finishes, Esc cancels.";
    }

    private void FinishLineDraw(Point cursor)
    {
        HideDrawSizeTip();
        if (_lineMode == LineToolMode.Line)
            cursor = AdjustLineEndpoint(_drawStart, cursor);
        var points = _lineMode == LineToolMode.Freehand
            ? _lineDraft.ToList()
            : new List<Point> { _drawStart, cursor };
        if (_lineMode == LineToolMode.Freehand && points.Count > 0 &&
            (cursor - points[^1]).Length > 0.1)
            points.Add(cursor);

        LinePreviewGroup.Visibility = Visibility.Collapsed;
        LinePreview.Data = null;
        LineStartArrowPreview.Data = null;
        LineEndArrowPreview.Data = null;
        _lineDraft.Clear();
        if (points.Count < 2 || (points[^1] - points[0]).Length < 1)
            return;

        PushUndo();
        var line = CreateLineFromPoints(points, freehand: _lineMode == LineToolMode.Freehand);
        _doc.Add(line);
        PersistLineDefaults();
        StatusText.Text = _lineMode == LineToolMode.Freehand
            ? $"Drew freehand line ({points.Count} points). Draw another or press V for Pick."
            : "Drew straight line. Draw another or press V for Pick.";
        NoteIfOutsideCanvas();
    }

    private void FinishPolylineDraw()
    {
        if (_lineDraft.Count < 2)
        {
            StatusText.Text = "Polyline needs at least two points.";
            return;
        }

        var points = _lineDraft.ToList();
        ResetLineDraft();
        PushUndo();
        var line = CreateLineFromPoints(points, freehand: false, polyline: true);
        _doc.Add(line);
        PersistLineDefaults();
        StatusText.Text = $"Drew polyline ({points.Count} points). Draw another or press V for Pick.";
        NoteIfOutsideCanvas();
    }

    private void CancelLineDraft()
    {
        ResetLineDraft();
        _drag = DragMode.None;
        if (Pasteboard.IsMouseCaptured) Pasteboard.ReleaseMouseCapture();
        StatusText.Text = "Line drawing cancelled.";
    }

    private void ResetLineDraft()
    {
        _lineDraft.Clear();
        HideDrawSizeTip();
        if (LinePreview == null) return;
        LinePreviewGroup.Visibility = Visibility.Collapsed;
        LinePreview.Data = null;
        LineStartArrowPreview.Data = null;
        LineEndArrowPreview.Data = null;
        LinePreviewGroup.Effect = null;
    }

    private void UpdatePolygonDraftPreview(Point? cursor)
    {
        if (_polygonDraft.Count == 0)
        {
            ResetPolygonDraft();
            return;
        }

        var line = new StreamGeometry();
        using (var ctx = line.Open())
        {
            ctx.BeginFigure(_polygonDraft[0], false, false);
            for (int i = 1; i < _polygonDraft.Count; i++)
                ctx.LineTo(_polygonDraft[i], true, false);
            if (cursor is Point c) ctx.LineTo(c, true, false);
        }
        line.Freeze();
        PolygonDraftLine.Data = line;
        PolygonDraftLine.Visibility = Visibility.Visible;

        var points = new GeometryGroup();
        foreach (var p in _polygonDraft)
            points.Children.Add(new EllipseGeometry(p, 3.5, 3.5));
        points.Freeze();
        PolygonDraftPoints.Data = points;
        PolygonDraftPoints.Visibility = Visibility.Visible;
    }

    private void FinishPolygonDraft()
    {
        if (_polygonDraft.Count < 3)
        {
            StatusText.Text = "Polygon needs at least 3 points.";
            return;
        }

        double minX = _polygonDraft.Min(p => p.X);
        double minY = _polygonDraft.Min(p => p.Y);
        double maxX = _polygonDraft.Max(p => p.X);
        double maxY = _polygonDraft.Max(p => p.Y);
        double w = maxX - minX, h = maxY - minY;
        if (w < 1 || h < 1)
        {
            StatusText.Text = "Polygon is too small.";
            return;
        }

        var normalized = _polygonDraft
            .Select(p => new Point((p.X - minX) / w, (p.Y - minY) / h))
            .ToList();

        PushUndo();
        var el = CreateShapeFromDefaults(new Rect(minX, minY, w, h));
        el.Kind = ShapeKind.Polygon;
        el.SetPolygonPoints(normalized);
        _doc.Add(el);
        PersistShapeDefaults();
        ResetPolygonDraft();
        StatusText.Text = $"Drew polygon ({normalized.Count} points).";
        NoteIfOutsideCanvas();
    }

    private void CancelPolygonDraft()
    {
        if (_polygonDraft.Count == 0) return;
        ResetPolygonDraft();
        StatusText.Text = "Polygon cancelled.";
    }

    private void ResetPolygonDraft()
    {
        _polygonDraft.Clear();
        if (PolygonDraftLine == null) return;
        PolygonDraftLine.Visibility = Visibility.Collapsed;
        PolygonDraftPoints.Visibility = Visibility.Collapsed;
        PolygonDraftLine.Data = null;
        PolygonDraftPoints.Data = null;
    }

    // ----- visual picking -----

    private DesignElement? PickTopmost(Point pasteboardPoint)
    {
        DesignElement? found = null;
        int bestZ = int.MinValue;
        VisualTreeHelper.HitTest(
            ElementHost, null,
            result =>
            {
                if (result.VisualHit is DependencyObject d)
                {
                    var el = FindElementDataContext(d);
                    if (el != null && !el.IsBackgroundLayer && el.ZIndex >= bestZ)
                    {
                        var selectable = _doc.GetSelectableElement(el);
                        bestZ = selectable.ZIndex;
                        found = selectable;
                    }
                }
                return HitTestResultBehavior.Continue;
            },
            new PointHitTestParameters(pasteboardPoint));
        return found;
    }

    private static DesignElement? FindElementDataContext(DependencyObject d)
    {
        var current = d;
        while (current != null)
        {
            if (current is FrameworkElement fe && fe.DataContext is DesignElement el)
                return el;
            current = VisualTreeHelper.GetParent(current);
        }
        return null;
    }

    // =================================================================
    // Text tool: create + edit in place
    // =================================================================

    private static bool IsInsideVisual(DependencyObject node, DependencyObject ancestor)
    {
        var current = node;
        while (current != null)
        {
            if (current == ancestor) return true;
            current = VisualTreeHelper.GetParent(current);
        }
        return false;
    }

    private void CreateTextAt(Point pos)
    {
        PushUndo();
        var te = new TextElement { X = pos.X, Y = pos.Y };
        _textDefaults.CopyStyleTo(te);
        _doc.Add(te);
        te.EnableAnchoring();
        BeginTextEdit(te, snapshot: false);
        NoteIfOutsideCanvas();
        StatusText.Text = "Type your text - Enter adds a line, Esc or click outside finishes.";
    }

    private void BeginTextEdit(TextElement te, bool selectAll = false, bool snapshot = true)
    {
        if (_editingText != null && _editingText != te) CommitTextEdit();
        if (snapshot) PushUndo();

        _editScrollX = Viewport.HorizontalOffset;
        _editScrollY = Viewport.VerticalOffset;

        _editingText = te;
        te.IsEditing = true;
        SelectionAdorner.Visibility = Visibility.Collapsed;
        _doc.Select(te);

        var box = new TextBox
        {
            Text = te.Text,
            FontFamily = new FontFamily(te.FontFamilyName),
            FontSize = te.FontSize,
            FontWeight = te.FontWeight,
            FontStyle = te.FontStyle,
            Foreground = new SolidColorBrush(te.TextColor1),
            CaretBrush = new SolidColorBrush(te.TextColor1),
            SelectionBrush = new SolidColorBrush(Color.FromArgb(0x55, 0x2E, 0x9B, 0xFF)),
            Background = Brushes.Transparent,
            BorderBrush = new SolidColorBrush(Color.FromRgb(0x2E, 0x9B, 0xFF)),
            BorderThickness = new Thickness(1),
            Padding = new Thickness(2),
            AcceptsReturn = true,
            TextWrapping = TextWrapping.NoWrap,
            VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            MinWidth = 24,
            MinHeight = Math.Max(20, te.FontSize * 1.3),
            FlowDirection = te.FlowDirectionValue,
            TextAlignment = te.LogicalAlignment,
        };
        Panel.SetZIndex(box, 10000);
        box.RequestBringIntoView += (_, e) => e.Handled = true; // no ScrollViewer jump on focus
        // Bind position/size to the model so side-anchored growth moves the editor
        // with the text on every keystroke (local values would stop updating).
        box.SetBinding(Canvas.LeftProperty, new Binding(nameof(DesignElement.X))
        {
            Source = te,
            Converter = new AddConverter(-3),
        });
        box.SetBinding(Canvas.TopProperty, new Binding(nameof(DesignElement.Y))
        {
            Source = te,
            Converter = new AddConverter(-3),
        });
        box.SetBinding(FrameworkElement.WidthProperty, new Binding(nameof(DesignElement.Width))
        {
            Source = te,
            Converter = new AddConverter(6),
        });
        SyncEditorBox(box, te);

        box.TextChanged += (_, _) =>
        {
            if (_editingText != te) return;
            te.Text = box.Text;
            box.FlowDirection = te.FlowDirectionValue;
            box.TextAlignment = te.LogicalAlignment;
            SyncEditorBox(box, te);
        };
        box.KeyDown += (_, ke) =>
        {
            if (ke.Key == Key.Escape) { CommitTextEdit(); ke.Handled = true; }
        };

        _textEditor = box;
        Pasteboard.Children.Add(box);
        box.Focus();
        if (selectAll) box.SelectAll();
        else box.CaretIndex = box.Text.Length;
        // Focusing a TextBox makes the ScrollViewer bring it into view; keep the
        // canvas where the user left it instead of jumping sideways.
        RestoreEditScrollSoon();
    }

    private void RestoreEditScroll()
    {
        Viewport.ScrollToHorizontalOffset(_editScrollX);
        Viewport.ScrollToVerticalOffset(_editScrollY);
    }

    private void RestoreEditScrollSoon()
    {
        Dispatcher.BeginInvoke(new Action(RestoreEditScroll),
            System.Windows.Threading.DispatcherPriority.Loaded);
        Dispatcher.BeginInvoke(new Action(RestoreEditScroll),
            System.Windows.Threading.DispatcherPriority.ContextIdle);
    }

    private void CommitTextEdit()
    {
        var te = _editingText;
        var box = _textEditor;
        if (te == null && box == null) return;

        _editingText = null;
        _textEditor = null;
        SelectionAdorner.Visibility = Visibility.Visible;

        if (box != null)
        {
            Pasteboard.Children.Remove(box);
            this.Focus(); // not the ScrollViewer: focusing it can scroll the canvas
            RestoreEditScrollSoon();
        }
        if (te == null) return;

        te.RefreshLayout(); // make sure Width/Height match the final text
        te.IsEditing = false;
        if (string.IsNullOrWhiteSpace(te.Text))
        {
            if (_doc.Selected == te) _doc.RemoveSelected();
            else _doc.Elements.Remove(te);
            OnSelectionChanged();
            return;
        }

        _doc.Select(te);
        OnSelectionChanged();
        StatusText.Text = "Text updated — double-click it with Pick to edit again.";
    }

    /// <summary>Keep the in-place editor's wrapping in sync (position/size are bound).</summary>
    private static void SyncEditorBox(TextBox box, TextElement te) =>
        box.TextWrapping = te.WrapWidth > 0 ? TextWrapping.Wrap : TextWrapping.NoWrap;

    /// <summary>Mirror the model's typography into the live editor (font previews, size, style).</summary>
    private void UpdateTextEditorStyle()
    {
        if (_textEditor == null || _doc.Selected is not TextElement te) return;
        _textEditor.FontFamily = te.FontFamilyValue;
        _textEditor.FontSize = te.FontSize;
        _textEditor.FontWeight = te.FontWeight;
        _textEditor.FontStyle = te.FontStyle;
        _textEditor.Foreground = new SolidColorBrush(te.TextColor1);
        _textEditor.CaretBrush = new SolidColorBrush(te.TextColor1);
        _textEditor.FlowDirection = te.FlowDirectionValue;
        _textEditor.TextAlignment = te.LogicalAlignment;
        SyncEditorBox(_textEditor, te);
    }

    // =================================================================
    // Zoom (−/+ on both keyboards, Ctrl+wheel, buttons)
    // =================================================================

    private Point ViewportCenterModel() => new(
        (Viewport.HorizontalOffset + Viewport.ViewportWidth / 2 - _pasteOffsetX) / _zoom,
        (Viewport.VerticalOffset + Viewport.ViewportHeight / 2 - _pasteOffsetY) / _zoom);

    private Point ViewportToModel(Point view) => new(
        (Viewport.HorizontalOffset + view.X - _pasteOffsetX) / _zoom,
        (Viewport.VerticalOffset + view.Y - _pasteOffsetY) / _zoom);

    /// <summary>
    /// Size the scrollable host to the canvas (never smaller than the viewport)
    /// and park the pasteboard so the artboard sits centered. When the scaled
    /// canvas fits, the host equals the viewport → zero scroll range (wheel and
    /// scrollbars do nothing). When it doesn't fit, the
    /// scroll range spans exactly the artboard, so the canvas can never be
    /// scrolled fully out of view.
    /// </summary>
    private void LayoutViewport()
    {
        double vw = Viewport.ViewportWidth;
        double vh = Viewport.ViewportHeight;
        if (vw <= 0 || vh <= 0) return;

        double aw = _doc.ArtboardWidth * _zoom;
        double ah = _doc.ArtboardHeight * _zoom;
        double hostW = Math.Max(aw, vw);
        double hostH = Math.Max(ah, vh);

        ScrollHost.Width = hostW;
        ScrollHost.Height = hostH;

        double offX = (hostW - aw) / 2.0;
        double offY = (hostH - ah) / 2.0;
        _pasteOffsetX = offX - _doc.ArtboardX * _zoom;
        _pasteOffsetY = offY - _doc.ArtboardY * _zoom;

        Canvas.SetLeft(Pasteboard, _pasteOffsetX);
        Canvas.SetTop(Pasteboard, _pasteOffsetY);
    }

    private void ApplyZoom(double z, Point anchorModel, Point anchorView)
    {
        _zoom = Math.Clamp(z, MinZoom, MaxZoom);
        Pasteboard.LayoutTransform = new ScaleTransform(_zoom, _zoom);
        LayoutViewport();
        Pasteboard.UpdateLayout();
        ScrollHost.UpdateLayout();
        Viewport.UpdateLayout();
        Viewport.ScrollToHorizontalOffset(_pasteOffsetX + anchorModel.X * _zoom - anchorView.X);
        Viewport.ScrollToVerticalOffset(_pasteOffsetY + anchorModel.Y * _zoom - anchorView.Y);
        Viewport.UpdateLayout();
        UpdateZoomUI();
    }

    private void SetZoom(double z)
    {
        var view = Mouse.GetPosition(Viewport);
        if (view.X >= 0 && view.Y >= 0 &&
            view.X <= Viewport.ViewportWidth && view.Y <= Viewport.ViewportHeight)
        {
            var center = new Point(Viewport.ViewportWidth / 2, Viewport.ViewportHeight / 2);
            ApplyZoom(z, ViewportToModel(view), center);
            return;
        }

        ApplyZoom(z, ViewportCenterModel(),
            new Point(Viewport.ViewportWidth / 2, Viewport.ViewportHeight / 2));
    }

    // Fixed zoom stops so keyboard/button zooming steps to the next entry and
    // therefore always passes exactly through 100% (0.05 .. 8.0).
    private static readonly double[] ZoomStops =
    {
        0.05, 0.10, 0.125, 0.25, 0.33, 0.50, 0.66, 0.75, 1.00,
        1.25, 1.50, 2.00, 3.00, 4.00, 6.00, 8.00,
    };

    private void ZoomStep(int dir)
    {
        const double eps = 1e-4;
        double next = dir > 0 ? MaxZoom : MinZoom;
        if (dir > 0)
        {
            foreach (var s in ZoomStops)
                if (s > _zoom + eps) { next = s; break; }
        }
        else
        {
            for (int i = ZoomStops.Length - 1; i >= 0; i--)
                if (ZoomStops[i] < _zoom - eps) { next = ZoomStops[i]; break; }
        }
        SetZoom(next);
    }

    private void ZoomReset_Click(object sender, RoutedEventArgs e) => SetZoom(1.0);
    private void ZoomIn_Click(object sender, RoutedEventArgs e) => ZoomStep(+1);
    private void ZoomOut_Click(object sender, RoutedEventArgs e) => ZoomStep(-1);

    private void Viewport_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (!Keyboard.IsKeyDown(Key.LeftCtrl) && !Keyboard.IsKeyDown(Key.RightCtrl))
            return; // plain wheel scrolls (only possible while the canvas overflows)
        var view = e.GetPosition(Viewport);
        var model = ViewportToModel(view);
        var center = new Point(Viewport.ViewportWidth / 2, Viewport.ViewportHeight / 2);
        ApplyZoom(_zoom * (e.Delta > 0 ? 1.15 : 1 / 1.15), model, center);
        e.Handled = true;
    }

    private void Viewport_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (Viewport.ViewportWidth <= 0 || Viewport.ViewportHeight <= 0) return;
        // Keep whatever the user is looking at while the host re-clamps.
        var centerModel = ViewportCenterModel();
        var centerView = new Point(Viewport.ViewportWidth / 2, Viewport.ViewportHeight / 2);
        LayoutViewport();
        ScrollHost.UpdateLayout();
        Viewport.ScrollToHorizontalOffset(_pasteOffsetX + centerModel.X * _zoom - centerView.X);
        Viewport.ScrollToVerticalOffset(_pasteOffsetY + centerModel.Y * _zoom - centerView.Y);
    }

    private void UpdateZoomUI()
    {
        string txt = $"{_zoom * 100:F0}%";
        ZoomLabelBtn.Content = txt;
        ZoomStatusText.Text = txt;
        RefreshActiveTabMeta();
        UpdateWindowTitle();
    }

    private void RefreshActiveTabMeta()
    {
        if (_activeTabMeta == null) return;
        _activeTabMeta.Text = $"{_doc.CanvasSizeText}  ·  {_zoom * 100:F0}%";
    }

    private void UpdateWindowTitle() => Title = $"{_doc.DocumentName} — YorteImpact";

    /// <summary>
    /// Fit 100% of the canvas (artboard) into the viewport and center it.
    /// The resulting zoom can be anything (25%, 63%, …); zooming in/out
    /// afterwards pivots around the pointer when it is over the viewport.
    /// </summary>
    private void ZoomToFit()
    {
        Viewport.UpdateLayout();
        double availW = Viewport.ViewportWidth;
        double availH = Viewport.ViewportHeight;
        if (availW <= 0 || availH <= 0 || _doc.ArtboardWidth <= 0 || _doc.ArtboardHeight <= 0)
        {
            // Layout isn't ready yet — retry once it is.
            Dispatcher.BeginInvoke(ZoomToFit);
            return;
        }

        const double margin = 0.96; // breathing room around the artboard
        double z = Math.Min(availW / _doc.ArtboardWidth, availH / _doc.ArtboardHeight) * margin;
        _zoom = Math.Clamp(z, MinZoom, MaxZoom);
        Pasteboard.LayoutTransform = new ScaleTransform(_zoom, _zoom);
        LayoutViewport();
        Pasteboard.UpdateLayout();
        ScrollHost.UpdateLayout();

        double cx = _pasteOffsetX + (_doc.ArtboardX + _doc.ArtboardWidth / 2) * _zoom;
        double cy = _pasteOffsetY + (_doc.ArtboardY + _doc.ArtboardHeight / 2) * _zoom;
        Viewport.ScrollToHorizontalOffset(Math.Max(0, cx - Viewport.ViewportWidth / 2));
        Viewport.ScrollToVerticalOffset(Math.Max(0, cy - Viewport.ViewportHeight / 2));
        UpdateZoomUI();
    }

    // =================================================================
    // Keyboard
    // =================================================================

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (TextFontCombo.IsDropDownOpen)
            return; // font search/list owns the keyboard (type-ahead, arrows, Enter, Esc)
        if (_textEditor != null && e.Key == Key.Escape)
        {
            CommitTextEdit(); // finish typing but stay on the Text tool
            e.Handled = true;
            return;
        }
        if (e.OriginalSource is TextBox && e.Key != Key.Escape)
            return; // let inspector/dialog fields handle their own keys

        if (_polygonDraft.Count > 0)
        {
            if (e.Key == Key.Enter) { FinishPolygonDraft(); e.Handled = true; return; }
            if (e.Key == Key.Escape) { CancelPolygonDraft(); e.Handled = true; return; }
            if (e.Key == Key.Back)
            {
                _polygonDraft.RemoveAt(_polygonDraft.Count - 1);
                if (_polygonDraft.Count == 0) ResetPolygonDraft();
                else UpdatePolygonDraftPreview(null);
                e.Handled = true;
                return;
            }
        }

        if (_sliceBezierDraft.Count > 0 && _tool == ActiveTool.Slice)
        {
            if (e.Key == Key.Enter) FinishBezierSlice();
            else if (e.Key == Key.Escape)
            {
                _sliceBezierDraft.Clear();
                _sliceBezierCursor = null;
                RefreshSliceLayer();
                StatusText.Text = "Bezier slice cancelled.";
            }
            else if (e.Key == Key.Back)
            {
                _sliceBezierDraft.RemoveAt(_sliceBezierDraft.Count - 1);
                if (_sliceBezierDraft.Count == 0) _sliceBezierCursor = null;
                RefreshSliceLayer();
            }
            if (e.Key is Key.Enter or Key.Escape or Key.Back) { e.Handled = true; return; }
        }

        if (_drag == DragMode.DrawLine && e.Key == Key.Escape)
        {
            CancelLineDraft();
            e.Handled = true;
            return;
        }

        if (_tool == ActiveTool.Line && _lineMode == LineToolMode.Polyline && _lineDraft.Count > 0)
        {
            if (e.Key == Key.Enter)
            {
                FinishPolylineDraw();
                e.Handled = true;
                return;
            }
            if (e.Key == Key.Escape)
            {
                CancelLineDraft();
                e.Handled = true;
                return;
            }
        }

        bool ctrl = Keyboard.IsKeyDown(Key.LeftCtrl) || Keyboard.IsKeyDown(Key.RightCtrl);

        if (ctrl && e.Key == Key.N) { NewCanvas_Click(sender, e); e.Handled = true; }
        else if (ctrl && e.Key == Key.T) { NewTab_Click(sender, e); e.Handled = true; }
        else if (ctrl && e.Key == Key.W) { CloseTab(_activeTab); e.Handled = true; }
        else if (ctrl && e.Key == Key.OemComma) { OpenSettings(); e.Handled = true; }
        else if (ctrl && e.Key == Key.O) { OpenProject_Click(sender, e); e.Handled = true; }
        else if (ctrl && e.Key == Key.S && (Keyboard.IsKeyDown(Key.LeftShift) || Keyboard.IsKeyDown(Key.RightShift))) { SaveProjectAs_Click(sender, e); e.Handled = true; }
        else if (ctrl && e.Key == Key.S) { SaveProject_Click(sender, e); e.Handled = true; }
        else if (ctrl && e.Key == Key.C) { CopySelectionOrSlice(); e.Handled = true; }
        else if (ctrl && e.Key == Key.X) { CutSelectionOrSlice(); e.Handled = true; }
        else if (ctrl && e.Key == Key.F) { PickContextColor(); e.Handled = true; }
        else if (ctrl && e.Key == Key.R) { FitCanvasToSelection(); e.Handled = true; }
        else if (ctrl && e.Key == Key.V) { PasteFromClipboardOrPrototype(); e.Handled = true; }
        else if (ctrl && e.Key == Key.D) { DuplicateProjectTab(); e.Handled = true; }
        else if (ctrl && e.Key == Key.Z && !Keyboard.IsKeyDown(Key.LeftShift) && !Keyboard.IsKeyDown(Key.RightShift)) { DoUndo(); e.Handled = true; }
        else if (ctrl && (e.Key == Key.Y || e.Key == Key.Z)) { DoRedo(); e.Handled = true; } // Ctrl+Y or Ctrl+Shift+Z
        else if (e.Key is Key.OemPlus or Key.Add) { ZoomStep(+1); e.Handled = true; }
        else if (e.Key is Key.OemMinus or Key.Subtract) { ZoomStep(-1); e.Handled = true; }
        else if (ctrl && e.Key is Key.D0 or Key.NumPad0) { SetZoom(1.0); e.Handled = true; }
        else if (e.Key is Key.Delete or Key.Back) { DeleteSelectedRespectingSlice(); e.Handled = true; }
        else if (e.Key == Key.Space) { ToggleSliceVisibility(); e.Handled = true; } // show/hide slice markers
        else if (e.Key is Key.Left or Key.Right or Key.Up or Key.Down)
        {
            if (NudgeWithArrows(e.Key)) e.Handled = true; // else: ScrollViewer scrolls
        }
        else if (e.Key == Key.V && !ctrl) { SetTool(ActiveTool.Pick); e.Handled = true; }
        else if (e.Key == Key.M && !ctrl) { SetTool(ActiveTool.Slice); e.Handled = true; }
        else if (e.Key == Key.R && !ctrl) { SetTool(ActiveTool.Rectangle); e.Handled = true; }
        else if (e.Key == Key.L && !ctrl) { SetTool(ActiveTool.Line); e.Handled = true; }
        else if (e.Key == Key.T && !ctrl) { SetTool(ActiveTool.Text); e.Handled = true; }
        else if (e.Key == Key.E && !ctrl) { SetTool(ActiveTool.Erase); e.Handled = true; }
        else if (e.Key == Key.B && !ctrl) { SetTool(ActiveTool.Paintbrush); e.Handled = true; }
        else if (e.Key == Key.I && !ctrl) { ToolElements_Click(sender, e); e.Handled = true; }
        else if (e.Key == Key.OemOpenBrackets && _tool == ActiveTool.Paintbrush) { AdjustBrushSize(-1); e.Handled = true; }
        else if (e.Key == Key.OemCloseBrackets && _tool == ActiveTool.Paintbrush) { AdjustBrushSize(+1); e.Handled = true; }
        else if (e.Key == Key.Escape)
        {
            if (_tool == ActiveTool.Elements)
            {
                SetTool(ActiveTool.Pick);
                e.Handled = true;
                return;
            }
            if (_ctrlGesturePending)
            {
                CancelCtrlGesture();
                e.Handled = true;
                return;
            }
        if (_drag == DragMode.TextPress)
        {
            _textPressTarget = null;
            _drag = DragMode.None;
            if (Pasteboard.IsMouseCaptured) Pasteboard.ReleaseMouseCapture();
            e.Handled = true;
            return;
        }

        if (_drag == DragMode.Marquee)
            {
                MarqueeVisual.Visibility = Visibility.Collapsed;
                _drag = DragMode.None;
                if (Pasteboard.IsMouseCaptured) Pasteboard.ReleaseMouseCapture();
                e.Handled = true;
                return;
            }
            if (_drag == DragMode.Paint) EndBrushStroke();
            if (_drag == DragMode.Draw) { DrawShapeVisual.Visibility = Visibility.Collapsed; HideDrawSizeTip(); }
            if (_drag == DragMode.DrawLine) ResetLineDraft();
            if (_drag == DragMode.Resize) HideDrawSizeTip();
            if (_drag != DragMode.None) { _drag = DragMode.None; _moveUndoPending = false; HideSnapGuides(); Pasteboard.ReleaseMouseCapture(); }
            else if (_tool != ActiveTool.Pick) SetTool(ActiveTool.Pick);
            else
            {
                _doc.Select(null);
                if (_slices.Count > 0) // Esc also dismisses leftover slice markers
                {
                    ClearSlice();
                    StatusText.Text = "Selection and slices cleared.";
                }
            }
            e.Handled = true;
        }
    }

    // =================================================================
    // Copy / Paste
    // Paste priority: fresh system image wins; otherwise our prototype
    // (selected element, or element built from a background slice).
    // =================================================================

    private static uint SafeClipboardSeq()
    {
        try { return GetClipboardSequenceNumber(); }
        catch { return 0; }
    }

    private static bool ClipboardHasImage()
    {
        try { return Clipboard.ContainsImage() || Clipboard.ContainsFileDropList(); }
        catch { return false; }
    }

    /// <summary>
    /// Put the copied pixels on the OS clipboard so other programs (Paint, Word,
    /// browsers, Google Images, …) can paste them: a DIB/Bitmap plus a PNG stream.
    /// </summary>
    private static void SetSystemClipboardImage(BitmapSource bmp)
    {
        try
        {
            var data = new DataObject();
            data.SetImage(bmp);

            var png = new PngBitmapEncoder();
            png.Frames.Add(BitmapFrame.Create(bmp));
            using var ms = new MemoryStream();
            png.Save(ms);
            ms.Position = 0;
            data.SetData("PNG", ms);

            Clipboard.SetDataObject(data, true); // keep after the app closes
        }
        catch
        {
            // The clipboard can be locked by another process; in-app copy still works.
        }
    }

    private void CopySelectionOrSlice()
    {
        if (_doc.SelectionCount > 1)
        {
            StatusText.Text = "Copy works on one object at a time; select a single object or use the right-click save.";
            return;
        }
        var slice = _sliceVisible ? _activeSlice : null;
        bool hasSlice = slice != null && !slice.IsEmpty;

        // A visible slice acts like a marquee: it defines WHAT gets copied.
        // With a selection, that means the slice area OF the selected object.
        if (hasSlice)
        {
            if (_doc.Selected is { } sel) CopySliceOfElement(sel, slice!);
            else CopySliceOfCanvas(slice!);
            return;
        }

        // No slice: copy the whole selected element.
        if (_doc.Selected is { } element)
        {
            _copiedPrototype = element.Clone();
            try { SetSystemClipboardImage(SliceExportService.CropElementRegion(element, element.Bounds, 1.0)); }
            catch { /* element not renderable: internal copy still works */ }
            _copyClipboardSeq = SafeClipboardSeq();
            StatusText.Text = "Copied element — Ctrl+V paste here, or paste it into other apps.";
            return;
        }

        StatusText.Text = "Nothing to copy — select an element, or draw a slice (M) first.";
    }

    /// <summary>Render just the slice intersection of an element at 1:1 and expose it to other apps.</summary>
    private static void PutSliceOnClipboard(DesignElement el, Rect inter, SliceRegion slice)
    {
        try
        {
            var bmp = SliceExportService.CropElementRegion(el, inter, 1.0);
            SetSystemClipboardImage(SliceExportService.ApplySliceMask(bmp, slice, inter));
        }
        catch { /* nothing renderable to share */ }
    }

    /// <summary>Copy the slice area of the selected element (the slice acts as a marquee).</summary>
    private void CopySliceOfElement(DesignElement el, SliceRegion slice)
    {
        var sliceRect = slice.Rect;
        var inter = Rect.Intersect(el.Bounds, sliceRect);
        if (inter.IsEmpty || inter.Width < 1 || inter.Height < 1)
        {
            StatusText.Text = "The slice doesn't overlap the selected element — nothing to copy.";
            return;
        }
        if (!SliceCutService.IsAxisAligned(el))
        {
            // Rotation-aware slicing isn't supported yet: copy the whole object.
            _copiedPrototype = el.Clone();
            try { SetSystemClipboardImage(SliceExportService.CropElementRegion(el, el.Bounds, 1.0)); }
            catch { }
            _copyClipboardSeq = SafeClipboardSeq();
            StatusText.Text = "Rotated element — copied whole (slice copy needs an axis-aligned object).";
            return;
        }

        try
        {
            Pasteboard.UpdateLayout();
            var bmp = SliceExportService.CropElementRegion(el, inter, _zoom);
            bmp = SliceExportService.ApplySliceMask(bmp, slice, inter);
            var (color, uniform) = SliceCutService.AnalyzeRegion(bmp);
            if (uniform)
            {
                _copiedPrototype = new ShapeElement
                {
                    X = inter.X, Y = inter.Y,
                    Width = inter.Width, Height = inter.Height,
                    CornerRadius = 0, FillColor1 = color, IsGradient = false,
                    StrokeColor = color, StrokeThickness = 0,
                };
                StatusText.Text = $"Copied {inter.Width:F0}×{inter.Height:F0} flat tile — Ctrl+V stamps it here, or paste it into other apps.";
            }
            else
            {
                var copy = new ImageElement { X = inter.X, Y = inter.Y };
                copy.SetBitmap(bmp);
                copy.Width = inter.Width;
                copy.Height = inter.Height;
                _copiedPrototype = copy;
                StatusText.Text = $"Copied {inter.Width:F0}×{inter.Height:F0} from the selected object — Ctrl+V here, or in other apps.";
            }
            PutSliceOnClipboard(el, inter, slice);
            _copyClipboardSeq = SafeClipboardSeq();
        }
        catch (Exception ex)
        {
            StatusText.Text = "Copy failed: " + ex.Message;
        }
    }

    /// <summary>Copy the slice area off the canvas (no selection): only the on-canvas part is real content.</summary>
    private void CopySliceOfCanvas(SliceRegion slice)
    {
        var sliceRect = slice.Rect;
        // The pasteboard and the canvas drop shadow around it must be ignored,
        // otherwise a slice crossing the edge copies black/gray filler.
        var region = Rect.Intersect(sliceRect, _doc.ArtboardRect);
        if (region.IsEmpty || region.Width < 1 || region.Height < 1)
        {
            StatusText.Text = "The slice is outside the canvas — nothing to copy.";
            return;
        }

        try
        {
            Pasteboard.UpdateLayout();
            var bmp = SliceExportService.RenderRegion(ElementHost, region, ExportBackground, _zoom);
            bmp = SliceExportService.ApplySliceMask(bmp, slice, region);
            var (color, uniform) = SliceCutService.AnalyzeRegion(bmp);

            if (uniform)
            {
                // Flat canvas (artboard white, …) -> crisp solid shape,
                // positioned exactly over the sliced area.
                _copiedPrototype = new ShapeElement
                {
                    X = region.X, Y = region.Y,
                    Width = region.Width, Height = region.Height,
                    CornerRadius = 0, FillColor1 = color, IsGradient = false,
                    StrokeColor = color, StrokeThickness = 0,
                };
                StatusText.Text = $"Copied {region.Width:F0}×{region.Height:F0} flat tile #{color.R:X2}{color.G:X2}{color.B:X2} — Ctrl+V stamps it here.";
            }
            else
            {
                var el = new ImageElement
                {
                    X = region.X, Y = region.Y,
                };
                el.SetBitmap(bmp);
                el.Width = region.Width;
                el.Height = region.Height;
                _copiedPrototype = el;
                StatusText.Text = $"Copied {region.Width:F0}×{region.Height:F0} slice image — Ctrl+V here, or paste it into other apps.";
            }
            try
            {
                var clipboardBmp = SliceExportService.RenderRegion(ElementHost, region, ExportBackground, 1.0);
                SetSystemClipboardImage(SliceExportService.ApplySliceMask(clipboardBmp, slice, region));
            }
            catch { }
            _copyClipboardSeq = SafeClipboardSeq(); // record the seq we just wrote so in-app paste wins
        }
        catch (Exception ex)
        {
            StatusText.Text = "Copy failed: " + ex.Message;
        }
    }

    private Point PasteTarget()
    {
        var art = _doc.ArtboardRect;
        var mouse = Mouse.GetPosition(Pasteboard);
        if (art.Contains(mouse)) return mouse;

        // The pasteboard margin is outside the visible canvas: pasting there
        // would hide the image under the artboard clip and make it unpickable.
        var center = ViewportCenterModel();
        if (art.Contains(center)) return center;

        return new Point(art.X + art.Width / 2, art.Y + art.Height / 2);
    }

    private void PasteFromClipboardOrPrototype()
    {
        bool wasEmpty = _tabs.Count == 0;
        if (wasEmpty)
        {
            if (!ClipboardHasImage() && _copiedPrototype == null)
            {
                StatusText.Text = "Clipboard has no image. Copy an image first.";
                return;
            }
            AddBlankTab();
        }

        var at = wasEmpty
            ? new Point(_doc.ArtboardX + _doc.ArtboardWidth / 2,
                        _doc.ArtboardY + _doc.ArtboardHeight / 2)
            : PasteTarget();
        bool hasImage = ClipboardHasImage();
        uint cur = SafeClipboardSeq();

        BitmapSource? sys = null;
        if (hasImage && (cur != _copyClipboardSeq || _copiedPrototype == null))
            sys = ClipboardHelper.TryGetImage();

        if (sys != null)
        {
            PasteSystemImage(sys, at);
            return;
        }

        if (_copiedPrototype != null)
        {
            // In-place paste: exact same position as the copied element/slice.
            PushUndo();
            var el = _copiedPrototype.Clone(); // fresh instance so Copy stays re-pasteable
            _doc.Add(el);
            StatusText.Text = "Pasted at the same position (selected — drag to move it).";
            NoteIfOutsideCanvas();
            return;
        }

        if (hasImage)
        {
            sys = ClipboardHelper.TryGetImage(); // clipboard-seq API failed; try anyway
            if (sys != null) { PasteSystemImage(sys, at); return; }
        }
        StatusText.Text = "Clipboard has no image. Copy an image in the browser first (right-click → Copy image).";
    }

    private void PasteSystemImage(BitmapSource bmp, Point at)
    {
        PushUndo();
        var el = new ImageElement();
        el.SetBitmap(bmp); // one canvas unit per image pixel
        PlaceImageOnCanvas(el, at);
        _doc.Add(el);
        StatusText.Text = $"Pasted image {bmp.PixelWidth}×{bmp.PixelHeight}px.";
        NoteIfOutsideCanvas();
    }

    /// <summary>Center the image on <paramref name="at"/> but keep it on the canvas.</summary>
    private void PlaceImageOnCanvas(ImageElement el, Point at)
    {
        var art = _doc.ArtboardRect;
        el.X = el.Width <= art.Width
            ? Math.Clamp(at.X - el.Width / 2, art.Left, art.Right - el.Width)
            : art.Left + (art.Width - el.Width) / 2;
        el.Y = el.Height <= art.Height
            ? Math.Clamp(at.Y - el.Height / 2, art.Top, art.Bottom - el.Height)
            : art.Top + (art.Height - el.Height) / 2;
    }

    /// <summary>Hint when an element lands fully outside the canvas (still recoverable via its box).</summary>
    private void NoteIfOutsideCanvas()
    {
        if (_doc.Selected is { } s && !s.Bounds.IntersectsWith(_doc.ArtboardRect))
            StatusText.Text += " Outside the canvas — drag it back in by its selection box.";
    }

    // =================================================================
    // Delete that respects the slice: cut the overlapped part out,
    // otherwise remove the whole element.
    // =================================================================

    private void DeleteSelectedRespectingSlice()
    {
        if (_doc.SelectionCount > 1)
        {
            int count = _doc.SelectionCount;
            PushUndo();
            _doc.RemoveSelected();
            StatusText.Text = $"Deleted {count} objects.";
            return;
        }

        var s = _doc.Selected;
        if (s == null) return;
        PushUndo();

        // A hidden slice (Space) is just a leftover marker: ignore it so Delete
        // removes the whole object, not the area where a slice once was.
        if (_sliceVisible && _activeSlice != null && !_activeSlice.IsEmpty && SliceCutService.IsAxisAligned(s))
        {
            var outer = s.Bounds;
            var inter = Rect.Intersect(outer, _activeSlice.Rect);
            if (!inter.IsEmpty && inter.Width >= 1 && inter.Height >= 1)
            {
                bool fullyCovered = _activeSlice.SelectionGeometry?.FillContains(new RectangleGeometry(outer)) == true ||
                    (_activeSlice.SelectionGeometry == null && inter.X <= outer.X + 0.5 && inter.Y <= outer.Y + 0.5
                    && inter.Right >= outer.Right - 0.5 && inter.Bottom >= outer.Bottom - 0.5);
                if (fullyCovered)
                {
                    _doc.RemoveSelected();
                    StatusText.Text = "Selection was fully inside the slice — removed.";
                    return;
                }

                if (s is ImageElement im)
                {
                    var r = SliceCutService.EraseImageRegion(im, _activeSlice);
                    if (r == PunchResult.Partial)
                    {
                        StatusText.Text = "Cut the slice area out of the image — Delete again removes the whole image.";
                        SelectionAdorner.InvalidateVisual();
                        return;
                    }
                }
                else if (s is ShapeElement sh)
                {
                    // One flat object out: bake fill/border to pixels, cut the hole.
                    int z = sh.ZIndex;
                    var flat = SliceCutService.CutShapeFlat(sh, _activeSlice);
                    _doc.RemoveElement(sh);
                    flat.ZIndex = z;
                    _doc.Elements.Add(flat);
                    _doc.NormalizeZ();
                    _doc.Select(flat);
                    StatusText.Text = "Cut the slice out — shape flattened to a single flat image (fill/border baked in).";
                    SelectionAdorner.InvalidateVisual();
                    return;
                }
            }
        }

        _doc.RemoveSelected();
    }

    /// <summary>
    /// Ctrl+X: copy (slice area or whole object, also to the OS clipboard) and
    /// then remove it — a visible slice over the selected object is punched out
    /// (exactly what Delete does), otherwise the whole object is cut.
    /// </summary>
    private void CutSelectionOrSlice()
    {
        if (_doc.SelectionCount > 1)
        {
            StatusText.Text = "Cut works on one object at a time; use Delete to remove the selected group.";
            return;
        }
        bool sliceActive = _sliceVisible && _activeSlice != null && !_activeSlice.IsEmpty;

        if (_doc.Selected is { } sel)
        {
            if (sliceActive)
            {
                var inter = SliceCutService.IsAxisAligned(sel)
                    ? Rect.Intersect(sel.Bounds, _activeSlice!.Rect)
                    : Rect.Empty;
                if (inter.IsEmpty || inter.Width < 1 || inter.Height < 1)
                {
                    StatusText.Text = "The slice doesn't overlap the selected object — nothing to cut.";
                    return;
                }
                CopySelectionOrSlice();          // slice area -> clipboard (in-app + OS)
                DeleteSelectedRespectingSlice(); // punch the area out of the object
                StatusText.Text = "Cut the slice area — Ctrl+V pastes it back, or paste into other apps.";
                return;
            }

            // No usable slice: cut the whole object.
            CopySelectionOrSlice();
            PushUndo();
            _doc.RemoveSelected();
            StatusText.Text = "Cut the object — Ctrl+V pastes it back, or paste into other apps.";
            return;
        }

        if (sliceActive)
        {
            // Nothing selected: nothing to remove, just copy the on-canvas slice area.
            CopySelectionOrSlice();
            StatusText.Text = "Copied the slice area (nothing selected to remove).";
            return;
        }

        StatusText.Text = "Nothing to cut — select an object, or draw a slice (M) first.";
    }

    private const string SaveFilter =
        "YorteImpact project (*.yimpact)|*.yimpact|" +
        "PNG image (*.png)|*.png|GIF image (*.gif)|*.gif|WebP image (*.webp)|*.webp";

    private void NewCanvas_Click(object sender, RoutedEventArgs e)
    {
        CommitTextEdit();
        var dlg = new NewCanvasDialog((int)_doc.ArtboardWidth, (int)_doc.ArtboardHeight,
            _doc.CanvasColor, _doc.CanvasTransparent) { Owner = this };
        if (dlg.ShowDialog() != true) return;

        var tab = new EditorTab { PrimaryColor = _primaryColor, Zoom = 0 };
        HookDocument(tab.Document);
        tab.Document.NewCanvas($"Untitled-{++_untitledCounter}", dlg.CanvasWidth, dlg.CanvasHeight,
            dlg.BackgroundColor, dlg.IsTransparent);
        _tabs.Add(tab);
        ActivateTab(_tabs.Count - 1);
        SetTool(ActiveTool.Pick);
        StatusText.Text = dlg.IsTransparent
            ? $"New transparent canvas tab {dlg.CanvasWidth}×{dlg.CanvasHeight}px."
            : $"New canvas tab {dlg.CanvasWidth}×{dlg.CanvasHeight}px, background {ToHex(dlg.BackgroundColor)}.";
    }

    private void Settings_Click(object sender, RoutedEventArgs e) => OpenSettings();

    private void OpenSettings(bool showIntegrations = false)
    {
        var dlg = new SettingsDialog(AppSettings.Load(), showIntegrations) { Owner = this };
        if (dlg.ShowDialog() != true) return;
        _settings.PixabayApiKey = dlg.Settings.PixabayApiKey ?? "";
        StatusText.Text =
            $"Settings saved — default canvas {dlg.Settings.DefaultCanvasWidth}×{dlg.Settings.DefaultCanvasHeight} px on next launch.";
    }
    private void OpenProject_Click(object sender, RoutedEventArgs e)
    {
        CommitTextEdit();
        var dlg = new OpenFileDialog { Filter = YorteProjectService.Filter };
        if (dlg.ShowDialog(this) != true) return;
        OpenProjectPath(dlg.FileName);
    }

    private static EditorTab LoadProjectTab(string path)
    {
        path = System.IO.Path.GetFullPath(path);
        var file = YorteProjectService.Load(path);
        var tab = new EditorTab { ProjectPath = path, Zoom = 0 };
        YorteProjectService.ApplyTo(file, tab.Document);
        if (TryParseColor(file.Primary, out var prim)) tab.PrimaryColor = prim;
        return tab;
    }

    private bool OpenProjectPath(string path)
    {
        CommitTextEdit();

        string fullPath;
        try
        {
            fullPath = System.IO.Path.GetFullPath(path);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, "Could not open project:\n" + ex.Message, "YorteImpact",
                MessageBoxButton.OK, MessageBoxImage.Error);
            return false;
        }

        int existing = _tabs.FindIndex(t =>
            string.Equals(t.ProjectPath, fullPath, StringComparison.OrdinalIgnoreCase));
        if (existing >= 0)
        {
            ActivateTab(existing);
            StatusText.Text = $"Switched to {System.IO.Path.GetFileName(fullPath)}.";
            return true;
        }

        try
        {
            var tab = LoadProjectTab(fullPath);
            HookDocument(tab.Document);
            _tabs.Add(tab);
            ActivateTab(_tabs.Count - 1);
            SetTool(ActiveTool.Pick);
            StatusText.Text = $"Opened {System.IO.Path.GetFileName(fullPath)} ({tab.Document.Elements.Count(e => !e.IsBackgroundLayer)} elements) in a new tab.";
            return true;
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, "Could not open project:\n" + ex.Message, "YorteImpact",
                MessageBoxButton.OK, MessageBoxImage.Error);
            return false;
        }
    }

    private void SaveProject_Click(object sender, RoutedEventArgs e) => SaveDocument(forceDialog: false);

    private void CopyAiBrief_Click(object sender, RoutedEventArgs e)
    {
        if (_tabs.Count == 0)
        {
            StatusText.Text = "No project open — create or open one first.";
            return;
        }

        try
        {
            string brief = AiBriefService.Build(_doc);
            Clipboard.SetText(brief);
            StatusText.Text = $"AI brief copied to the clipboard ({brief.Length:N0} characters).";
        }
        catch (Exception ex)
        {
            StatusText.Text = "Could not copy the AI brief: " + ex.Message;
        }
    }

    private void SaveProjectAs_Click(object sender, RoutedEventArgs e) => SaveDocument(forceDialog: true);

    /// <summary>
    /// Save / Save As. The dialog offers the editable .yimpact project plus the
    /// flat image formats (PNG / GIF / WebP); the choice is picked from the file
    /// extension, so "Save" can also produce a PNG when no project path exists.
    /// </summary>
    private void SaveDocument(bool forceDialog)
    {
        if (_tabs.Count == 0)
        {
            StatusText.Text = "No project open — create or open one first.";
            return;
        }
        try
        {
            string? path = forceDialog ? null : _projectPath;
            if (path == null)
            {
                var dlg = new SaveFileDialog
                {
                    Filter = SaveFilter,
                    FilterIndex = 1,
                    FileName = _doc.DocumentName + YorteProjectService.Extension,
                };
                if (dlg.ShowDialog(this) != true) return;
                path = dlg.FileName;
            }

            if (IsImagePath(path))
            {
                Pasteboard.UpdateLayout();
                var artboard = new Rect(_doc.ArtboardX, _doc.ArtboardY, _doc.ArtboardWidth, _doc.ArtboardHeight);
                SliceExportService.ExportCanvasRegion(ElementHost, artboard, ExportBackground, path, 96, 1.0);
                StatusText.Text = "Canvas exported to " + path;
                return;
            }

            YorteProjectService.Save(_doc, path, ToHex(_primaryColor));
            _projectPath = path;
            if (_activeTab >= 0 && _activeTab < _tabs.Count)
            {
                _tabs[_activeTab].ProjectPath = path;
                _tabs[_activeTab].IsDirty = false;
            }
            _doc.DocumentName = System.IO.Path.GetFileNameWithoutExtension(path);
            StatusText.Text = "Project saved to " + path;
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, "Could not save:\n" + ex.Message, "YorteImpact", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private static bool IsImagePath(string path) => System.IO.Path.GetExtension(path).ToLowerInvariant() is ".png" or ".gif" or ".webp";

    // =================================================================
    // Toolbar commands
    // =================================================================

    private void Pasteboard_DragOver(object sender, DragEventArgs e)
    {
        e.Effects = HasSupportedFileDrop(e) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private void Pasteboard_Drop(object sender, DragEventArgs e)
    {
        if (!e.Data.GetDataPresent(DataFormats.FileDrop)) return;
        TabDropPlaceholder.Visibility = Visibility.Collapsed;
        CommitTextEdit();
        var files = (string[])e.Data.GetData(DataFormats.FileDrop);

        var images = files.Where(IsSupportedImageFile).ToList();
        if (images.Count > 0)
        {
            if (_tabs.Count == 0) OpenDroppedFiles(images);
            else AddImagesToCanvas(images, e.GetPosition(Pasteboard));
        }

        foreach (var f in files.Where(IsProjectFile))
            OpenProjectPath(f);

        e.Handled = true;
    }

    private void AddImagesToCanvas(IReadOnlyList<string> files, Point pos)
    {
        bool undoPushed = false;
        int count = 0;
        foreach (var f in files)
        {
            if (!TryAddImageFile(f, pos, ref undoPushed)) continue;
            pos.Offset(24, 24);
            count++;
        }

        if (count == 0)
        {
            StatusText.Text = "Could not add the dropped image.";
            return;
        }

        StatusText.Text = $"Added {count} image{(count == 1 ? "" : "s")} to the canvas.";
        NoteIfOutsideCanvas();
    }

    private bool TryAddImageFile(string f, Point pos, ref bool undoPushed)
    {
        if (!IsSupportedImageFile(f)) return false;
        try
        {
            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.UriSource = new Uri(f, UriKind.Absolute);
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            bmp.EndInit();
            bmp.Freeze();

            var el = new ImageElement { SourcePath = f };
            el.SetBitmap(bmp);
            PlaceImageOnCanvas(el, pos);
            if (!undoPushed) { PushUndo(); undoPushed = true; }
            _doc.Add(el);
            return true;
        }
        catch { return false; }
    }

    private static bool IsSupportedImageFile(string f) =>
        System.IO.Path.GetExtension(f).ToLowerInvariant() is
            ".png" or ".jpg" or ".jpeg" or ".bmp" or ".gif" or ".webp" or ".tif" or ".tiff";

    private static bool IsProjectFile(string f) =>
        string.Equals(System.IO.Path.GetExtension(f), ".yimpact", StringComparison.OrdinalIgnoreCase);

    private static bool HasSupportedFileDrop(DragEventArgs e) =>
        e.Data.GetDataPresent(DataFormats.FileDrop) &&
        e.Data.GetData(DataFormats.FileDrop) is string[] files &&
        files.Any(f => IsSupportedImageFile(f) || IsProjectFile(f));

    private void OpenDroppedFiles(IEnumerable<string> files)
    {
        int images = 0, projects = 0;
        string? lastName = null;
        int lastW = 0, lastH = 0;

        foreach (var f in files)
        {
            if (IsProjectFile(f))
            {
                if (OpenProjectPath(f)) projects++;
            }
            else if (IsSupportedImageFile(f))
            {
                if (!OpenImageAsProject(f, out int w, out int h)) continue;
                images++;
                lastName = System.IO.Path.GetFileNameWithoutExtension(f);
                lastW = w;
                lastH = h;
            }
        }

        int count = images + projects;
        if (count == 0)
        {
            StatusText.Text = "No supported file was opened. Drop images or .yimpact projects.";
            return;
        }

        if (count == 1 && images == 1)
        {
            StatusText.Text = $"Opened \"{lastName}\" in a new project exactly {lastW}×{lastH} px.";
        }
        else if (count > 1)
        {
            StatusText.Text = $"Opened {count} files in separate tabs ({projects} project{(projects == 1 ? "" : "s")}, {images} image{(images == 1 ? "" : "s")}).";
        }
    }

    private bool OpenImageAsProject(string f, out int width, out int height)
    {
        width = 0;
        height = 0;
        try
        {
            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.UriSource = new Uri(f, UriKind.Absolute);
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            bmp.EndInit();
            bmp.Freeze();

            width = bmp.PixelWidth;
            height = bmp.PixelHeight;
            if (width <= 0 || height <= 0) return false;

            var name = System.IO.Path.GetFileNameWithoutExtension(f);
            if (string.IsNullOrWhiteSpace(name)) name = "Image";

            var tab = new EditorTab { PrimaryColor = _primaryColor, Zoom = 0 };
            HookDocument(tab.Document);
            tab.Document.NewCanvas(name, width, height, Colors.Transparent, true);

            var el = new ImageElement { SourcePath = f };
            el.SetBitmap(bmp, keepSize: true);
            el.Width = width;
            el.Height = height;
            el.X = tab.Document.ArtboardX;
            el.Y = tab.Document.ArtboardY;
            tab.Document.Add(el);

            _tabs.Add(tab);
            ActivateTab(_tabs.Count - 1);
            SetTool(ActiveTool.Pick);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private void EmptyState_DragOver(object sender, DragEventArgs e)
    {
        e.Effects = HasSupportedFileDrop(e) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private void EmptyState_Drop(object sender, DragEventArgs e)
    {
        TabDropPlaceholder.Visibility = Visibility.Collapsed;
        if (!e.Data.GetDataPresent(DataFormats.FileDrop)) return;
        var files = (string[])e.Data.GetData(DataFormats.FileDrop);
        OpenDroppedFiles(files);
        e.Handled = true;
    }

    private void TabDropArea_DragOver(object sender, DragEventArgs e)
    {
        if (!HasSupportedFileDrop(e))
        {
            TabDropPlaceholder.Visibility = Visibility.Collapsed;
            e.Effects = DragDropEffects.None;
            e.Handled = true;
            return;
        }

        e.Effects = DragDropEffects.Copy;
        TabDropHintText.Text = TabDropText(e);
        if (TabDropPlaceholder.Visibility != Visibility.Visible)
            TabDropPlaceholder.Visibility = Visibility.Visible;
        e.Handled = true;
    }

    private void TabDropArea_DragLeave(object sender, DragEventArgs e)
    {
        var p = e.GetPosition(TabDropArea);
        if (p.X < 0 || p.Y < 0 || p.X >= TabDropArea.ActualWidth || p.Y >= TabDropArea.ActualHeight)
            TabDropPlaceholder.Visibility = Visibility.Collapsed;
    }

    private void TabDropArea_Drop(object sender, DragEventArgs e)
    {
        TabDropPlaceholder.Visibility = Visibility.Collapsed;
        if (!e.Data.GetDataPresent(DataFormats.FileDrop)) return;
        var files = (string[])e.Data.GetData(DataFormats.FileDrop);
        OpenDroppedFiles(files);
        e.Handled = true;
    }

    private static string TabDropText(DragEventArgs e)
    {
        if (e.Data.GetData(DataFormats.FileDrop) is string[] files)
        {
            bool images = files.Any(IsSupportedImageFile);
            bool projects = files.Any(IsProjectFile);
            if (projects && !images) return "Open project";
            if (images && !projects) return "Open as new project";
        }
        return "Open files";
    }

    private void FlipH_Click(object sender, RoutedEventArgs e)
    {
        if (_doc.Selected == null) return;
        PushUndo();
        _doc.Selected.FlipHorizontal = !_doc.Selected.FlipHorizontal;
    }

    private void FlipV_Click(object sender, RoutedEventArgs e)
    {
        if (_doc.Selected == null) return;
        PushUndo();
        _doc.Selected.FlipVertical = !_doc.Selected.FlipVertical;
    }

    private void RotateLeft90_Click(object sender, RoutedEventArgs e) => RotateSelection90(-90);
    private void RotateRight90_Click(object sender, RoutedEventArgs e) => RotateSelection90(90);

    private void RotateSelection90(double delta)
    {
        if (_doc.Selected is not { } s) return;
        PushUndo();
        double a = ((s.RotationAngle + delta) % 360 + 360) % 360;
        if (a > 180) a -= 360; // keep the inspector's -180..180 range
        s.RotationAngle = a;
        RefreshInspector();
        StatusText.Text = delta > 0 ? "Rotated 90° right." : "Rotated 90° left.";
    }

    // =================================================================
    // Attribute toolbar (Pick tool: object transparency / corner / align)
    // =================================================================

    private void AttrOpacity_ValueChanged(object sender, EventArgs e)
    {
        if (_suppressInspector || _doc.Selected is not { } el) return;
        ArmInspectorUndo();
        el.Opacity = AttrOpacity.Value;
    }

    private void AttrRatioLock_Click(object sender, RoutedEventArgs e)
    {
        SetAspectLock(AttrRatioLockBtn.IsChecked == true);
        StatusText.Text = _lockAspectRatio
            ? "Aspect ratio locked while resizing."
            : "Aspect ratio unlocked.";
    }

    private void InspRatioLock_Click(object sender, RoutedEventArgs e)
    {
        SetAspectLock(InspRatioLockBtn.IsChecked == true);
        StatusText.Text = _lockAspectRatio
            ? "Aspect ratio locked while resizing."
            : "Aspect ratio unlocked.";
    }

    private void SetAspectLock(bool locked)
    {
        _lockAspectRatio = locked;
        AttrRatioLockBtn.IsChecked = locked;
        InspRatioLockBtn.IsChecked = locked;
        UpdateRatioLockIcon();
    }

    private void UpdateRatioLockIcon()
    {
        if (RatioLockIcon == null) return;
        RatioLockIcon.Visibility = _lockAspectRatio ? Visibility.Visible : Visibility.Collapsed;
        RatioUnlockIcon.Visibility = _lockAspectRatio ? Visibility.Collapsed : Visibility.Visible;
        if (InspRatioLockIcon == null) return;
        InspRatioLockIcon.Visibility = _lockAspectRatio ? Visibility.Visible : Visibility.Collapsed;
        InspRatioUnlockIcon.Visibility = _lockAspectRatio ? Visibility.Collapsed : Visibility.Visible;
    }

    private void AttrDrawRatioLock_Click(object sender, RoutedEventArgs e)
    {
        _lockDrawRatio = AttrDrawRatioLockBtn.IsChecked == true;
        UpdateDrawRatioLockIcon();
        StatusText.Text = _lockDrawRatio
            ? "Drawing ratio locked — width and height stay equal."
            : "Drawing ratio unlocked.";
    }

    private void UpdateDrawRatioLockIcon()
    {
        if (DrawRatioLockIcon == null) return;
        DrawRatioLockIcon.Visibility = _lockDrawRatio ? Visibility.Visible : Visibility.Collapsed;
        DrawRatioUnlockIcon.Visibility = _lockDrawRatio ? Visibility.Collapsed : Visibility.Visible;
    }

    private void AttrCorner_ValueChanged(object sender, EventArgs e)
    {
        if (_suppressInspector || _doc.Selected is not { } el) return;
        if (el.UseIndividualCornerRadii) return;
        ArmInspectorUndo();
        if (el is ShapeElement sh) sh.CornerRadius = AttrCorner.Value;
        else if (el is ImageElement im) im.CornerRadius = AttrCorner.Value;
    }

    private static double GetUniformCornerRadius(DesignElement element) => element switch
    {
        ShapeElement shape => shape.CornerRadius,
        ImageElement image => image.CornerRadius,
        _ => 0,
    };

    private static CornerRadius SetCornerValue(CornerRadius current, string corner, double value) => corner switch
    {
        "TopLeft" => new CornerRadius(value, current.TopRight, current.BottomRight, current.BottomLeft),
        "TopRight" => new CornerRadius(current.TopLeft, value, current.BottomRight, current.BottomLeft),
        "BottomRight" => new CornerRadius(current.TopLeft, current.TopRight, value, current.BottomLeft),
        "BottomLeft" => new CornerRadius(current.TopLeft, current.TopRight, current.BottomRight, value),
        _ => current,
    };

    private void AttrCornerIndividual_Toggled(object sender, RoutedEventArgs e)
    {
        if (_suppressInspector || _doc.Selected is not { } element) return;
        ArmInspectorUndo();
        bool enabled = AttrCornerIndividualCheck.IsChecked == true;
        if (enabled && !element.UseIndividualCornerRadii)
            element.IndividualCornerRadii = new CornerRadius(GetUniformCornerRadius(element));
        element.UseIndividualCornerRadii = enabled;
        RefreshInspector();
        RefreshShapeToolbar();
    }

    private void AttrCornerIndividual_ValueChanged(object? sender, EventArgs e)
    {
        if (_suppressInspector || sender is not ValueSlider slider || slider.Tag is not string corner
            || _doc.Selected is not { } element || !element.UseIndividualCornerRadii) return;
        ArmInspectorUndo();
        element.IndividualCornerRadii = SetCornerValue(element.IndividualCornerRadii, corner, slider.Value);
        RefreshInspector();
        RefreshShapeToolbar();
    }

    private void AttrSoft_ValueChanged(object sender, EventArgs e)
    {
        if (_suppressInspector || _doc.Selected is not { } el) return;
        ArmInspectorUndo();
        el.SoftEdge = AttrSoft.Value;
    }

    // ---------- line and arrow tool ----------

    private LineElement? SelectedLine => _doc.Selected as LineElement;

    private void AttrLineMode_Click(object sender, RoutedEventArgs e)
    {
        if (_suppressLineToolbar || sender is not ToggleButton { Tag: string tag }
            || !Enum.TryParse<LineToolMode>(tag, true, out var mode)) return;

        if (_lineDraft.Count > 0 && mode != _lineMode) CancelLineDraft();
        _lineMode = mode;
        _settings.Line.Mode = mode.ToString();
        RefreshLineToolbar();
    }

    private void AttrLinePattern_Click(object sender, RoutedEventArgs e)
    {
        if (_suppressLineToolbar || sender is not ToggleButton { Tag: string tag }
            || !Enum.TryParse<LinePattern>(tag, true, out var pattern)) return;
        var target = SelectedLine;
        if (target != null && target.Pattern != pattern)
        {
            PushUndo();
            target.Pattern = pattern;
        }
        _settings.Line.Pattern = pattern.ToString();
        RefreshLineToolbar();
    }

    private void AttrLineCap_Click(object sender, RoutedEventArgs e)
    {
        if (_suppressLineToolbar || sender is not ToggleButton { Tag: string tag }) return;
        var parts = tag.Split(':', 2);
        if (parts.Length != 2 || !Enum.TryParse<LineEndpointStyle>(parts[1], true, out var cap)) return;

        var target = SelectedLine;
        bool isStart = parts[0] == "Start";
        if (target != null)
        {
            var current = isStart ? target.StartCap : target.EndCap;
            if (current != cap)
            {
                PushUndo();
                if (isStart) target.StartCap = cap;
                else target.EndCap = cap;
            }
        }
        if (isStart) _settings.Line.StartCap = cap.ToString();
        else _settings.Line.EndCap = cap.ToString();
        RefreshLineToolbar();
    }

    private void AttrLineColor_Click(object sender, RoutedEventArgs e)
    {
        var target = SelectedLine;
        var current = target?.StrokeColor ?? ReadLineColor(_settings.Line.Color);
        var dialog = new ColorPickerDialog(current, CanvasColorsWithPrimary()) { Owner = this };
        if (dialog.ShowDialog() != true) return;
        if (target != null && target.StrokeColor != dialog.SelectedColor)
        {
            PushUndo();
            target.StrokeColor = dialog.SelectedColor;
        }
        _settings.Line.Color = ToHex(dialog.SelectedColor);
        RefreshLineToolbar();
        StatusText.Text = $"Line color set to {ToHex(dialog.SelectedColor)}.";
    }

    private void AttrLineThickness_ValueChanged(object? sender, EventArgs e)
    {
        if (_suppressLineToolbar) return;
        var target = SelectedLine;
        if (target != null)
        {
            if (Math.Abs(target.StrokeThickness - AttrLineThickness.Value) < 0.01) return;
            ArmInspectorUndo();
            target.StrokeThickness = AttrLineThickness.Value;
        }
        _settings.Line.Thickness = AttrLineThickness.Value;
        RefreshLineToolbar();
    }

    private void AttrLineCurve_ValueChanged(object? sender, EventArgs e)
    {
        if (_suppressLineToolbar) return;
        var target = SelectedLine;
        if (target is { IsFreehand: true })
        {
            if (Math.Abs(target.CurveStrength - AttrLineCurve.Value) < 0.01) return;
            ArmInspectorUndo();
            target.CurveStrength = AttrLineCurve.Value;
        }
        _settings.Line.CurveStrength = AttrLineCurve.Value;
        RefreshLineToolbar();
    }

    private void AttrLineShadow_Toggled(object sender, RoutedEventArgs e)
    {
        if (_suppressLineToolbar) return;
        bool enabled = AttrLineShadowCheck.IsChecked == true;
        var target = SelectedLine;
        if (target != null && (target.ShadowStyle != ShadowStyle.None) != enabled)
        {
            PushUndo();
            if (enabled)
            {
                target.ApplyShadowPreset(ShadowStyle.Medium);
                target.ShadowColor = ReadLineColor(_settings.Line.ShadowColor);
            }
            else target.ShadowStyle = ShadowStyle.None;
        }
        _settings.Line.Shadow = enabled;
        if (target != null) RefreshInspector();
        RefreshLineToolbar();
    }

    private void AttrLineShadowColor_Click(object sender, RoutedEventArgs e)
    {
        var target = SelectedLine;
        var current = target?.ShadowColor ?? ReadLineColor(_settings.Line.ShadowColor);
        var dialog = new ColorPickerDialog(current, CanvasColorsWithPrimary()) { Owner = this };
        if (dialog.ShowDialog() != true) return;
        if (target != null && target.ShadowColor != dialog.SelectedColor)
        {
            PushUndo();
            target.ShadowColor = dialog.SelectedColor;
        }
        _settings.Line.ShadowColor = ToHex(dialog.SelectedColor);
        if (target != null) RefreshInspector();
        RefreshLineToolbar();
    }

    private void RefreshLineToolbar()
    {
        if (AttrLinePanel == null) return;
        _suppressLineToolbar = true;
        try
        {
            var line = SelectedLine;
            var defaults = _settings.Line;
            var pattern = line?.Pattern ?? ParseEnum(defaults.Pattern, LinePattern.Solid);
            var startCap = line?.StartCap ?? ParseLineEndpointStyle(defaults.StartCap);
            var endCap = line?.EndCap ?? ParseLineEndpointStyle(defaults.EndCap);
            var color = line?.StrokeColor ?? ReadLineColor(defaults.Color);
            AttrLineModeLineBtn.IsChecked = _lineMode == LineToolMode.Line;
            AttrLineModePolylineBtn.IsChecked = _lineMode == LineToolMode.Polyline;
            AttrLineModeFreehandBtn.IsChecked = _lineMode == LineToolMode.Freehand;
            AttrLineColorBtn.Background = new SolidColorBrush(color);
            AttrLineThickness.SetValue(line?.StrokeThickness ?? defaults.Thickness);
            AttrLineSolidBtn.IsChecked = pattern == LinePattern.Solid;
            AttrLineDashedBtn.IsChecked = pattern == LinePattern.Dashed;
            AttrLineDottedBtn.IsChecked = pattern == LinePattern.Dotted;
            AttrLineStartFlatBtn.IsChecked = startCap == LineEndpointStyle.Flat;
            AttrLineStartArrowBtn.IsChecked = startCap == LineEndpointStyle.OpenArrow;
            AttrLineStartFilledArrowBtn.IsChecked = startCap == LineEndpointStyle.FilledTriangle;
            AttrLineStartRoundBtn.IsChecked = startCap == LineEndpointStyle.Round;
            AttrLineStartSquareBtn.IsChecked = startCap == LineEndpointStyle.Square;
            AttrLineEndFlatBtn.IsChecked = endCap == LineEndpointStyle.Flat;
            AttrLineEndArrowBtn.IsChecked = endCap == LineEndpointStyle.OpenArrow;
            AttrLineEndFilledArrowBtn.IsChecked = endCap == LineEndpointStyle.FilledTriangle;
            AttrLineEndRoundBtn.IsChecked = endCap == LineEndpointStyle.Round;
            AttrLineEndSquareBtn.IsChecked = endCap == LineEndpointStyle.Square;
            AttrLineCurve.SetValue(line is { IsFreehand: true } ? line.CurveStrength : defaults.CurveStrength);
            bool showCurve = _lineMode == LineToolMode.Freehand || line?.IsFreehand == true;
            AttrLineCurve.Visibility = showCurve ? Visibility.Visible : Visibility.Collapsed;
            AttrLineCurve.IsEnabled = line == null || line.IsFreehand || _lineMode == LineToolMode.Freehand;
            AttrLineShadowCheck.IsChecked = (line?.ShadowStyle != ShadowStyle.None) && line != null
                || (line == null && defaults.Shadow);
            AttrLineShadowColorBtn.Background = new SolidColorBrush(
                line?.ShadowColor ?? ReadLineColor(defaults.ShadowColor));
        }
        finally
        {
            _suppressLineToolbar = false;
        }
    }

    private static Color ReadLineColor(string? value) =>
        TryParseColor(value, out var color) ? color : Color.FromRgb(0x2E, 0x9B, 0xFF);

    private void CaptureLineDefaults(LineElement line, bool captureMode = false)
    {
        var defaults = _settings.Line;
        defaults.Color = ToHex(line.StrokeColor);
        if (captureMode)
        {
            defaults.Mode = line.IsFreehand
                ? LineToolMode.Freehand.ToString()
                : (line.IsPolyline || line.PathPoints.Count > 2 ? LineToolMode.Polyline : LineToolMode.Line).ToString();
            _lineMode = ParseLineToolMode(defaults.Mode);
        }
        defaults.Thickness = line.StrokeThickness;
        defaults.Pattern = line.Pattern.ToString();
        defaults.StartCap = line.StartCap.ToString();
        defaults.EndCap = line.EndCap.ToString();
        defaults.CurveStrength = line.CurveStrength;
        defaults.Shadow = line.ShadowStyle != ShadowStyle.None;
        defaults.ShadowColor = ToHex(line.ShadowColor);
    }

    private void PersistLineDefaults()
    {
        var fresh = AppSettings.Load();
        fresh.Line = _settings.Line;
        fresh.Save();
    }

    private static bool IsLineStyleProperty(string? name) => name is
        nameof(LineElement.StrokeColor) or nameof(LineElement.StrokeThickness)
        or nameof(LineElement.Pattern) or nameof(LineElement.StartCap)
        or nameof(LineElement.EndCap) or nameof(LineElement.IsFreehand) or nameof(LineElement.IsPolyline)
        or nameof(LineElement.CurveStrength) or nameof(DesignElement.ShadowStyle)
        or nameof(DesignElement.ShadowX) or nameof(DesignElement.ShadowY)
        or nameof(DesignElement.ShadowBlur) or nameof(DesignElement.ShadowOpacity)
        or nameof(DesignElement.ShadowColor);

    // ---------- shape tool bar ----------

    private ShapeElement ShapeDefaultsPreview() => CreateShapeFromDefaults(new Rect(0, 0, 160, 120));

    private static List<Point> DefaultPolygonPoints() => new()
    {
        new Point(0.5, 0), new Point(1, 0.38), new Point(0.82, 1),
        new Point(0.18, 1), new Point(0, 0.38),
    };

    private void AttrShapeKind_Click(object sender, RoutedEventArgs e)
    {
        if (_suppressShapeToolbar || sender is not ToggleButton { Tag: string tag }) return;
        if (!Enum.TryParse<ShapeKind>(tag, out var kind)) return;

        var target = SelectedShape;
        if (target != null)
        {
            if (target.Kind != kind)
            {
                PushUndo();
                target.Kind = kind;
                if (kind == ShapeKind.Polygon && target.PolygonPoints.Count < 3)
                    target.SetPolygonPoints(DefaultPolygonPoints());
                RefreshInspector();
            }
        }
        else
        {
            _settings.Shape.ShapeKind = kind.ToString();
            if (kind != ShapeKind.Polygon) CancelPolygonDraft();
        }
        RefreshShapeToolbar();
    }

    private void RefreshShapeToolbar()
    {
        if (AttrRectPanel == null) return;
        _suppressShapeToolbar = true;
        try
        {
            var src = SelectedShape ?? ShapeDefaultsPreview();
            AttrShapeRectBtn.IsChecked = src.Kind == ShapeKind.Rectangle;
            AttrShapeEllipseBtn.IsChecked = src.Kind == ShapeKind.Ellipse;
            AttrShapeTriangleBtn.IsChecked = src.Kind == ShapeKind.Triangle;
            AttrShapePolygonBtn.IsChecked = src.Kind == ShapeKind.Polygon;
            bool rectangle = src.Kind == ShapeKind.Rectangle;
            AttrShapeCorner.IsEnabled = rectangle && !src.UseIndividualCornerRadii;
            AttrShapeCorner.Opacity = rectangle ? 1.0 : 0.35;
            AttrShapeIndividualCheck.IsChecked = src.UseIndividualCornerRadii;
            AttrShapeIndividualCheck.IsEnabled = rectangle;
            AttrShapeIndividualPanel.Visibility = rectangle && src.UseIndividualCornerRadii
                ? Visibility.Visible : Visibility.Collapsed;
            AttrShapeTopLeft.SetValue(src.IndividualCornerRadii.TopLeft);
            AttrShapeTopRight.SetValue(src.IndividualCornerRadii.TopRight);
            AttrShapeBottomRight.SetValue(src.IndividualCornerRadii.BottomRight);
            AttrShapeBottomLeft.SetValue(src.IndividualCornerRadii.BottomLeft);
            AttrShapeFill1Btn.Background = new SolidColorBrush(src.FillColor1);
            AttrShapeFill2Btn.Background = new SolidColorBrush(src.FillColor2);
            AttrShapeFill2Btn.Visibility = src.IsGradient ? Visibility.Visible : Visibility.Collapsed;
            AttrShapeFillModeCombo.SelectedIndex = src.IsGradient ? 1 : 0;
            AttrShapeCorner.SetValue(src.CornerRadius);
            AttrShapeBorderCheck.IsChecked = src.StrokeThickness > 0.01;
            AttrShapeBorderColorBtn.Background = new SolidColorBrush(src.StrokeColor);
            AttrShapeBorderThickness.SetValue(src.StrokeThickness);
            AttrShapeShadowCheck.IsChecked = src.ShadowStyle != ShadowStyle.None;
            AttrShapeShadowColorBtn.Background = new SolidColorBrush(src.ShadowColor);
        }
        finally
        {
            _suppressShapeToolbar = false;
        }
    }

    private void AttrShapeFill1_Click(object sender, RoutedEventArgs e)
    {
        var target = SelectedShape;
        var src = target ?? ShapeDefaultsPreview();
        var dlg = new ColorPickerDialog(src.FillColor1, CanvasColorsWithPrimary()) { Owner = this };
        if (dlg.ShowDialog() != true) return;
        if (target != null)
        {
            PushUndo();
            target.FillColor1 = dlg.SelectedColor;
            RefreshInspector();
        }
        else
        {
            _settings.Shape.Fill1 = ToHex(dlg.SelectedColor);
        }
        RefreshShapeToolbar();
    }

    private void AttrShapeFill2_Click(object sender, RoutedEventArgs e)
    {
        var target = SelectedShape;
        var src = target ?? ShapeDefaultsPreview();
        var dlg = new ColorPickerDialog(src.FillColor2, CanvasColorsWithPrimary()) { Owner = this };
        if (dlg.ShowDialog() != true) return;
        if (target != null)
        {
            PushUndo();
            target.FillColor2 = dlg.SelectedColor;
            RefreshInspector();
        }
        else
        {
            _settings.Shape.Fill2 = ToHex(dlg.SelectedColor);
        }
        RefreshShapeToolbar();
    }

    private void AttrShapeFillMode_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressShapeToolbar) return;
        bool gradient = AttrShapeFillModeCombo.SelectedIndex == 1;
        var target = SelectedShape;
        if (target != null)
        {
            if (target.IsGradient == gradient) return;
            PushUndo();
            target.IsGradient = gradient;
            RefreshInspector();
        }
        else
        {
            _settings.Shape.IsGradient = gradient;
        }
        RefreshShapeToolbar();
    }

    private void AttrShapeCorner_ValueChanged(object? sender, EventArgs e)
    {
        if (_suppressShapeToolbar) return;
        var target = SelectedShape;
        if (target?.UseIndividualCornerRadii == true || (target == null && _settings.Shape.IndividualCorners)) return;
        if (target != null)
        {
            ArmInspectorUndo();
            target.CornerRadius = AttrShapeCorner.Value;
            RefreshInspector();
        }
        else
        {
            _settings.Shape.CornerRadius = AttrShapeCorner.Value;
        }
    }

    private void AttrShapeIndividual_Toggled(object sender, RoutedEventArgs e)
    {
        if (_suppressShapeToolbar) return;
        bool enabled = AttrShapeIndividualCheck.IsChecked == true;
        var target = SelectedShape;
        if (target != null)
        {
            ArmInspectorUndo();
            if (enabled && !target.UseIndividualCornerRadii)
                target.IndividualCornerRadii = new CornerRadius(target.CornerRadius);
            target.UseIndividualCornerRadii = enabled;
            RefreshInspector();
        }
        else
        {
            if (enabled && !_settings.Shape.IndividualCorners)
            {
                double radius = _settings.Shape.CornerRadius;
                _settings.Shape.CornerTopLeft = radius;
                _settings.Shape.CornerTopRight = radius;
                _settings.Shape.CornerBottomRight = radius;
                _settings.Shape.CornerBottomLeft = radius;
            }
            _settings.Shape.IndividualCorners = enabled;
        }
        RefreshShapeToolbar();
    }

    private void AttrShapeIndividual_ValueChanged(object? sender, EventArgs e)
    {
        if (_suppressShapeToolbar || sender is not ValueSlider slider || slider.Tag is not string corner) return;
        var target = SelectedShape;
        if (target != null)
        {
            if (!target.UseIndividualCornerRadii) return;
            ArmInspectorUndo();
            target.IndividualCornerRadii = SetCornerValue(target.IndividualCornerRadii, corner, slider.Value);
            RefreshInspector();
        }
        else
        {
            if (!_settings.Shape.IndividualCorners) return;
            var radii = new CornerRadius(_settings.Shape.CornerTopLeft, _settings.Shape.CornerTopRight,
                _settings.Shape.CornerBottomRight, _settings.Shape.CornerBottomLeft);
            radii = SetCornerValue(radii, corner, slider.Value);
            _settings.Shape.CornerTopLeft = radii.TopLeft;
            _settings.Shape.CornerTopRight = radii.TopRight;
            _settings.Shape.CornerBottomRight = radii.BottomRight;
            _settings.Shape.CornerBottomLeft = radii.BottomLeft;
        }
        RefreshShapeToolbar();
    }

    private void AttrShapeBorder_Toggled(object sender, RoutedEventArgs e)
    {
        if (_suppressShapeToolbar) return;
        bool on = AttrShapeBorderCheck.IsChecked == true;
        var target = SelectedShape;
        if (target != null)
        {
            PushUndo();
            target.StrokeThickness = on
                ? (target.StrokeThickness > 0.01 ? target.StrokeThickness : 2)
                : 0;
            RefreshInspector();
        }
        else
        {
            _settings.Shape.StrokeThickness = on
                ? (_settings.Shape.StrokeThickness > 0.01 ? _settings.Shape.StrokeThickness : 2)
                : 0;
        }
        RefreshShapeToolbar();
    }

    private void AttrShapeBorderColor_Click(object sender, RoutedEventArgs e)
    {
        var target = SelectedShape;
        var src = target ?? ShapeDefaultsPreview();
        var dlg = new ColorPickerDialog(src.StrokeColor, CanvasColorsWithPrimary()) { Owner = this };
        if (dlg.ShowDialog() != true) return;
        if (target != null)
        {
            PushUndo();
            target.StrokeColor = dlg.SelectedColor;
            RefreshInspector();
        }
        else
        {
            _settings.Shape.Stroke = ToHex(dlg.SelectedColor);
        }
        RefreshShapeToolbar();
    }

    private void AttrShapeBorderThickness_ValueChanged(object? sender, EventArgs e)
    {
        if (_suppressShapeToolbar) return;
        var target = SelectedShape;
        if (target != null)
        {
            ArmInspectorUndo();
            target.StrokeThickness = AttrShapeBorderThickness.Value;
            RefreshInspector();
        }
        else
        {
            _settings.Shape.StrokeThickness = AttrShapeBorderThickness.Value;
        }
        RefreshShapeToolbar();
    }

    private void AttrShapeShadow_Toggled(object sender, RoutedEventArgs e)
    {
        if (_suppressShapeToolbar) return;
        bool on = AttrShapeShadowCheck.IsChecked == true;
        var target = SelectedShape;
        if (target != null)
        {
            PushUndo();
            if (on) target.ApplyShadowPreset(ShadowStyle.Medium);
            else target.ShadowStyle = ShadowStyle.None;
            RefreshInspector();
        }
        else if (on)
        {
            _settings.Shape.ShadowStyle = ShadowStyle.Medium.ToString();
            _settings.Shape.ShadowX = 4;
            _settings.Shape.ShadowY = 8;
            _settings.Shape.ShadowBlur = 14;
            _settings.Shape.ShadowOpacity = 35;
            _settings.Shape.ShadowColor = "#000000";
        }
        else
        {
            _settings.Shape.ShadowStyle = ShadowStyle.None.ToString();
        }
        RefreshShapeToolbar();
    }

    private void AttrShapeShadowColor_Click(object sender, RoutedEventArgs e)
    {
        var target = SelectedShape;
        var src = target ?? ShapeDefaultsPreview();
        var dlg = new ColorPickerDialog(src.ShadowColor, CanvasColorsWithPrimary()) { Owner = this };
        if (dlg.ShowDialog() != true) return;
        if (target != null)
        {
            PushUndo();
            target.ShadowColor = dlg.SelectedColor;
            RefreshInspector();
        }
        else
        {
            _settings.Shape.ShadowColor = ToHex(dlg.SelectedColor);
        }
        RefreshShapeToolbar();
    }

    // ---------- text tool bar ----------

    private TextElement? SelectedText => _doc.Selected as TextElement;

    private void RefreshTextToolbar()
    {
        if (AttrTextPanel == null) return;
        _suppressTextToolbar = true;
        try
        {
            var src = SelectedText ?? _textDefaults;
            AttrTextColorBtn.Background = new SolidColorBrush(src.TextColor1);
            AttrTextFontCombo.SelectedItem = src.FontFamilyName;
            AttrTextSize.SetValue(src.FontSize);
            AttrTextBoldBtn.IsChecked = src.IsBold;
            AttrTextItalicBtn.IsChecked = src.IsItalic;
            AttrTextUnderlineBtn.IsChecked = src.IsUnderline;
            AttrTextStrikeBtn.IsChecked = src.IsStrikethrough;
            AttrTextSpacing.SetValue(src.LetterSpacing);
            AttrTextBorderCheck.IsChecked = src.HasBorder;
            AttrTextBorderColorBtn.Background = new SolidColorBrush(src.BorderColor);
            AttrTextBorderThickness.SetValue(src.BorderThickness);
            AttrTextShadowCheck.IsChecked = src.ShadowStyle != ShadowStyle.None;
            AttrTextShadowColorBtn.Background = new SolidColorBrush(src.ShadowColor);
            UpdateAttrTextAlignIcon(src.Alignment);
        }
        finally
        {
            _suppressTextToolbar = false;
        }
    }

    private void UpdateAttrTextAlignIcon(TextAlign alignment)
    {
        AttrTextAlignIcon.Data = Geometry.Parse(alignment switch
        {
            TextAlign.Left => "M3,5 H21 M3,10 H14 M3,15 H21 M3,20 H11",
            TextAlign.Center => "M3,5 H21 M6,10 H18 M3,15 H21 M7,20 H17",
            _ => "M3,5 H21 M10,10 H21 M3,15 H21 M13,20 H21",
        });
        AttrTextAlignBtn.ToolTip = $"Alignment: {alignment} (click to change)";
    }

    /// <summary>Remember the selected text's look as the default for the next text.</summary>
    private void CaptureTextDefaults(TextElement te)
    {
        te.CopyStyleTo(_textDefaults);
    }

    private void ApplyTextDefaults(TextDefaults d)
    {
        _textDefaults.FontFamilyName = d.FontFamilyName;
        _textDefaults.FontSize = d.FontSize;
        if (TryParseColor(d.Color1, out var c1)) _textDefaults.TextColor1 = c1;
        if (TryParseColor(d.Color2, out var c2)) _textDefaults.TextColor2 = c2;
        _textDefaults.IsGradient = d.IsGradient;
        _textDefaults.TextGradientAngle = d.GradientAngle;
        _textDefaults.IsBold = d.IsBold;
        _textDefaults.IsItalic = d.IsItalic;
        _textDefaults.IsUnderline = d.IsUnderline;
        _textDefaults.IsStrikethrough = d.IsStrikethrough;
        _textDefaults.HasBorder = d.HasBorder;
        if (TryParseColor(d.BorderColor, out var bc)) _textDefaults.BorderColor = bc;
        _textDefaults.BorderThickness = d.BorderThickness;
        _textDefaults.Direction = ParseEnum(d.Direction, TextDirection.Auto);
        _textDefaults.Alignment = ParseEnum(d.Alignment, TextAlign.Right);
        _textDefaults.LetterSpacing = d.LetterSpacing;
        _textDefaults.LineSpacing = d.LineSpacing;
        _textDefaults.ShadowStyle = ParseEnum(d.ShadowStyle, ShadowStyle.None);
        _textDefaults.ShadowX = d.ShadowX;
        _textDefaults.ShadowY = d.ShadowY;
        _textDefaults.ShadowBlur = d.ShadowBlur;
        _textDefaults.ShadowOpacity = d.ShadowOpacity;
        if (TryParseColor(d.ShadowColor, out var sc)) _textDefaults.ShadowColor = sc;
        _textDefaults.Opacity = d.Opacity;
        _textDefaults.SoftEdge = d.SoftEdge;
    }

    /// <summary>
    /// Save the text defaults without clobbering unrelated settings changed in
    /// Settings while the editor was open.
    /// </summary>
    private void PersistTextDefaults()
    {
        var t = _textDefaults;
        var fresh = AppSettings.Load();
        fresh.Text = new TextDefaults
        {
            FontFamilyName = t.FontFamilyName,
            FontSize = t.FontSize,
            Color1 = ToHex(t.TextColor1),
            Color2 = ToHex(t.TextColor2),
            IsGradient = t.IsGradient,
            GradientAngle = t.TextGradientAngle,
            IsBold = t.IsBold,
            IsItalic = t.IsItalic,
            IsUnderline = t.IsUnderline,
            IsStrikethrough = t.IsStrikethrough,
            HasBorder = t.HasBorder,
            BorderColor = ToHex(t.BorderColor),
            BorderThickness = t.BorderThickness,
            Direction = t.Direction.ToString(),
            Alignment = t.Alignment.ToString(),
            LetterSpacing = t.LetterSpacing,
            LineSpacing = t.LineSpacing,
            ShadowStyle = t.ShadowStyle.ToString(),
            ShadowX = t.ShadowX,
            ShadowY = t.ShadowY,
            ShadowBlur = t.ShadowBlur,
            ShadowOpacity = t.ShadowOpacity,
            ShadowColor = ToHex(t.ShadowColor),
            Opacity = t.Opacity,
            SoftEdge = t.SoftEdge,
        };
        fresh.Save();
    }

    private static bool IsTextStyleProperty(string? name) => name is
        nameof(TextElement.FontFamilyName) or nameof(TextElement.FontSize)
        or nameof(TextElement.TextColor1) or nameof(TextElement.TextColor2)
        or nameof(TextElement.IsGradient) or nameof(TextElement.TextGradientAngle)
        or nameof(TextElement.IsBold) or nameof(TextElement.IsItalic)
        or nameof(TextElement.IsUnderline) or nameof(TextElement.IsStrikethrough)
        or nameof(TextElement.HasBorder) or nameof(TextElement.BorderColor)
        or nameof(TextElement.BorderThickness) or nameof(TextElement.Direction)
        or nameof(TextElement.Alignment) or nameof(TextElement.LetterSpacing)
        or nameof(TextElement.LineSpacing) or nameof(TextElement.WrapWidth)
        or nameof(DesignElement.ShadowStyle) or nameof(DesignElement.ShadowX)
        or nameof(DesignElement.ShadowY) or nameof(DesignElement.ShadowBlur)
        or nameof(DesignElement.ShadowOpacity) or nameof(DesignElement.ShadowColor)
        or nameof(DesignElement.Opacity) or nameof(DesignElement.SoftEdge);

    private void AttrTextColor_Click(object sender, RoutedEventArgs e)
    {
        var target = SelectedText;
        var dlg = new ColorPickerDialog(target?.TextColor1 ?? _textDefaults.TextColor1,
            CanvasColorsWithPrimary()) { Owner = this };
        if (dlg.ShowDialog() != true) return;
        if (target != null)
        {
            PushUndo();
            target.TextColor1 = dlg.SelectedColor;
            UpdateTextEditorStyle();
            RefreshInspector();
        }
        else
        {
            _textDefaults.TextColor1 = dlg.SelectedColor;
        }
        RefreshTextToolbar();
    }

    private void AttrTextFont_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressTextToolbar || AttrTextFontCombo.SelectedItem is not string name) return;
        var target = SelectedText;
        if (target == null)
        {
            _textDefaults.FontFamilyName = name;
            return;
        }
        if (target.FontFamilyName == name) return;
        PushUndo();
        target.FontFamilyName = name;
        UpdateTextEditorStyle();
        RefreshInspector();
        RefreshTextToolbar();
    }

    private void AttrTextSize_ValueChanged(object? sender, EventArgs e)
    {
        if (_suppressTextToolbar) return;
        var target = SelectedText;
        if (target == null)
        {
            _textDefaults.FontSize = AttrTextSize.Value;
            return;
        }
        ArmInspectorUndo();
        target.FontSize = AttrTextSize.Value;
        UpdateTextEditorStyle();
        RefreshInspector();
    }

    private void AttrTextStyle_Click(object sender, RoutedEventArgs e)
    {
        if (_suppressTextToolbar) return;
        bool bold = AttrTextBoldBtn.IsChecked == true;
        bool italic = AttrTextItalicBtn.IsChecked == true;
        bool underline = AttrTextUnderlineBtn.IsChecked == true;
        bool strike = AttrTextStrikeBtn.IsChecked == true;
        var target = SelectedText;
        if (target != null)
        {
            PushUndo();
            target.IsBold = bold;
            target.IsItalic = italic;
            target.IsUnderline = underline;
            target.IsStrikethrough = strike;
            UpdateTextEditorStyle();
            RefreshInspector();
        }
        else
        {
            _textDefaults.IsBold = bold;
            _textDefaults.IsItalic = italic;
            _textDefaults.IsUnderline = underline;
            _textDefaults.IsStrikethrough = strike;
        }
        RefreshTextToolbar();
    }

    private void AttrTextAlign_Click(object sender, RoutedEventArgs e)
    {
        if (_suppressTextToolbar) return;
        var target = SelectedText;
        var current = target?.Alignment ?? _textDefaults.Alignment;
        var next = current switch
        {
            TextAlign.Left => TextAlign.Center,
            TextAlign.Center => TextAlign.Right,
            _ => TextAlign.Left,
        };
        if (target != null)
        {
            PushUndo();
            target.Alignment = next;
            UpdateTextEditorStyle();
            RefreshInspector();
        }
        else
        {
            _textDefaults.Alignment = next;
        }
        RefreshTextToolbar();
    }

    private void AttrTextSpacing_ValueChanged(object? sender, EventArgs e)
    {
        if (_suppressTextToolbar) return;
        var target = SelectedText;
        if (target == null)
        {
            _textDefaults.LetterSpacing = AttrTextSpacing.Value;
            return;
        }
        ArmInspectorUndo();
        target.LetterSpacing = AttrTextSpacing.Value;
        UpdateTextEditorStyle();
        RefreshInspector();
    }

    private void AttrTextBorder_Toggled(object sender, RoutedEventArgs e)
    {
        if (_suppressTextToolbar) return;
        bool on = AttrTextBorderCheck.IsChecked == true;
        var target = SelectedText;
        if (target != null)
        {
            PushUndo();
            target.HasBorder = on;
        }
        else
        {
            _textDefaults.HasBorder = on;
        }
        RefreshTextToolbar();
    }

    private void AttrTextBorderColor_Click(object sender, RoutedEventArgs e)
    {
        var target = SelectedText;
        var dlg = new ColorPickerDialog(target?.BorderColor ?? _textDefaults.BorderColor,
            CanvasColorsWithPrimary()) { Owner = this };
        if (dlg.ShowDialog() != true) return;
        if (target != null)
        {
            PushUndo();
            target.BorderColor = dlg.SelectedColor;
        }
        else
        {
            _textDefaults.BorderColor = dlg.SelectedColor;
        }
        RefreshTextToolbar();
    }

    private void AttrTextBorderThickness_ValueChanged(object? sender, EventArgs e)
    {
        if (_suppressTextToolbar) return;
        var target = SelectedText;
        if (target == null)
        {
            _textDefaults.BorderThickness = AttrTextBorderThickness.Value;
            return;
        }
        ArmInspectorUndo();
        target.BorderThickness = AttrTextBorderThickness.Value;
    }

    private void AttrTextShadow_Toggled(object sender, RoutedEventArgs e)
    {
        if (_suppressTextToolbar) return;
        bool on = AttrTextShadowCheck.IsChecked == true;
        var target = SelectedText;
        if (target != null) PushUndo();
        var styleTarget = target ?? _textDefaults;
        if (on) styleTarget.ApplyShadowPreset(ShadowStyle.Medium);
        else styleTarget.ShadowStyle = ShadowStyle.None;
        RefreshTextToolbar();
        RefreshInspector();
    }

    private void AttrTextShadowColor_Click(object sender, RoutedEventArgs e)
    {
        var target = SelectedText;
        var dlg = new ColorPickerDialog(target?.ShadowColor ?? _textDefaults.ShadowColor,
            CanvasColorsWithPrimary()) { Owner = this };
        if (dlg.ShowDialog() != true) return;
        if (target != null)
        {
            PushUndo();
            target.ShadowColor = dlg.SelectedColor;
        }
        else
        {
            _textDefaults.ShadowColor = dlg.SelectedColor;
        }
        RefreshTextToolbar();
        RefreshInspector();
    }

    // ---------- background remover (magic wand) ----------

    private void EraseAt(Point p)
    {
        if (PickTopmost(p) is not ImageElement im)
        {
            StatusText.Text = "Background remover: click on a raster image (a picture element).";
            return;
        }
        if (!CanvasSampler.TryToSourcePixel(im, p, out int sx, out int sy))
        {
            StatusText.Text = "Click inside the picture to erase the color under the cursor.";
            return;
        }
        var result = BackgroundRemoverService.EraseColor(im, sx, sy,
            AttrEraseTolerance.Value, AttrEraseContiguous.IsChecked == true);
        if (result == null)
        {
            StatusText.Text = "Nothing to erase (pixel already transparent, or raise the tolerance).";
            return;
        }
        PushUndo();
        im.SetBitmap(result, keepSize: true);
        _doc.Select(im);
        StatusText.Text = "Erased the connected color region. Ctrl+Z to undo.";
    }

    private void AttrEraseAuto_Click(object sender, RoutedEventArgs e)
    {
        if (_doc.Selected is not ImageElement im)
        {
            StatusText.Text = "Select a picture first, then Auto — it erases the background connected to its edges.";
            return;
        }
        var result = BackgroundRemoverService.AutoErase(im,
            AttrEraseTolerance.Value, AttrEraseContiguous.IsChecked == true);
        if (result == null)
        {
            StatusText.Text = "Auto: no removable background found.";
            return;
        }
        PushUndo();
        im.SetBitmap(result, keepSize: true);
        StatusText.Text = "Auto background removal done. Ctrl+Z to undo.";
    }

    /// <summary>Ctrl+R: shrink the canvas to the selected object's bounds (its position is untouched).</summary>
    private void FitCanvasToSelection()
    {
        if (_tool == ActiveTool.Slice && _sliceVisible && _activeSlice is { IsEmpty: false } slice)
        {
            PushUndo();
            _doc.SetArtboardRect(slice.Rect);
            ZoomToFit();
            StatusText.Text = $"Project cropped to the active slice ({slice.Rect.Width:F0}×{slice.Rect.Height:F0} px). Content outside the slice stays in the document.";
            return;
        }

        if (_doc.Selected is not { } el)
        {
            StatusText.Text = "Select an object or draw a slice first — Ctrl+R fits the canvas to it.";
            return;
        }
        PushUndo();
        _doc.SetArtboardRect(new Rect(el.X, el.Y, el.Width, el.Height));
        ZoomToFit();
        StatusText.Text = $"Canvas fitted to the selection ({el.Width:F0}×{el.Height:F0} px). Other objects kept their positions.";
    }

    /// <summary>Align the selected object to the canvas edges / center (snap to edges).</summary>
    private void AlignToCanvas_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button btn || btn.Tag is not string tag) return;
        var targets = _doc.SelectedElements.ToList();
        if (targets.Count == 0) return;

        PushUndo();
        double ax = _doc.ArtboardX, ay = _doc.ArtboardY, aw = _doc.ArtboardWidth, ah = _doc.ArtboardHeight;
        foreach (var el in targets)
        {
            switch (tag)
            {
                case "L": el.X = ax; break;
                case "H": el.X = ax + (aw - el.Width) / 2; break;
                case "R": el.X = ax + aw - el.Width; break;
                case "T": el.Y = ay; break;
                case "V": el.Y = ay + (ah - el.Height) / 2; break;
                case "B": el.Y = ay + ah - el.Height; break;
                case "C":
                    el.X = ax + (aw - el.Width) / 2;
                    el.Y = ay + (ah - el.Height) / 2;
                    break;
            }
        }
        StatusText.Text = targets.Count > 1
            ? $"{targets.Count} objects aligned to the canvas."
            : "Object aligned to the canvas.";
    }

    private void BringFront_Click(object sender, RoutedEventArgs e) { if (_doc.Selected != null) PushUndo(); _doc.BringToFront(); }
    private void BringFwd_Click(object sender, RoutedEventArgs e) { if (_doc.Selected != null) PushUndo(); _doc.BringForward(); }
    private void SendBwd_Click(object sender, RoutedEventArgs e) { if (_doc.Selected != null) PushUndo(); _doc.SendBackward(); }
    private void SendBack_Click(object sender, RoutedEventArgs e) { if (_doc.Selected != null) PushUndo(); _doc.SendToBack(); }

    private void DoUndo()
    {
        if (_drag != DragMode.None) return; // never yank state mid-gesture
        StatusText.Text = _undo.Undo(_doc) ? "Undone." : "Nothing to undo.";
    }

    private void DoRedo()
    {
        if (_drag != DragMode.None) return;
        StatusText.Text = _undo.Redo(_doc) ? "Redone." : "Nothing to redo.";
    }

    // =================================================================
    // Tools
    // =================================================================

    private void ToolPick_Click(object sender, RoutedEventArgs e) => SetTool(ActiveTool.Pick);
    private void ToolSlice_Click(object sender, RoutedEventArgs e) => SetTool(ActiveTool.Slice);
    private void ToolLine_Click(object sender, RoutedEventArgs e) => SetTool(ActiveTool.Line);
    private void ToolRect_Click(object sender, RoutedEventArgs e) => SetTool(ActiveTool.Rectangle);
    private void ToolText_Click(object sender, RoutedEventArgs e) => SetTool(ActiveTool.Text);
    private void ToolErase_Click(object sender, RoutedEventArgs e) => SetTool(ActiveTool.Erase);
    private void ToolBrush_Click(object sender, RoutedEventArgs e) => SetTool(ActiveTool.Paintbrush);

    /// <summary>Top-bar "+" toggle: accumulate slices instead of replacing.</summary>
    private void SliceAdd_Click(object sender, RoutedEventArgs e)
    {
        _sliceAddMode = TopAddSliceBtn.IsChecked == true;
        StatusText.Text = _sliceAddMode
            ? "Slice: add mode — each drag adds another region."
            : "Slice: replace mode — each drag replaces the previous region.";
    }

    private void SliceMode_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not ToggleButton button) return;
        _sliceToolMode = button.Tag?.ToString() == "Bezier" ? SliceToolMode.Bezier : SliceToolMode.Rectangle;
        SliceRectModeBtn.IsChecked = _sliceToolMode == SliceToolMode.Rectangle;
        SliceBezierModeBtn.IsChecked = _sliceToolMode == SliceToolMode.Bezier;
        _sliceBezierDraft.Clear();
        _sliceBezierCursor = null;
        RefreshSliceLayer();
        StatusText.Text = _sliceToolMode == SliceToolMode.Bezier
                    ? "Bezier slice: click exact corner points; click the first point, double-click or press Enter to close. Esc cancels."
            : "Rectangular slice: drag to create a region.";
    }

    private static double DistanceSquared(Point a, Point b)
    {
        double dx = a.X - b.X, dy = a.Y - b.Y;
        return dx * dx + dy * dy;
    }

    private void SetTool(ActiveTool tool)
    {
        if (_editingText != null && tool != ActiveTool.Text)
            CommitTextEdit(); // leave the in-place editor before switching tools
        if (tool != ActiveTool.Rectangle && _polygonDraft.Count > 0) ResetPolygonDraft();
        if (tool != ActiveTool.Line && _lineDraft.Count > 0)
        {
            ResetLineDraft();
            if (_drag == DragMode.DrawLine) _drag = DragMode.None;
            if (Pasteboard.IsMouseCaptured) Pasteboard.ReleaseMouseCapture();
        }
        if (_drag == DragMode.Paint && tool != ActiveTool.Paintbrush) EndBrushStroke();
        if (_drag == DragMode.TextPress)
        {
            _drag = DragMode.None;
            _textPressTarget = null;
            if (Pasteboard.IsMouseCaptured) Pasteboard.ReleaseMouseCapture();
        }
        if (tool != ActiveTool.Paintbrush) BrushCursor.Visibility = Visibility.Collapsed;
        HideDrawSizeTip();
        _tool = tool;
        RefreshSliceLayer(); // slice selections remain visible while other tools are active
        UpdateToolUI();
        StatusText.Text = _tabs.Count == 0
            ? "No project open — create one, or paste/drop an image."
            : tool switch
            {
                ActiveTool.Pick => "Pick tool: click to select; Ctrl/Shift+click adds to the selection; drag to move. Slice selections stay visible here; Space hides or shows them.",
                ActiveTool.Slice => _sliceToolMode == SliceToolMode.Bezier
                    ? "Bezier slice: click exact corner points; close at the first point, double-click or press Enter. Esc cancels."
                    : _sliceAddMode
                        ? "Slice tool (add): drag to add a rectangular region."
                        : "Slice tool: drag to create a rectangular region; turn on +Add slice to accumulate.",
                ActiveTool.Rectangle => "Path tool: choose a shape and drag; Polygon uses click-to-connect. V returns to Pick.",
                ActiveTool.Line => "Line tool: Shift snaps straight line angles to 15°; Ctrl snaps endpoints to guides, canvas and objects.",
                ActiveTool.Text => "Text tool: click the canvas and type. Enter = new line, Esc or click outside = done. RTL is automatic (button in the inspector).",
                ActiveTool.Erase => "Background remover: click an image to erase that color's connected area, or press Auto to clear the border background.",
                ActiveTool.Paintbrush => "Paintbrush: paint on the clicked image or on the canvas. Alt+click samples a color; [ / ] changes the size.",
                ActiveTool.Elements => "Elements: browse categories and click an icon to insert it onto the canvas.",
                _ => StatusText.Text,
            };
    }

    /// <summary>
    /// Slide the Elements panel in and out. The panel width is animated (not
    /// just its visibility) so the canvas area shrinks in the same frames and
    /// the artboard glides sideways instead of jumping.
    /// </summary>
    private void AnimateElementsSidebar(bool open)
    {
        var sidebar = ElementsSidebar;
        int version = ++_elementsSidebarVersion;

        var fade = new DoubleAnimation(open ? 0 : 1, open ? 1 : 0, ElementsSidebarDuration)
        {
            FillBehavior = FillBehavior.HoldEnd,
        };
        ElementsSidebarContent.BeginAnimation(UIElement.OpacityProperty, fade);

        if (open)
        {
            double from = 0;
            if (sidebar.Visibility == Visibility.Visible)
            {
                // Reversing a running close animation: continue from the
                // current width instead of snapping back to zero.
                from = sidebar.ActualWidth > 0 ? sidebar.ActualWidth : ElementsSidebarWidth;
                sidebar.BeginAnimation(FrameworkElement.WidthProperty, null);
                sidebar.Width = from;
            }
            else
            {
                sidebar.Visibility = Visibility.Visible;
                sidebar.Width = 0;
            }

            var grow = new DoubleAnimation(from, ElementsSidebarWidth, ElementsSidebarDuration)
            {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
                FillBehavior = FillBehavior.HoldEnd,
            };
            sidebar.BeginAnimation(FrameworkElement.WidthProperty, grow);
            return;
        }

        if (sidebar.Visibility == Visibility.Collapsed) return;

        double shrinkFrom = sidebar.ActualWidth > 0 ? sidebar.ActualWidth : ElementsSidebarWidth;
        sidebar.BeginAnimation(FrameworkElement.WidthProperty, null);
        var shrink = new DoubleAnimation(shrinkFrom, 0, ElementsSidebarDuration)
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseIn },
            FillBehavior = FillBehavior.HoldEnd,
        };
        shrink.Completed += (_, _) =>
        {
            if (version != _elementsSidebarVersion) return; // re-opened meanwhile
            sidebar.BeginAnimation(FrameworkElement.WidthProperty, null);
            sidebar.Width = ElementsSidebarWidth;
            sidebar.Visibility = Visibility.Collapsed;
        };
        sidebar.BeginAnimation(FrameworkElement.WidthProperty, shrink);
    }

    private void UpdateToolUI()
    {
        SidePickBtn.IsChecked = _tool == ActiveTool.Pick;
        SideSliceBtn.IsChecked = _tool == ActiveTool.Slice;
        SideRectBtn.IsChecked = _tool == ActiveTool.Rectangle;
        SideLineBtn.IsChecked = _tool == ActiveTool.Line;
        SideTextBtn.IsChecked = _tool == ActiveTool.Text;
        SideElementsBtn.IsChecked = _tool == ActiveTool.Elements;
        SideEraseBtn.IsChecked = _tool == ActiveTool.Erase;
        SideBrushBtn.IsChecked = _tool == ActiveTool.Paintbrush;
        Cursor = _tool switch
        {
            ActiveTool.Pick => Cursors.Arrow,
            ActiveTool.Text => Cursors.IBeam,
            ActiveTool.Elements => Cursors.Arrow,
            _ => Cursors.Cross,
        };

        // Attribute toolbar (tool settings row) — shows the active
        // tool's options here, never on the top bar.
        AttrPickPanel.Visibility = _tool == ActiveTool.Pick ? Visibility.Visible : Visibility.Collapsed;
        AttrSlicePanel.Visibility = _tool == ActiveTool.Slice ? Visibility.Visible : Visibility.Collapsed;
        AttrErasePanel.Visibility = _tool == ActiveTool.Erase ? Visibility.Visible : Visibility.Collapsed;
        AttrBrushPanel.Visibility = _tool == ActiveTool.Paintbrush ? Visibility.Visible : Visibility.Collapsed;
        AttrTextPanel.Visibility = _tool == ActiveTool.Text ? Visibility.Visible : Visibility.Collapsed;
        AttrRectPanel.Visibility = _tool == ActiveTool.Rectangle ? Visibility.Visible : Visibility.Collapsed;
        AttrLinePanel.Visibility = _tool == ActiveTool.Line ? Visibility.Visible : Visibility.Collapsed;
        AttrElementsPanel.Visibility = _tool == ActiveTool.Elements ? Visibility.Visible : Visibility.Collapsed;
        AnimateElementsSidebar(_tool == ActiveTool.Elements);
        bool hint = _tool is not (ActiveTool.Pick or ActiveTool.Slice or ActiveTool.Erase
            or ActiveTool.Text or ActiveTool.Rectangle or ActiveTool.Line or ActiveTool.Paintbrush or ActiveTool.Elements);
        AttrHint.Visibility = hint ? Visibility.Visible : Visibility.Collapsed;
        if (_tool == ActiveTool.Text) RefreshTextToolbar();
        if (_tool == ActiveTool.Rectangle) RefreshShapeToolbar();
        if (_tool == ActiveTool.Line) RefreshLineToolbar();
        if (_tool == ActiveTool.Paintbrush) RefreshBrushToolbar();
        AttrToolName.Text = _tool switch
        {
            ActiveTool.Pick => "PICK",
            ActiveTool.Slice => "SLICE",
            ActiveTool.Rectangle => "PATH",
            ActiveTool.Line => "LINE AND ARROW",
            ActiveTool.Text => "TEXT",
            ActiveTool.Elements => "ELEMENTS",
            ActiveTool.Erase => "ERASE BACKGROUND",
            ActiveTool.Paintbrush => "PAINTBRUSH",
            _ => "PICK",
        };
        AttrHint.Text = _tool switch
        {
            ActiveTool.Rectangle => "Choose Rectangle, Ellipse, Triangle or Polygon in the top bar, then draw on the canvas.",
            ActiveTool.Line => "Choose Line, Polyline or Freehand; set start and end markers independently, including arrowheads.",
            ActiveTool.Text => "Click the canvas and type. Enter = new line; Esc or click outside finishes. Auto RTL.",
            _ => "",
        };
        if (_tool == ActiveTool.Pick) UpdatePickToolbarState();
    }

    /// <summary>
    /// Pick-tool properties are only meaningful with a selection; hide them
    /// (with a short hint) instead of showing dead controls. Corner applies to
    /// shapes/images only, so it is faded for text selections.
    /// </summary>
    private void UpdatePickToolbarState()
    {
        bool hasSelection = _doc.Selected != null;
        AttrPickPanel.Visibility = hasSelection ? Visibility.Visible : Visibility.Collapsed;
        AttrHint.Visibility = hasSelection ? Visibility.Collapsed : Visibility.Visible;
        if (!hasSelection)
        {
            AttrHint.Text = _tabs.Count == 0
                ? "Create or open a project, or paste/drop an image to start."
                : "Select an object to edit its properties.";
            return;
        }

        bool canCorner = _doc.Selected is ImageElement
            || _doc.Selected is ShapeElement { Kind: ShapeKind.Rectangle };
        bool single = _doc.SelectionCount == 1;
        AttrRatioLockBtn.IsEnabled = single;
        AttrOpacity.IsEnabled = single;
        AttrSoft.IsEnabled = single;
        AttrCorner.IsEnabled = single && canCorner;
        AttrCorner.Opacity = single && canCorner ? 1.0 : 0.35;
    }

    // =================================================================
    // Paintbrush: settings, presets and raster strokes
    // =================================================================

    private void ApplyBrushDefaults(BrushDefaults d)
    {
        _brushSettings.Style = ParseEnum(d.Style, BrushStyle.Paint);
        _brushSettings.Target = ParseEnum(d.Target, BrushTargetMode.Auto);
        _brushSettings.Size = Math.Clamp(d.Size, 1, 300);
        _brushSettings.Opacity = Math.Clamp(d.Opacity, 0, 100);
        _brushSettings.Softness = Math.Clamp(d.Softness, 0, 100);
        _brushSettings.Smoothing = Math.Clamp(d.Smoothing, 0, 100);
        _brushSettings.Color = TryParseColor(d.Color, out var color)
            ? color
            : Color.FromRgb(0x2E, 0x9B, 0xFF);
    }

    private BrushDefaults CaptureBrushDefaults(string name = "") => new()
    {
        Name = name,
        Style = _brushSettings.Style.ToString(),
        Target = _brushSettings.Target.ToString(),
        Size = _brushSettings.Size,
        Opacity = _brushSettings.Opacity,
        Softness = _brushSettings.Softness,
        Smoothing = _brushSettings.Smoothing,
        Color = ToHex(_brushSettings.Color),
    };

    private void PersistBrushDefaults()
    {
        var fresh = AppSettings.Load();
        fresh.Brush = CaptureBrushDefaults();
        fresh.BrushPresets = _settings.BrushPresets;
        fresh.Save();
    }

    private void SaveBrushPresets()
    {
        var fresh = AppSettings.Load();
        fresh.Brush = CaptureBrushDefaults();
        fresh.BrushPresets = _settings.BrushPresets;
        fresh.Save();
    }

    private void RebuildBrushPresetList()
    {
        _brushPresetItems.Clear();

        _brushPresetItems.Add(BuiltInPreset("Soft brush", BrushStyle.Paint, 48, 100, 70, 40, "#2E9BFF"));
        _brushPresetItems.Add(BuiltInPreset("Hard round", BrushStyle.Paint, 24, 100, 4, 12, "#000000"));
        _brushPresetItems.Add(BuiltInPreset("Retouch blur", BrushStyle.Smudge, 44, 45, 85, 30, "#2E9BFF"));
        _brushPresetItems.Add(BuiltInPreset("Watercolor wet", BrushStyle.Watercolor, 52, 65, 75, 45, "#3B82F6"));
        _brushPresetItems.Add(BuiltInPreset("Soft eraser", BrushStyle.Eraser, 40, 100, 65, 25, "#2E9BFF"));
        _brushPresetItems.Add(BuiltInPreset("Merge to background", BrushStyle.Merge, 56, 45, 80, 30, "#2E9BFF"));

        foreach (var saved in _settings.BrushPresets)
        {
            if (saved == null) continue;
            _brushPresetItems.Add(new BrushPresetItem
            {
                Name = string.IsNullOrWhiteSpace(saved.Name) ? "Preset" : saved.Name,
                Settings = saved,
                Preview = BuildBrushPresetPreview(saved),
            });
        }

        if (AttrBrushPresetList == null) return;
        AttrBrushPresetList.ItemsSource = null;
        AttrBrushPresetList.ItemsSource = _brushPresetItems;
    }

    private static BrushPresetItem BuiltInPreset(
        string name, BrushStyle style, double size, double opacity, double softness,
        double smoothing, string color)
    {
        var settings = new BrushDefaults
        {
            Name = name,
            Style = style.ToString(),
            Target = BrushTargetMode.Auto.ToString(),
            Size = size,
            Opacity = opacity,
            Softness = softness,
            Smoothing = smoothing,
            Color = color,
        };
        return new BrushPresetItem
        {
            Name = name,
            IsBuiltIn = true,
            Settings = settings,
            Preview = BuildBrushPresetPreview(settings),
        };
    }

    /// <summary>Small representative stroke rendered with the real brush engine.</summary>
    private static ImageSource? BuildBrushPresetPreview(BrushDefaults defaults)
    {
        try
        {
            const int width = 54;
            const int height = 24;
            var style = ParseEnum(defaults.Style, BrushStyle.Paint);
            var settings = new BrushSettings
            {
                Style = style,
                Target = BrushTargetMode.Auto,
                Size = Math.Clamp(defaults.Size, 8, 100),
                Opacity = Math.Clamp(defaults.Opacity, 0, 100),
                Softness = Math.Clamp(defaults.Softness, 0, 100),
                Smoothing = Math.Clamp(defaults.Smoothing, 0, 100),
                Color = TryParseColor(defaults.Color, out var color)
                    ? color
                    : Color.FromRgb(0x2E, 0x9B, 0xFF),
            };

            var bitmap = BrushRaster.CreateTransparent(width, height);
            var pixels = BrushRaster.ReadPixels(bitmap);

            if (style is BrushStyle.Smudge or BrushStyle.Merge or BrushStyle.Eraser)
            {
                FillPresetPreviewBackground(pixels, width, height, style);
                bitmap.WritePixels(new Int32Rect(0, 0, width, height), pixels, width * 4, 0);
            }

            var surface = new BrushSurface(bitmap, pixels, 0.34);
            var engine = new BrushEngine();
            var mergeColor = style == BrushStyle.Merge
                ? Color.FromRgb(0xF8, 0xFA, 0xFC)
                : settings.Color;

            engine.Begin(surface, new Point(6, 18), settings, mergeColor, 0.9);
            for (int i = 1; i <= 12; i++)
            {
                double t = i / 12.0;
                var point = new Point(
                    6 + t * 42,
                    18 - t * 12 + Math.Sin(t * Math.PI * 2) * 1.4);
                engine.Move(point, 0.9, mergeColor);
            }
            engine.End();
            return bitmap;
        }
        catch
        {
            return null;
        }
    }

    private static void FillPresetPreviewBackground(
        byte[] pixels, int width, int height, BrushStyle style)
    {
        (byte r1, byte g1, byte b1, byte r2, byte g2, byte b2) = style switch
        {
            BrushStyle.Smudge => ((byte)0x2E, (byte)0x9B, (byte)0xFF,
                                  (byte)0xF5, (byte)0x9E, (byte)0x0B),
            BrushStyle.Merge => ((byte)0x1E, (byte)0x3A, (byte)0x8A,
                                 (byte)0x9A, (byte)0x5C, (byte)0xFF),
            _ => ((byte)0x2E, (byte)0x9B, (byte)0xFF,
                  (byte)0x9A, (byte)0x5C, (byte)0xFF),
        };

        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                double t = (double)x / Math.Max(1, width - 1);
                double wave = Math.Sin(x * 0.55 + y * 0.7) * 0.05;
                int i = (y * width + x) * 4;
                pixels[i] = (byte)Math.Clamp((int)(b1 + (b2 - b1) * t + wave * 255), 0, 255);
                pixels[i + 1] = (byte)Math.Clamp((int)(g1 + (g2 - g1) * t + wave * 255), 0, 255);
                pixels[i + 2] = (byte)Math.Clamp((int)(r1 + (r2 - r1) * t + wave * 255), 0, 255);
                pixels[i + 3] = 255;
            }
        }
    }

    private void RefreshBrushToolbar()
    {
        if (AttrBrushPanel == null) return;
        _suppressBrushToolbar = true;
        try
        {
            UpdateBrushToggles();
            AttrBrushSize.SetValue(_brushSettings.Size);
            AttrBrushOpacity.SetValue(_brushSettings.Opacity);
            AttrBrushSoft.SetValue(_brushSettings.Softness);
            AttrBrushSmooth.SetValue(_brushSettings.Smoothing);
            AttrBrushColorBtn.Background = new SolidColorBrush(_brushSettings.Color);
            UpdateBrushStyleUi();
            UpdateBrushPreview();
        }
        finally { _suppressBrushToolbar = false; }
    }

    /// <summary>Small live stroke so size, softness and color are visible before painting.</summary>
    private void UpdateBrushPreview()
    {
        if (AttrBrushPreview == null) return;
        AttrBrushPreview.Source = BuildBrushPresetPreview(CaptureBrushDefaults());
    }

    private void UpdateBrushToggles()
    {
        AttrBrushPaintBtn.IsChecked = _brushSettings.Style == BrushStyle.Paint;
        AttrBrushSmudgeBtn.IsChecked = _brushSettings.Style == BrushStyle.Smudge;
        AttrBrushWaterBtn.IsChecked = _brushSettings.Style == BrushStyle.Watercolor;
        AttrBrushEraserBtn.IsChecked = _brushSettings.Style == BrushStyle.Eraser;
        AttrBrushMergeBtn.IsChecked = _brushSettings.Style == BrushStyle.Merge;

        AttrBrushAutoBtn.IsChecked = _brushSettings.Target == BrushTargetMode.Auto;
        AttrBrushCanvasBtn.IsChecked = _brushSettings.Target == BrushTargetMode.Canvas;
        AttrBrushSelectedBtn.IsChecked = _brushSettings.Target == BrushTargetMode.Selected;
    }

    private void UpdateBrushStyleUi()
    {
        if (AttrBrushColorBtn == null) return;
        bool usesColor = _brushSettings.Style is BrushStyle.Paint or BrushStyle.Watercolor;
        AttrBrushColorBtn.IsEnabled = usesColor;
        AttrBrushColorBtn.Opacity = usesColor ? 1.0 : 0.35;
        AttrBrushColorBtn.ToolTip = _brushSettings.Style switch
        {
            BrushStyle.Eraser => "Eraser: removes pixels to transparency",
            BrushStyle.Smudge => "Smudge/Blur: samples the colors under the brush",
            BrushStyle.Merge => "Merge: samples the canvas behind the image",
            _ => "Brush color (Alt+click on the canvas samples a color)",
        };
    }

    private void MarkBrushCustom()
    {
        if (AttrBrushPresetName != null)
            AttrBrushPresetName.Text = "Custom";
        UpdateBrushPreview();
    }

    private void AttrBrushStyle_Click(object sender, RoutedEventArgs e)
    {
        if (_suppressBrushToolbar || sender is not ToggleButton { Tag: string tag }
            || !Enum.TryParse<BrushStyle>(tag, true, out var style))
            return;

        _brushSettings.Style = style;
        _suppressBrushToolbar = true;
        UpdateBrushToggles();
        _suppressBrushToolbar = false;
        UpdateBrushStyleUi();
        MarkBrushCustom();
    }

    private void AttrBrushTarget_Click(object sender, RoutedEventArgs e)
    {
        if (_suppressBrushToolbar || sender is not ToggleButton { Tag: string tag }
            || !Enum.TryParse<BrushTargetMode>(tag, true, out var target))
            return;

        _brushSettings.Target = target;
        _suppressBrushToolbar = true;
        UpdateBrushToggles();
        _suppressBrushToolbar = false;
        MarkBrushCustom();
    }

    private void AttrBrushColor_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new ColorPickerDialog(_brushSettings.Color, CanvasColorsWithPrimary())
        {
            Owner = this,
        };
        if (dlg.ShowDialog() != true) return;
        _brushSettings.Color = dlg.SelectedColor;
        AttrBrushColorBtn.Background = new SolidColorBrush(_brushSettings.Color);
        MarkBrushCustom();
    }

    private void AttrBrushSize_ValueChanged(object? sender, EventArgs e)
    {
        if (_suppressBrushToolbar || BrushCursor == null) return;
        _brushSettings.Size = Math.Clamp(AttrBrushSize.Value, 1, 300);
        MarkBrushCustom();
        UpdateBrushCursor(Mouse.GetPosition(Pasteboard));
    }

    private void AttrBrushOpacity_ValueChanged(object? sender, EventArgs e)
    {
        if (_suppressBrushToolbar) return;
        _brushSettings.Opacity = Math.Clamp(AttrBrushOpacity.Value, 0, 100);
        MarkBrushCustom();
    }

    private void AttrBrushSoft_ValueChanged(object? sender, EventArgs e)
    {
        if (_suppressBrushToolbar) return;
        _brushSettings.Softness = Math.Clamp(AttrBrushSoft.Value, 0, 100);
        MarkBrushCustom();
    }

    private void AttrBrushSmooth_ValueChanged(object? sender, EventArgs e)
    {
        if (_suppressBrushToolbar) return;
        _brushSettings.Smoothing = Math.Clamp(AttrBrushSmooth.Value, 0, 100);
        MarkBrushCustom();
    }

    private void AttrBrushPresetItem_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: BrushPresetItem item } || item.IsCustom) return;
        ApplyBrushDefaults(item.Settings);
        RefreshBrushToolbar();
        AttrBrushPresetName.Text = item.Name;
        AttrBrushPresetBtn.IsChecked = false;
        StatusText.Text = $"Brush preset: {item.Name}.";
    }

    private void AttrBrushPresetSave_Click(object sender, RoutedEventArgs e)
    {
        int n = 1;
        string name;
        do { name = $"Preset {n++}"; } while (_settings.BrushPresets.Any(p => p.Name == name));

        var preset = CaptureBrushDefaults(name);
        _settings.BrushPresets.Add(preset);
        SaveBrushPresets();
        RebuildBrushPresetList();

        AttrBrushPresetName.Text = name;
        AttrBrushPresetBtn.IsChecked = false;
        StatusText.Text = $"Saved brush preset \"{name}\".";
    }

    private void AttrBrushPresetDeleteItem_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: BrushPresetItem item } || !item.CanDelete)
            return;

        _settings.BrushPresets.RemoveAll(p => p.Name == item.Name);
        SaveBrushPresets();
        if (AttrBrushPresetName.Text == item.Name)
            AttrBrushPresetName.Text = "Custom";
        RebuildBrushPresetList();
        StatusText.Text = $"Deleted brush preset \"{item.Name}\".";
    }

    private void AdjustBrushSize(int direction)
    {
        double step = _brushSettings.Size < 10 ? 1 : _brushSettings.Size < 50 ? 2 : 6;
        _brushSettings.Size = Math.Clamp(_brushSettings.Size + direction * step, 1, 300);
        _suppressBrushToolbar = true;
        AttrBrushSize.SetValue(_brushSettings.Size);
        _suppressBrushToolbar = false;
        MarkBrushCustom();
        UpdateBrushCursor(Mouse.GetPosition(Pasteboard));
        StatusText.Text = $"Brush size: {_brushSettings.Size:F0} px.";
    }

    private double GetBrushPressure(MouseEventArgs e)
    {
        try
        {
            var device = e.StylusDevice;
            if (device != null)
            {
                var points = device.GetStylusPoints(Pasteboard);
                if (points.Count > 0)
                    return points[points.Count - 1].PressureFactor;
            }
        }
        catch { }
        return 1.0;
    }

    /// <summary>Rotation-aware bounds test for starting a stroke on an object.</summary>
    private static bool IsInsideElementBounds(DesignElement el, Point point)
    {
        double dx = point.X - el.Center.X;
        double dy = point.Y - el.Center.Y;
        double angle = -el.RotationAngle * Math.PI / 180.0;
        double cos = Math.Cos(angle), sin = Math.Sin(angle);
        double rx = dx * cos - dy * sin;
        double ry = dx * sin + dy * cos;
        if (el.FlipHorizontal) rx = -rx;
        if (el.FlipVertical) ry = -ry;
        return Math.Abs(rx) <= el.Width / 2 + 1 && Math.Abs(ry) <= el.Height / 2 + 1;
    }

    /// <summary>
    /// Replace a shape/text object with an image element at the same place so the
    /// paintbrush can put pixels on it. Undo restores the original object.
    /// </summary>
    private static void LogPaintError(Exception ex)
    {
        try
        {
            var path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "YorteImpact-paint-error.log");
            System.IO.File.AppendAllText(path,
                DateTime.Now.ToString("s") + " " + ex + Environment.NewLine);
        }
        catch
        {
        }
    }

    private ImageElement? RasterizeForPainting(DesignElement target)
    {
        if (!_doc.Elements.Contains(target)) return null;

        ImageElement image;
        try
        {
            var source = target.Clone();
            source.RotationAngle = 0;
            source.FlipHorizontal = false;
            source.FlipVertical = false;
            source.ShadowStyle = ShadowStyle.None;
            source.Opacity = 100;
            source.SoftEdge = 0;
            source.UseOpacityGradient = false;

            double scale = Math.Min(1.0, Math.Min(4096.0 / Math.Max(1, target.Width),
                4096.0 / Math.Max(1, target.Height)));
            var bitmap = SliceExportService.RenderElement(source, null, scale);

            image = new ImageElement
            {
                X = target.X,
                Y = target.Y,
                Width = target.Width,
                Height = target.Height,
                RotationAngle = target.RotationAngle,
                FlipHorizontal = target.FlipHorizontal,
                FlipVertical = target.FlipVertical,
                Opacity = target.Opacity,
                SoftEdge = target.SoftEdge,
                UseOpacityGradient = target.UseOpacityGradient,
                OpacityDirection = target.OpacityDirection,
                OpacityStart = target.OpacityStart,
                OpacityEnd = target.OpacityEnd,
                OpacityStrength = target.OpacityStrength,
                ShadowStyle = target.ShadowStyle,
                ShadowX = target.ShadowX,
                ShadowY = target.ShadowY,
                ShadowBlur = target.ShadowBlur,
                ShadowOpacity = target.ShadowOpacity,
                ShadowColor = target.ShadowColor,
                ZIndex = target.ZIndex,
            };
            image.SetBitmap(bitmap, keepSize: true);
        }
        catch (Exception ex)
        {
            LogPaintError(ex);
            return null;
        }

        PushUndo();
        int index = _doc.Elements.IndexOf(target);
        _doc.Select(null);
        _doc.Elements[index] = image;
        _doc.Select(image);
        return image;
    }

    private bool TryResolveBrushTarget(
        Point pos, bool selectImage, out ImageElement? image, out Point sourcePoint,
        out double pixelsPerUnit, out bool canvas)
    {
        image = null;
        sourcePoint = default;
        pixelsPerUnit = 1;
        canvas = false;

        switch (_brushSettings.Target)
        {
            case BrushTargetMode.Canvas:
                canvas = true;
                return true;

            case BrushTargetMode.Selected:
                if (_doc.Selected is ImageElement selected
                    && TryMapImagePoint(selected, pos, out _, out sourcePoint, out pixelsPerUnit))
                {
                    image = selected;
                    return true;
                }
                return false;

            default:
                // A selected image always wins over the canvas behind it, but a
                // click on another image paints that one instead.
                if (_doc.Selected is ImageElement current
                    && TryMapImagePoint(current, pos, out _, out sourcePoint, out pixelsPerUnit))
                {
                    image = current;
                    return true;
                }

                if (PickTopmost(pos) is ImageElement hit
                    && TryMapImagePoint(hit, pos, out _, out sourcePoint, out pixelsPerUnit))
                {
                    image = hit;
                    if (selectImage && !ReferenceEquals(_doc.Selected, hit))
                        _doc.Select(hit);
                    return true;
                }

                if (_doc.Selected is ImageElement) return false;

                canvas = true;
                return true;
        }
    }

    private static bool TryMapImagePoint(
        ImageElement image, Point canvasPoint, out Point local, out Point source,
        out double pixelsPerUnit)
    {
        local = default;
        source = default;
        pixelsPerUnit = 1;
        if (image.Source is not BitmapSource bmp || bmp.PixelWidth < 1 || bmp.PixelHeight < 1)
            return false;

        double dx = canvasPoint.X - image.Center.X;
        double dy = canvasPoint.Y - image.Center.Y;
        double angle = -image.RotationAngle * Math.PI / 180.0;
        double cos = Math.Cos(angle);
        double sin = Math.Sin(angle);
        double rx = dx * cos - dy * sin;
        double ry = dx * sin + dy * cos;
        if (image.FlipHorizontal) rx = -rx;
        if (image.FlipVertical) ry = -ry;

        local = new Point(image.Width / 2 + rx, image.Height / 2 + ry);
        if (local.X < 0 || local.Y < 0 || local.X > image.Width || local.Y > image.Height)
            return false;

        if (!image.TryMapToSource(local, out double sx, out double sy, out pixelsPerUnit))
            return false;

        source = new Point(sx, sy);
        return true;
    }

    private void BeginBrushStroke(Point pos, MouseEventArgs e, bool snapshot = true,
        ImageElement? forcedImage = null)
    {
        if (_tabs.Count == 0) return;

        // A shape or text object under the brush is rasterized in place the
        // first time it is painted, so strokes land on that object and it
        // behaves like an image afterwards. Undo restores the original object.
        if ((Keyboard.Modifiers & ModifierKeys.Alt) == 0 &&
            _brushSettings.Target != BrushTargetMode.Canvas)
        {
            DesignElement? paintTarget = null;
            if (_doc.Selected is { } selected && selected is not ImageElement &&
                !selected.IsBackgroundLayer && IsInsideElementBounds(selected, pos))
            {
                paintTarget = selected;
            }
            else if (_brushSettings.Target != BrushTargetMode.Selected)
            {
                var hit = PickTopmost(pos);
                if (hit is { IsBackgroundLayer: false } && hit is not ImageElement &&
                    IsInsideElementBounds(hit, pos))
                    paintTarget = hit;
            }

            if (paintTarget != null)
            {
                var converted = RasterizeForPainting(paintTarget);
                if (converted != null)
                {
                    BeginBrushStroke(pos, e, snapshot: false, forcedImage: converted);
                    StatusText.Text += " Converted the object to an image (Ctrl+Z restores it).";
                    return;
                }
            }
        }

        ImageElement? image;
        Point sourcePoint;
        double pixelsPerUnit;
        bool canvas;
        if (forcedImage != null)
        {
            if (!TryMapImagePoint(forcedImage, pos, out _, out sourcePoint, out pixelsPerUnit))
            {
                StatusText.Text = "Paintbrush: click inside the converted image.";
                return;
            }
            image = forcedImage;
            canvas = false;
        }
        else if (!TryResolveBrushTarget(pos, selectImage: true, out image,
                     out sourcePoint, out pixelsPerUnit, out canvas))
        {
            StatusText.Text = _doc.Selected is ImageElement
                ? "Paintbrush: click inside the selected image, or set Target to Canvas to paint the background."
                : "Paintbrush: select an object or image, or set Target to Auto/Canvas.";
            return;
        }

        if (canvas && !_doc.ArtboardRect.Contains(pos))
            return;

        if ((Keyboard.Modifiers & ModifierKeys.Alt) != 0)
        {
            TryPickBrushColor(pos, image, sourcePoint, canvas);
            return;
        }

        if (_brushSettings.Style == BrushStyle.Merge && image != null)
            PrepareMergeBackground(image);

        var pressure = GetBrushPressure(e);
        _mergeFallback = _doc.CanvasColor;
        var mergeColor = SampleMergeColor(pos, image != null);

        try
        {
            if (image != null)
            {
                if (image.Source is not BitmapSource source) return;

                if (snapshot) PushUndo();
                var bitmap = BrushRaster.CopyToWriteable(source);
                image.Source = bitmap;
                var pixels = BrushRaster.ReadPixels(bitmap);
                _brushImage = image;
                _brushSurface = new BrushSurface(bitmap, pixels, pixelsPerUnit);

                var point = new Point(
                    Math.Clamp(sourcePoint.X, 0, bitmap.PixelWidth - 1),
                    Math.Clamp(sourcePoint.Y, 0, bitmap.PixelHeight - 1));
                _brushEngine.Begin(_brushSurface, point, _brushSettings, mergeColor, pressure);
            }
            else
            {
                if (snapshot) PushUndo();
                var bitmap = EnsureCanvasPaintSurface();
                if (bitmap == null) return;

                var pixels = BrushRaster.ReadPixels(bitmap);
                double ppu = (double)bitmap.PixelWidth / Math.Max(1, _doc.ArtboardWidth);
                _brushImage = null;
                _brushSurface = new BrushSurface(bitmap, pixels, ppu);

                var art = _doc.ArtboardRect;
                var point = new Point(
                    Math.Clamp((pos.X - art.X) * bitmap.PixelWidth / Math.Max(1, art.Width), 0, bitmap.PixelWidth - 1),
                    Math.Clamp((pos.Y - art.Y) * bitmap.PixelHeight / Math.Max(1, art.Height), 0, bitmap.PixelHeight - 1));
                _brushEngine.Begin(_brushSurface, point, _brushSettings, mergeColor, pressure);
            }
        }
        catch (Exception ex)
        {
            LogPaintError(ex);
            StatusText.Text = "Paint failed: " + ex.Message;
            EndBrushStroke();
            return;
        }

        _drag = DragMode.Paint;
        _brushOutside = false;
        Pasteboard.CaptureMouse();
        UpdateBrushCursor(pos);

        string target = image != null ? "image" : "canvas";
        string style = _brushSettings.Style switch
        {
            BrushStyle.Smudge => "smudge / blur",
            BrushStyle.Watercolor => "watercolor",
            BrushStyle.Eraser => "eraser",
            BrushStyle.Merge => "merge",
            _ => "paint",
        };
        StatusText.Text = $"Paintbrush ({style}) on {target}: {_brushSettings.Size:F0} px, {_brushSettings.Opacity:F0}% opacity. Alt+click samples a color.";
        if (canvas && PickTopmost(pos) is { IsBackgroundLayer: false })
            StatusText.Text += " An object is above this spot - set Target to Auto to paint it.";
    }

    private void ContinueBrushStroke(Point pos, MouseEventArgs e)
    {
        if (_brushSurface == null) return;

        var pressure = GetBrushPressure(e);
        var mergeColor = SampleMergeColor(pos, _brushImage != null);
        Point point;

        if (_brushImage != null)
        {
            if (!TryMapImagePoint(_brushImage, pos, out _, out var source, out _))
            {
                _brushOutside = true;
                return;
            }
            point = new Point(
                Math.Clamp(source.X, 0, _brushSurface.Width - 1),
                Math.Clamp(source.Y, 0, _brushSurface.Height - 1));
        }
        else
        {
            if (!_doc.ArtboardRect.Contains(pos))
            {
                _brushOutside = true;
                return;
            }
            var art = _doc.ArtboardRect;
            point = new Point(
                Math.Clamp((pos.X - art.X) * _brushSurface.Width / Math.Max(1, art.Width), 0, _brushSurface.Width - 1),
                Math.Clamp((pos.Y - art.Y) * _brushSurface.Height / Math.Max(1, art.Height), 0, _brushSurface.Height - 1));
        }

        if (_brushOutside)
        {
            _brushEngine.SkipTo(point);
            _brushOutside = false;
        }

        _brushEngine.Move(point, pressure, mergeColor);
        UpdateBrushCursor(pos);
    }

    private void EndBrushStroke()
    {
        var editedImage = _brushImage;
        _brushEngine.End();
        _brushSurface = null;
        _brushImage = null;
        _brushOutside = false;
        _drag = DragMode.None;
        ClearMergeBackground();
        if (Pasteboard.IsMouseCaptured) Pasteboard.ReleaseMouseCapture();
        if (editedImage != null && !editedImage.Adjustments.IsDefault)
        {
            editedImage.RefreshPhotoPreview();
            if (ReferenceEquals(_doc.Selected, editedImage)) RefreshPhotoPanel();
        }
        UpdateBrushCursor(Mouse.GetPosition(Pasteboard));
    }

    private WriteableBitmap? EnsureCanvasPaintSurface()
    {
        int width = Math.Max(1, (int)Math.Round(_doc.ArtboardWidth));
        int height = Math.Max(1, (int)Math.Round(_doc.ArtboardHeight));
        if ((long)width * height > 40_000_000)
        {
            StatusText.Text = "The canvas is too large for the paint layer (max 40 MP).";
            return null;
        }

        var layer = _doc.EnsureCanvasPaintLayer();
        if (layer.Source is WriteableBitmap writeable && !writeable.IsFrozen
            && writeable.PixelWidth == width && writeable.PixelHeight == height)
            return writeable;

        WriteableBitmap next;
        if (layer.Source is BitmapSource source)
        {
            next = source.PixelWidth == width && source.PixelHeight == height
                ? BrushRaster.CopyToWriteable(source)
                : BrushRaster.Resize(source, width, height);
        }
        else
        {
            next = BrushRaster.CreateTransparent(width, height);
        }

        layer.Source = next;
        return next;
    }

    private void UpdateBrushCursor(Point pos)
    {
        if (BrushCursor == null || _tool != ActiveTool.Paintbrush || _tabs.Count == 0)
        {
            if (BrushCursor != null) BrushCursor.Visibility = Visibility.Collapsed;
            return;
        }

        double size = Math.Max(2, _brushSettings.Size);
        BrushCursor.Width = size;
        BrushCursor.Height = size;
        Canvas.SetLeft(BrushCursor, pos.X - size / 2);
        Canvas.SetTop(BrushCursor, pos.Y - size / 2);
        BrushCursor.StrokeThickness = Math.Max(0.4, 1.2 / Math.Max(0.05, _zoom));
        BrushCursor.Visibility = _doc.ArtboardRect.Contains(pos)
            ? Visibility.Visible
            : Visibility.Collapsed;
    }

    private void PrepareMergeBackground(ImageElement image)
    {
        ClearMergeBackground();
        _mergeFallback = _doc.CanvasColor;

        var hidden = new List<UIElement>();
        try
        {
            ElementHost.UpdateLayout();
            foreach (var el in _doc.Elements
                         .Where(el => !el.IsBackgroundLayer && el.ZIndex >= image.ZIndex)
                         .ToList())
            {
                if (ElementHost.ItemContainerGenerator.ContainerFromItem(el) is not UIElement container
                    || container.Visibility != Visibility.Visible)
                    continue;
                container.Visibility = Visibility.Collapsed;
                hidden.Add(container);
            }
            ElementHost.UpdateLayout();

            var art = _doc.ArtboardRect;
            var bmp = SliceExportService.RenderRegion(ElementHost, art, _doc.CanvasColor, 1.0, 96);
            BitmapSource bgra = bmp.Format == PixelFormats.Bgra32
                ? bmp
                : new FormatConvertedBitmap(bmp, PixelFormats.Bgra32, null, 0);
            _mergeWidth = bgra.PixelWidth;
            _mergeHeight = bgra.PixelHeight;
            _mergeStride = _mergeWidth * 4;
            _mergePixels = new byte[_mergeStride * _mergeHeight];
            bgra.CopyPixels(_mergePixels, _mergeStride, 0);
        }
        catch
        {
            ClearMergeBackground();
        }
        finally
        {
            foreach (var container in hidden)
                container.Visibility = Visibility.Visible;
            ElementHost.UpdateLayout();
        }
    }

    private void ClearMergeBackground()
    {
        _mergePixels = null;
        _mergeWidth = _mergeHeight = _mergeStride = 0;
    }

    private Color SampleMergeColor(Point pos, bool imageTarget)
    {
        if (imageTarget && _mergePixels != null && _mergeWidth > 0 && _mergeHeight > 0)
        {
            int x = (int)Math.Floor((pos.X - _doc.ArtboardX) * _mergeWidth / Math.Max(1, _doc.ArtboardWidth));
            int y = (int)Math.Floor((pos.Y - _doc.ArtboardY) * _mergeHeight / Math.Max(1, _doc.ArtboardHeight));
            var color = BrushRaster.Sample(_mergePixels, _mergeWidth, _mergeHeight, x, y);
            if (color.A > 0) return color;
        }

        if (_doc.CanvasPaintLayer is { Source: BitmapSource paint } layer)
        {
            var art = _doc.ArtboardRect;
            int x = (int)Math.Floor((pos.X - layer.X) * paint.PixelWidth / Math.Max(1, layer.Width));
            int y = (int)Math.Floor((pos.Y - layer.Y) * paint.PixelHeight / Math.Max(1, layer.Height));
            var color = BrushRaster.Sample(paint, x, y);
            if (color.A > 0) return color;
        }

        return _mergeFallback;
    }

    private void TryPickBrushColor(Point pos, ImageElement? image, Point sourcePoint, bool canvas)
    {
        Color? sampled = null;
        if (image?.DisplaySource is BitmapSource bmp)
        {
            sampled = BrushRaster.Sample(bmp, (int)sourcePoint.X, (int)sourcePoint.Y);
        }
        else if (canvas)
        {
            if (_doc.CanvasPaintLayer is { Source: BitmapSource paint } layer)
            {
                int x = (int)Math.Floor((pos.X - layer.X) * paint.PixelWidth / Math.Max(1, layer.Width));
                int y = (int)Math.Floor((pos.Y - layer.Y) * paint.PixelHeight / Math.Max(1, layer.Height));
                var color = BrushRaster.Sample(paint, x, y);
                if (color.A > 0) sampled = color;
            }
            if (sampled == null) sampled = _doc.CanvasColor;
        }

        if (sampled is not { A: > 0 } picked)
        {
            StatusText.Text = "Nothing to sample under the cursor.";
            return;
        }

        _brushSettings.Color = Color.FromRgb(picked.R, picked.G, picked.B);
        AttrBrushColorBtn.Background = new SolidColorBrush(_brushSettings.Color);
        MarkBrushCustom();
        StatusText.Text = $"Brush color: {ToHex(_brushSettings.Color)}.";
    }

    // =================================================================
    // Inspector
    // =================================================================
    // Inspector
    // =================================================================

    /// <summary>Drag the divider to widen / narrow the inspector.</summary>
    private void InspectorSplitter_DragDelta(object sender, DragDeltaEventArgs e)
    {
        double min = 190;
        double max = Math.Max(min, ActualWidth * 0.65);
        InspectorBorder.Width = Math.Clamp(InspectorBorder.Width - e.HorizontalChange, min, max);
    }

    private void RefreshInspector()
    {
        _suppressInspector = true;
        try
        {
            var s = _doc.Selected;
            bool multi = _doc.SelectionCount > 1;
            var group = multi ? _doc.SelectionBounds : s?.Bounds ?? Rect.Empty;

            MultiSelectionCard.Visibility = multi ? Visibility.Visible : Visibility.Collapsed;
            if (multi)
                MultiSelectionText.Text = $"{_doc.SelectionCount} objects selected · {group.Width:F0} × {group.Height:F0} px";

            InspectorPanel.IsEnabled = _doc.SelectionCount == 1;
            InspX.SetValue(group.IsEmpty ? 0 : group.X);
            InspY.SetValue(group.IsEmpty ? 0 : group.Y);
            InspW.SetValue(group.IsEmpty ? 0 : group.Width);
            InspH.SetValue(group.IsEmpty ? 0 : group.Height);
            double rot = s?.RotationAngle ?? 0;
            rot = ((rot + 180) % 360 + 360) % 360 - 180; // show as -180..180
            InspRot.SetValue(rot);
            InspRadius.SetValue(s switch
            {
                ShapeElement sh => sh.CornerRadius,
                ImageElement im => im.CornerRadius,
                _ => 0,
            });
            bool individualCorners = s?.UseIndividualCornerRadii == true;
            var radii = s?.IndividualCornerRadii ?? new CornerRadius();
            InspIndividualCornersCheck.IsChecked = individualCorners;
            InspIndividualCornersPanel.Visibility = individualCorners ? Visibility.Visible : Visibility.Collapsed;
            InspCornerTopLeft.SetValue(radii.TopLeft);
            InspCornerTopRight.SetValue(radii.TopRight);
            InspCornerBottomRight.SetValue(radii.BottomRight);
            InspCornerBottomLeft.SetValue(radii.BottomLeft);
            InspRatioLockBtn.IsChecked = _lockAspectRatio;
            AttrOpacity.SetValue(s?.Opacity ?? 0);
            AttrCorner.SetValue(s switch
            {
                ShapeElement sh => sh.CornerRadius,
                ImageElement im => im.CornerRadius,
                _ => 0,
            });
            AttrCornerIndividualCheck.IsChecked = individualCorners;
            AttrCornerIndividualPanel.Visibility = individualCorners ? Visibility.Visible : Visibility.Collapsed;
            AttrCornerTopLeft.SetValue(radii.TopLeft);
            AttrCornerTopRight.SetValue(radii.TopRight);
            AttrCornerBottomRight.SetValue(radii.BottomRight);
            AttrCornerBottomLeft.SetValue(radii.BottomLeft);
            AttrSoft.SetValue(s?.SoftEdge ?? 0);
            bool canCorner = s is ImageElement || s is ShapeElement { Kind: ShapeKind.Rectangle };
            AttrOpacity.IsEnabled = AttrSoft.IsEnabled = s != null;
            AttrCornerIndividualCheck.IsEnabled = canCorner;
            AttrCorner.IsEnabled = canCorner && !individualCorners;
            AttrCorner.Opacity = canCorner ? 1.0 : 0.35;
            InspIndividualCornersCheck.IsEnabled = canCorner;
            InspRadius.IsEnabled = canCorner && !individualCorners;
            InspRadius.Opacity = canCorner ? 1.0 : 0.35;
            RefreshAppearance();
            RefreshImage();
            RefreshShadow();
            RefreshText();
            RefreshTransparency();
            RefreshPhotoPanel();
            RefreshGridPanel();
            UpdateRightTabs();
        }
        finally { _suppressInspector = false; }
    }

    /// <summary>Programmatic text update that never yanks a field the user is typing in.</summary>
    private static void SetText(TextBox box, string text)
    {
        if (!box.IsKeyboardFocused) box.Text = text;
    }

    // ---------- Photo tab (non-destructive image adjustments) ----------

    private void InspectorTab_Click(object sender, RoutedEventArgs e) =>
        SetRightPanelMode(RightPanelMode.Inspector);

    private void PhotoTab_Click(object sender, RoutedEventArgs e)
    {
        if (!PhotoTabBtn.IsEnabled)
        {
            SetRightPanelMode(RightPanelMode.Inspector);
            return;
        }
        SetRightPanelMode(RightPanelMode.Photo);
        RefreshPhotoPanel();
    }

    private void GridTab_Click(object sender, RoutedEventArgs e)
    {
        if (!GridTabBtn.IsEnabled)
        {
            SetRightPanelMode(RightPanelMode.Inspector);
            return;
        }
        SetRightPanelMode(RightPanelMode.Grid);
        RefreshGridPanel();
    }

    private void SetRightPanelMode(RightPanelMode mode)
    {
        if (mode == RightPanelMode.Photo && !PhotoTabBtn.IsEnabled)
            mode = RightPanelMode.Inspector;
        if (mode == RightPanelMode.Grid && !GridTabBtn.IsEnabled)
            mode = RightPanelMode.Inspector;

        _rightPanelMode = mode;
        InspectorTabBtn.IsChecked = mode == RightPanelMode.Inspector;
        PhotoTabBtn.IsChecked = mode == RightPanelMode.Photo;
        GridTabBtn.IsChecked = mode == RightPanelMode.Grid;
        InspectorPanel.Visibility = mode == RightPanelMode.Inspector ? Visibility.Visible : Visibility.Collapsed;
        PhotoPanel.Visibility = mode == RightPanelMode.Photo ? Visibility.Visible : Visibility.Collapsed;
        GridPanel.Visibility = mode == RightPanelMode.Grid ? Visibility.Visible : Visibility.Collapsed;
    }

    private void RefreshPhotoPanel()
    {
        _suppressPhoto = true;
        try
        {
            var im = _doc.Selected as ImageElement;
            bool isImage = _doc.SelectionCount == 1 && im != null;
            bool hasImage = im?.Source is BitmapSource;

            PhotoContent.Visibility = isImage ? Visibility.Visible : Visibility.Collapsed;
            PhotoEmptyText.Visibility = isImage ? Visibility.Collapsed : Visibility.Visible;
            if (im is null) return;

            PhotoContent.IsEnabled = hasImage;
            var a = im.Adjustments;
            PhotoExposure.SetValue(a.Exposure);
            PhotoContrast.SetValue(a.Contrast);
            PhotoHighlights.SetValue(a.Highlights);
            PhotoShadows.SetValue(a.Shadows);
            PhotoWhites.SetValue(a.Whites);
            PhotoBlacks.SetValue(a.Blacks);
            PhotoTemperature.SetValue(a.Temperature);
            PhotoTint.SetValue(a.Tint);
            PhotoVibrance.SetValue(a.Vibrance);
            PhotoSaturation.SetValue(a.Saturation);
            PhotoClarity.SetValue(a.Clarity);
            PhotoVignette.SetValue(a.Vignette);
            PhotoGrain.SetValue(a.Grain);
            PhotoBlur.SetValue(a.Blur);
            PhotoSharpen.SetValue(a.Sharpen);
            PhotoGlow.SetValue(a.Glow);
            PhotoToneAmount.SetValue(a.ToneAmount);
            PhotoToneColorBox.Background = new SolidColorBrush(a.ToneColor);
            PhotoIntensity.SetValue(a.Intensity);

            PhotoMetaText.Text = BuildPhotoMeta(im);
            UpdatePhotoHistogram(im.DisplaySource as BitmapSource);

            if (!ReferenceEquals(_photoPresetSource, im.Source) ||
                _photoPresets.Any(p => p.Preview is null))
            {
                _photoPresetSource = im.Source;
                RefreshPhotoPresetPreviews(im);
            }
        }
        finally { _suppressPhoto = false; }
    }

    private void RefreshGridPanel()
    {
        _suppressGrid = true;
        try
        {
            CanvasBgSwatchBtn.Background = new SolidColorBrush(_doc.CanvasColor);
            CanvasTransparentCheck.IsChecked = _doc.CanvasTransparent;
            GridShowCheck.IsChecked = GridToggleBtn.IsChecked == true;
            GridCenterCheck.IsChecked = GuideOverlay.ShowCenterLine;
            GridColumns.SetValue(GuideOverlay.Columns);
            GridMargin.SetValue(GuideOverlay.Margin);
            GridGutter.SetValue(GuideOverlay.Gutter);
            GridOpacity.SetValue(GuideOverlay.GridOpacity);
        }
        finally { _suppressGrid = false; }
    }

    private void UpdateRightTabs()
    {
        bool hasSelection = _doc.SelectionCount > 0;
        bool showPhoto = _doc.SelectionCount == 1 && _doc.Selected is ImageElement;
        bool showGrid = _doc.SelectionCount == 0 && _tabs.Count > 0;

        InspectorTabBtn.Visibility = hasSelection ? Visibility.Visible : Visibility.Collapsed;
        PhotoTabBtn.Visibility = showPhoto ? Visibility.Visible : Visibility.Collapsed;
        GridTabBtn.Visibility = showGrid ? Visibility.Visible : Visibility.Collapsed;
        PhotoTabBtn.IsEnabled = showPhoto;
        GridTabBtn.IsEnabled = showGrid;
        RightTabs.Columns = Math.Max(1, (hasSelection ? 1 : 0) + (showPhoto ? 1 : 0) + (showGrid ? 1 : 0));

        if (showGrid && _rightPanelMode != RightPanelMode.Grid)
            SetRightPanelMode(RightPanelMode.Grid);
        else if (!showGrid && _rightPanelMode == RightPanelMode.Grid)
            SetRightPanelMode(RightPanelMode.Inspector);
        else if (!showPhoto && _rightPanelMode == RightPanelMode.Photo)
            SetRightPanelMode(RightPanelMode.Inspector);
    }

    private void GridShow_Toggled(object sender, RoutedEventArgs e)
    {
        if (_suppressGrid) return;
        GridToggleBtn.IsChecked = GridShowCheck.IsChecked == true;
        GridToggle_Click(GridToggleBtn, new RoutedEventArgs());
    }

    private void GridCenter_Toggled(object sender, RoutedEventArgs e)
    {
        if (_suppressGrid) return;
        GuideOverlay.ShowCenterLine = GridCenterCheck.IsChecked == true;
        GuideOverlay.InvalidateVisual();
    }

    private void GridColumns_ValueChanged(object? sender, EventArgs e)
    {
        if (_suppressGrid) return;
        GuideOverlay.Columns = (int)Math.Round(GridColumns.Value);
        GuideOverlay.InvalidateVisual();
    }

    private void GridMargin_ValueChanged(object? sender, EventArgs e)
    {
        if (_suppressGrid) return;
        GuideOverlay.Margin = GridMargin.Value;
        GuideOverlay.InvalidateVisual();
    }

    private void GridGutter_ValueChanged(object? sender, EventArgs e)
    {
        if (_suppressGrid) return;
        GuideOverlay.Gutter = GridGutter.Value;
        GuideOverlay.InvalidateVisual();
    }

    private void GridOpacity_ValueChanged(object? sender, EventArgs e)
    {
        if (_suppressGrid) return;
        GuideOverlay.GridOpacity = GridOpacity.Value;
        GuideOverlay.InvalidateVisual();
    }

    private void OpenGridSettings_Click(object sender, RoutedEventArgs e)
    {
        if (_tabs.Count == 0) return;
        if (_doc.SelectionCount > 0) _doc.Select(null);
        RefreshGridPanel();
        SetRightPanelMode(RightPanelMode.Grid);
    }

    private static string BuildPhotoMeta(ImageElement im)
    {
        string dimensions = im.Source is BitmapSource bmp
            ? $"{bmp.PixelWidth} × {bmp.PixelHeight} px"
            : "No image";
        string name = string.IsNullOrWhiteSpace(im.SourcePath)
            ? "Embedded image"
            : Path.GetFileName(im.SourcePath);
        return $"{dimensions}  ·  {name}";
    }

    private void RefreshPhotoPresetPreviews(ImageElement im)
    {
        var source = im.Source as BitmapSource ?? PhotoAdjustmentService.Placeholder;
        var thumb = PhotoAdjustmentService.Render(source, new PhotoAdjustments(), 128);
        if (thumb == null) return;

        foreach (var preset in _photoPresets)
        {
            var adjustments = preset.IsAuto
                ? PhotoAdjustmentService.BuildAuto(thumb)
                : preset.Adjustments;
            preset.Preview = PhotoAdjustmentService.Render(thumb, adjustments) ?? thumb;
        }
    }

    private void UpdatePhotoHistogram(BitmapSource? source)
    {
        const int bins = 96;
        const double width = 220;
        const double height = 64;

        if (source is null)
        {
            PhotoHistR.Points = new PointCollection();
            PhotoHistG.Points = new PointCollection();
            PhotoHistB.Points = new PointCollection();
            return;
        }

        var (r, g, b) = PhotoAdjustmentService.Histogram(source, bins);
        PhotoHistR.Points = BuildHistogramPoints(r, width, height);
        PhotoHistG.Points = BuildHistogramPoints(g, width, height);
        PhotoHistB.Points = BuildHistogramPoints(b, width, height);
    }

    private static PointCollection BuildHistogramPoints(float[] values, double width, double height)
    {
        var points = new PointCollection(values.Length);
        for (int i = 0; i < values.Length; i++)
        {
            double x = values.Length <= 1 ? 0 : i * (width - 1) / (values.Length - 1);
            double y = height - 1 - values[i] * (height - 2);
            points.Add(new Point(x, y));
        }
        return points;
    }

    private void PhotoAdjust_ValueChanged(object? sender, EventArgs e)
    {
        if (_suppressPhoto || sender is not ValueSlider { Tag: string key } slider) return;
        if (_doc.Selected is not ImageElement im || im.Source is not BitmapSource) return;
        if (!TryGetPhotoValue(im.Adjustments, key, out double current)) return;
        if (Math.Abs(current - slider.Value) < 0.0001) return;

        ArmInspectorUndo();
        SetPhotoValue(im.Adjustments, key, slider.Value);
        im.RefreshPhotoPreview();
        UpdatePhotoHistogram(im.DisplaySource as BitmapSource);
    }

    private static bool TryGetPhotoValue(PhotoAdjustments a, string key, out double value)
    {
        value = key switch
        {
            nameof(PhotoAdjustments.Exposure) => a.Exposure,
            nameof(PhotoAdjustments.Contrast) => a.Contrast,
            nameof(PhotoAdjustments.Highlights) => a.Highlights,
            nameof(PhotoAdjustments.Shadows) => a.Shadows,
            nameof(PhotoAdjustments.Whites) => a.Whites,
            nameof(PhotoAdjustments.Blacks) => a.Blacks,
            nameof(PhotoAdjustments.Temperature) => a.Temperature,
            nameof(PhotoAdjustments.Tint) => a.Tint,
            nameof(PhotoAdjustments.Vibrance) => a.Vibrance,
            nameof(PhotoAdjustments.Saturation) => a.Saturation,
            nameof(PhotoAdjustments.Clarity) => a.Clarity,
            nameof(PhotoAdjustments.Vignette) => a.Vignette,
            nameof(PhotoAdjustments.Grain) => a.Grain,
            nameof(PhotoAdjustments.Blur) => a.Blur,
            nameof(PhotoAdjustments.Sharpen) => a.Sharpen,
            nameof(PhotoAdjustments.Glow) => a.Glow,
            nameof(PhotoAdjustments.ToneAmount) => a.ToneAmount,
            nameof(PhotoAdjustments.Intensity) => a.Intensity,
            _ => double.NaN,
        };
        return !double.IsNaN(value);
    }

    private static void SetPhotoValue(PhotoAdjustments a, string key, double value)
    {
        switch (key)
        {
            case nameof(PhotoAdjustments.Exposure): a.Exposure = value; break;
            case nameof(PhotoAdjustments.Contrast): a.Contrast = value; break;
            case nameof(PhotoAdjustments.Highlights): a.Highlights = value; break;
            case nameof(PhotoAdjustments.Shadows): a.Shadows = value; break;
            case nameof(PhotoAdjustments.Whites): a.Whites = value; break;
            case nameof(PhotoAdjustments.Blacks): a.Blacks = value; break;
            case nameof(PhotoAdjustments.Temperature): a.Temperature = value; break;
            case nameof(PhotoAdjustments.Tint): a.Tint = value; break;
            case nameof(PhotoAdjustments.Vibrance): a.Vibrance = value; break;
            case nameof(PhotoAdjustments.Saturation): a.Saturation = value; break;
            case nameof(PhotoAdjustments.Clarity): a.Clarity = value; break;
            case nameof(PhotoAdjustments.Vignette): a.Vignette = value; break;
            case nameof(PhotoAdjustments.Grain): a.Grain = value; break;
            case nameof(PhotoAdjustments.Blur): a.Blur = value; break;
            case nameof(PhotoAdjustments.Sharpen): a.Sharpen = value; break;
            case nameof(PhotoAdjustments.Glow): a.Glow = value; break;
            case nameof(PhotoAdjustments.ToneAmount): a.ToneAmount = value; break;
            case nameof(PhotoAdjustments.Intensity): a.Intensity = value; break;
        }
    }

    private void PhotoSection_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not ToggleButton { Tag: string tag } toggle) return;
        var content = tag switch
        {
            "Light" => PhotoLightContent,
            "Color" => PhotoColorContent,
            "Tone" => PhotoToneContent,
            "Effects" => PhotoEffectsContent,
            "Presets" => PhotoPresetsContent,
            _ => null,
        };
        if (content != null)
            content.Visibility = toggle.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
    }

    private void PhotoPreset_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: PhotoPreset preset })
            ApplyPhotoPreset(preset);
    }

    private void PhotoQuickAuto_Click(object sender, RoutedEventArgs e) => ApplyPhotoPresetByName("Auto");
    private void PhotoQuickBw_Click(object sender, RoutedEventArgs e) => ApplyPhotoPresetByName("B&W");
    private void PhotoQuickHdr_Click(object sender, RoutedEventArgs e) => ApplyPhotoPresetByName("HDR");

    private void ApplyPhotoPresetByName(string name)
    {
        var preset = _photoPresets.FirstOrDefault(p => p.Name == name);
        if (preset != null) ApplyPhotoPreset(preset);
    }

    private void ApplyPhotoPreset(PhotoPreset preset)
    {
        if (_doc.Selected is not ImageElement im || im.Source is not BitmapSource source) return;

        PushUndo();
        _inspectorArmed = false;
        im.Adjustments.CopyFrom(preset.IsAuto
            ? PhotoAdjustmentService.BuildAuto(source)
            : preset.Adjustments);
        im.RefreshPhotoPreview();
        RefreshPhotoPanel();
        StatusText.Text = $"Photo preset: {preset.Name}.";
    }

    private void PhotoToneColor_Click(object sender, RoutedEventArgs e)
    {
        if (_doc.Selected is not ImageElement im) return;

        var adjustments = im.Adjustments;
        var dlg = new ColorPickerDialog(adjustments.ToneColor, CanvasColorsWithPrimary())
        {
            Owner = this,
        };
        if (dlg.ShowDialog() != true) return;

        PushUndo();
        _inspectorArmed = false;
        adjustments.ToneColor = dlg.SelectedColor;
        if (adjustments.ToneAmount <= 0) adjustments.ToneAmount = 60;
        im.RefreshPhotoPreview();
        RefreshPhotoPanel();
        StatusText.Text = $"Tone color set to {ToHex(adjustments.ToneColor)}.";
    }

    private void PhotoReset_Click(object sender, RoutedEventArgs e)
    {
        if (_doc.Selected is not ImageElement im || im.Adjustments.IsDefault) return;

        PushUndo();
        _inspectorArmed = false;
        im.Adjustments.Reset();
        im.RefreshPhotoPreview();
        RefreshPhotoPanel();
        StatusText.Text = "Photo adjustments reset.";
    }

    // ---------- appearance (shape background / border) ----------

    private ShapeElement? SelectedShape => _doc.Selected as ShapeElement;

    private static string ToHex(System.Windows.Media.Color c) => $"#{c.R:X2}{c.G:X2}{c.B:X2}";

    private static bool TryParseColor(string? s, out System.Windows.Media.Color color)
    {
        color = System.Windows.Media.Colors.Black;
        if (string.IsNullOrWhiteSpace(s)) return false;
        try
        {
            var c = System.Windows.Media.ColorConverter.ConvertFromString(s.Trim());
            if (c is System.Windows.Media.Color mc) { color = mc; return true; }
            return false;
        }
        catch { return false; }
    }

    private void RefreshAppearance()
    {
        var sh = SelectedShape;
        AppearanceCard.Visibility = sh == null ? Visibility.Collapsed : Visibility.Visible;
        if (sh == null) return;

        FillSolidRadio.IsChecked = !sh.IsGradient;
        FillGradientRadio.IsChecked = sh.IsGradient;
        Fill1Box.Background = new SolidColorBrush(sh.FillColor1);
        SetText(Fill1Text, ToHex(sh.FillColor1));
        Fill2Box.Background = new SolidColorBrush(sh.FillColor2);
        SetText(Fill2Text, ToHex(sh.FillColor2));
        InspGradAngle.SetValue(sh.GradientAngle);
        UpdateGradientDirectionButtons(ShapeGradDirections, sh.GradientAngle);
        GradTypeCombo.SelectedIndex = (int)sh.GradientType;
        StrokeBox.Background = new SolidColorBrush(sh.StrokeColor);
        SetText(StrokeText, ToHex(sh.StrokeColor));
        InspStrokeThick.SetValue(sh.StrokeThickness);

        UpdateAppearanceEnabled(sh.IsGradient, sh.GradientType);
    }

    private void RefreshImage()
    {
        var im = _doc.Selected as ImageElement;
        ImageCard.Visibility = im == null ? Visibility.Collapsed : Visibility.Visible;
        if (im == null) return;

        ImageFitFillRadio.IsChecked = im.FitMode == ImageFitMode.Fill;
        ImageFitFitRadio.IsChecked = im.FitMode == ImageFitMode.Fit;
        ImageFitStretchRadio.IsChecked = im.FitMode == ImageFitMode.Stretch;
    }

    private void ImageFit_Checked(object sender, RoutedEventArgs e)
    {
        if (_suppressInspector || _doc.Selected is not ImageElement im) return;
        if (sender is not RadioButton { Tag: string tag } || !Enum.TryParse<ImageFitMode>(tag, out var mode))
            return;
        if (im.FitMode == mode) return;

        PushUndo();
        _inspectorArmed = false;
        im.FitMode = mode;
    }

    /// <summary>
    /// Enable the gradient-only controls for the current fill mode. The angle
    /// only applies to the linear ramp; radial / elliptical hide it.
    /// </summary>
    private void UpdateAppearanceEnabled(bool grad, GradientType type)
    {
        GradientOptions.Visibility = grad ? Visibility.Visible : Visibility.Collapsed;
        GradAngleRow.Visibility = grad && type == GradientType.Linear
            ? Visibility.Visible
            : Visibility.Collapsed;
        Fill1Label.Text = grad ? "Color 1" : "Color";
    }

    private void FillMode_Checked(object sender, RoutedEventArgs e)
    {
        if (_suppressInspector || SelectedShape is not { } sh) return;
        PushUndo();
        sh.IsGradient = FillGradientRadio.IsChecked == true;
        UpdateAppearanceEnabled(sh.IsGradient, sh.GradientType);
    }

    private void GradTypeCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressInspector || SelectedShape is not { } sh) return;
        if (GradTypeCombo.SelectedIndex < 0) return;
        var type = (GradientType)GradTypeCombo.SelectedIndex;
        if (sh.GradientType == type) return;
        PushUndo();
        sh.GradientType = type;
        UpdateAppearanceEnabled(sh.IsGradient, type);
    }

    // ---------- shadow (any element) ----------

    private static ShadowPresetItem ShadowPreset(string name, ShadowStyle style)
    {
        var temp = new ShapeElement();
        temp.ApplyShadowPreset(style);
        var effect = temp.ShadowEffect;
        DropShadowEffect? preview = null;
        if (effect != null)
        {
            preview = new DropShadowEffect
            {
                Direction = effect.Direction,
                ShadowDepth = effect.ShadowDepth == 0 ? 0 : Math.Max(2, effect.ShadowDepth * 0.6),
                BlurRadius = Math.Max(4, effect.BlurRadius * 0.6),
                Opacity = Math.Min(0.95, effect.Opacity * 1.5 + 0.12),
                Color = effect.Color,
                RenderingBias = RenderingBias.Performance,
            };
            preview.Freeze();
        }
        return new ShadowPresetItem(name, style, preview);
    }

    private static TransparencyPresetItem TransparencyPreset(
        string name, string toolTip, OpacityFadeDirection direction, Point start, Point end)
    {
        var brush = new LinearGradientBrush
        {
            StartPoint = start,
            EndPoint = end,
            GradientStops =
            {
                new GradientStop(Color.FromArgb(255, 230, 238, 246), 0),
                new GradientStop(Color.FromArgb(24, 230, 238, 246), 1),
            },
        };
        brush.Freeze();
        return new TransparencyPresetItem(name, toolTip, direction, brush);
    }

    private void RefreshShadow()
    {
        var el = _doc.Selected;
        bool isText = el is TextElement;
        ShadowCard.Visibility = el == null || isText ? Visibility.Collapsed : Visibility.Visible;
        if (el == null || isText) return;

        RefreshShadowPresetSelection(el);
        ShadowColorBox.Background = new SolidColorBrush(el.ShadowColor);
        SetText(ShadowText, ToHex(el.ShadowColor));
        ShadowX.SetValue(el.ShadowX);
        ShadowY.SetValue(el.ShadowY);
        ShadowBlur.SetValue(el.ShadowBlur);
        ShadowOpacity.SetValue(el.ShadowOpacity);
        UpdateShadowVisibility(el.ShadowStyle != ShadowStyle.None);
    }

    private void RefreshShadowPresetSelection(DesignElement? el)
    {
        foreach (var item in _shadowPresets)
            item.IsActive = el != null && item.Style == el.ShadowStyle;
    }

    private void ShadowPresetItem_Click(object sender, RoutedEventArgs e)
    {
        if (_suppressInspector || _doc.Selected is not { } el) return;
        if (sender is not Button { Tag: ShadowPresetItem item }) return;
        if (el.ShadowStyle == item.Style) return;

        PushUndo();
        _inspectorArmed = false;
        el.ApplyShadowPreset(item.Style);
        RefreshInspector();
        StatusText.Text = item.Style == ShadowStyle.None
            ? "Shadow removed."
            : $"Shadow: {item.Name}.";
    }

    private void UpdateShadowVisibility(bool on) =>
        ShadowOptions.Visibility = on ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>Free-hand edit: switch the preset labels to Custom without re-applying a look.</summary>
    private void MarkShadowCustom(DesignElement el)
    {
        if (el.ShadowStyle is ShadowStyle.Custom or ShadowStyle.None) return;
        el.ShadowStyle = ShadowStyle.Custom;
        RefreshShadowPresetSelection(el);
    }

    private void ShadowX_ValueChanged(object? sender, EventArgs e)
    {
        if (_suppressInspector || _doc.Selected is not { } el) return;
        ArmInspectorUndo();
        el.ShadowX = ShadowX.Value;
        MarkShadowCustom(el);
    }

    private void ShadowY_ValueChanged(object? sender, EventArgs e)
    {
        if (_suppressInspector || _doc.Selected is not { } el) return;
        ArmInspectorUndo();
        el.ShadowY = ShadowY.Value;
        MarkShadowCustom(el);
    }

    private void ShadowBlur_ValueChanged(object? sender, EventArgs e)
    {
        if (_suppressInspector || _doc.Selected is not { } el) return;
        ArmInspectorUndo();
        el.ShadowBlur = ShadowBlur.Value;
        MarkShadowCustom(el);
    }

    private void ShadowOpacity_ValueChanged(object? sender, EventArgs e)
    {
        if (_suppressInspector || _doc.Selected is not { } el) return;
        ArmInspectorUndo();
        el.ShadowOpacity = ShadowOpacity.Value;
        MarkShadowCustom(el);
    }

    // ---------- text ----------

    private void RefreshTransparency()
    {
        var el = _doc.Selected;
        TransparencyCard.Visibility = el == null ? Visibility.Collapsed : Visibility.Visible;
        if (el == null) return;
        OpacityGradientCheck.IsChecked = el.UseOpacityGradient;
        OpacityOptions.Visibility = el.UseOpacityGradient ? Visibility.Visible : Visibility.Collapsed;
        RefreshTransparencyPresetSelection(el);
        OpacityStrength.SetValue(el.OpacityStrength);
        OpacityStart.SetValue(el.OpacityStart);
        OpacityEnd.SetValue(el.OpacityEnd);
    }

    private void RefreshTransparencyPresetSelection(DesignElement? el)
    {
        foreach (var item in _transparencyPresets)
            item.IsActive = el != null && el.UseOpacityGradient && item.Direction == el.OpacityDirection;
    }

    private void TransparencyPresetItem_Click(object sender, RoutedEventArgs e)
    {
        if (_suppressInspector || _doc.Selected is not { } el) return;
        if (sender is not Button { Tag: TransparencyPresetItem item }) return;

        PushUndo();
        _inspectorArmed = false;
        el.UseOpacityGradient = true;
        el.OpacityDirection = item.Direction;
        _suppressInspector = true;
        OpacityGradientCheck.IsChecked = true;
        _suppressInspector = false;
        RefreshInspector();
        StatusText.Text = $"Transparency: {item.ToolTip}.";
    }

    private void Transparency_Click(object sender, RoutedEventArgs e) =>
        TransparencyContent.Visibility = TransparencyToggle.IsChecked == true
            ? Visibility.Visible
            : Visibility.Collapsed;

    private void OpacityGradient_Toggled(object sender, RoutedEventArgs e)
    {
        if (_suppressInspector || _doc.Selected is not { } el) return;
        PushUndo();
        el.UseOpacityGradient = OpacityGradientCheck.IsChecked == true;
        if (el.UseOpacityGradient)
        {
            // Sensible first fade for elements saved before this feature.
            if (el.OpacityEnd <= el.OpacityStart) el.OpacityEnd = 100;
            if (el.OpacityStrength <= 0) el.OpacityStrength = 100;
            OpacityEnd.SetValue(el.OpacityEnd);
            OpacityStrength.SetValue(el.OpacityStrength);
        }
        OpacityOptions.Visibility = el.UseOpacityGradient ? Visibility.Visible : Visibility.Collapsed;
        RefreshTransparencyPresetSelection(el);
    }

    private void OpacityStrength_ValueChanged(object? sender, EventArgs e)
    {
        if (_suppressInspector || _doc.Selected is not { } el) return;
        ArmInspectorUndo();
        el.OpacityStrength = OpacityStrength.Value;
    }

    private void OpacityStart_ValueChanged(object? sender, EventArgs e)
    {
        if (_suppressInspector || _doc.Selected is not { } el) return;
        ArmInspectorUndo();
        el.OpacityStart = OpacityStart.Value;
    }

    private void OpacityEnd_ValueChanged(object? sender, EventArgs e)
    {
        if (_suppressInspector || _doc.Selected is not { } el) return;
        ArmInspectorUndo();
        el.OpacityEnd = OpacityEnd.Value;
    }

    private void RefreshText()
    {
        var te = _doc.Selected as TextElement;
        TextCard.Visibility = te == null ? Visibility.Collapsed : Visibility.Visible;
        TextFillCard.Visibility = te == null ? Visibility.Collapsed : Visibility.Visible;
        TextExtraCard.Visibility = te == null ? Visibility.Collapsed : Visibility.Visible;
        InspCornerRow.Visibility = te == null ? Visibility.Visible : Visibility.Collapsed;
        if (te == null) return;

        TextFontCombo.SelectedItem = te.FontFamilyName;
        TextSize.SetValue(te.FontSize);
        bool wrap = te.WrapWidth > 0;
        TextWrapCheck.IsChecked = wrap;
        TextWrapRow.Visibility = wrap ? Visibility.Visible : Visibility.Collapsed;
        TextWrapWidth.SetValue(wrap ? te.WrapWidth : 400);
        TextBoldBtn.IsChecked = te.IsBold;
        TextItalicBtn.IsChecked = te.IsItalic;
        TextUnderlineBtn.IsChecked = te.IsUnderline;
        TextStrikeBtn.IsChecked = te.IsStrikethrough;

        TextFillSolidRadio.IsChecked = !te.IsGradient;
        TextFillGradientRadio.IsChecked = te.IsGradient;
        TextColor1Box.Background = new SolidColorBrush(te.TextColor1);
        SetText(TextColor1Text, ToHex(te.TextColor1));
        TextColor2Box.Background = new SolidColorBrush(te.TextColor2);
        SetText(TextColor2Text, ToHex(te.TextColor2));
        TextGradientOptions.Visibility = te.IsGradient ? Visibility.Visible : Visibility.Collapsed;
        TextGradAngle.SetValue(te.TextGradientAngle);
        UpdateGradientDirectionButtons(TextGradDirections, te.TextGradientAngle);

        TextAlignLeftBtn.IsChecked = te.Alignment == TextAlign.Left;
        TextAlignCenterBtn.IsChecked = te.Alignment == TextAlign.Center;
        TextAlignRightBtn.IsChecked = te.Alignment == TextAlign.Right;
        TextLetterSpacing.SetValue(te.LetterSpacing);
        TextLineSpacing.SetValue(te.LineSpacing);

        bool shadow = te.ShadowStyle != ShadowStyle.None;
        TextShadowCheck.IsChecked = shadow;
        TextShadowOptions.Visibility = shadow ? Visibility.Visible : Visibility.Collapsed;
        TextShadowColorBox.Background = new SolidColorBrush(te.ShadowColor);
        SetText(TextShadowText, ToHex(te.ShadowColor));
        TextShadowX.SetValue(te.ShadowX);
        TextShadowY.SetValue(te.ShadowY);
        TextShadowBlur.SetValue(te.ShadowBlur);
        TextShadowOpacity.SetValue(te.ShadowOpacity);
    }

    private void TextFontSearch_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_suppressInspector) return;
        ApplyFontFilter(TextFontSearch.Text);
    }

    private void ApplyFontFilter(string query)
    {
        if (TextFontCombo == null) return;
        string? current = (_doc.Selected as TextElement)?.FontFamilyName
                          ?? TextFontCombo.SelectedItem as string;
        IEnumerable<string> filtered = string.IsNullOrWhiteSpace(query)
            ? _allFonts
            : _allFonts.Where(f => f.Contains(query.Trim(), StringComparison.CurrentCultureIgnoreCase));

        bool prev = _suppressInspector;
        _suppressInspector = true;
        TextFontCombo.ItemsSource = filtered.ToList();
        TextFontCombo.SelectedItem = current != null && filtered.Contains(current) ? current : null;
        _suppressInspector = prev;
    }

    private void ClearFontSearch()
    {
        if (TextFontSearch.Text.Length > 0) TextFontSearch.Text = "";
    }

    private void TextExtra_Click(object sender, RoutedEventArgs e) =>
        TextExtraContent.Visibility = TextExtraToggle.IsChecked == true
            ? Visibility.Visible
            : Visibility.Collapsed;

    private void TextFont_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressInspector || _doc.Selected is not TextElement te) return;
        if (TextFontCombo.SelectedItem is not string name) return;
        if (te.FontFamilyName == name) return;
        if (!_fontUndoPushed) { PushUndo(); _fontUndoPushed = true; }
        _fontPreviewed = true;
        te.FontFamilyName = name;
        UpdateTextEditorStyle();
    }

    private void TextFont_DropDownOpened(object? sender, EventArgs e)
    {
        _fontOriginal = (_doc.Selected as TextElement)?.FontFamilyName;
        _fontPreviewed = false;
        _fontCommitted = false;
        _fontUndoPushed = false;
    }

    private void TextFont_DropDownClosed(object? sender, EventArgs e)
    {
        bool committed = _fontCommitted;
        if (!committed && _fontPreviewed && _fontOriginal != null
            && _doc.Selected is TextElement te)
        {
            te.FontFamilyName = _fontOriginal; // cancelled: restore the pre-preview font
            UpdateTextEditorStyle();
        }
        _fontOriginal = null;
        _fontPreviewed = false;
        _fontCommitted = false;
        _fontUndoPushed = false;
        if (committed) ClearFontSearch();
        if (_editingText != null) _textEditor?.Focus();
    }

    private void FontItem_MouseEnter(object sender, MouseEventArgs e)
    {
        if (!TextFontCombo.IsDropDownOpen) return;
        if (sender is ComboBoxItem { DataContext: string font })
            TextFontCombo.SelectedItem = font; // live preview on hover
    }

    private void FontItem_Clicked(object sender, MouseButtonEventArgs e)
    {
        if (sender is ComboBoxItem { DataContext: string font })
        {
            TextFontCombo.SelectedItem = font;
            _fontCommitted = true; // click keeps the previewed font
        }
    }

    private void TextFont_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (TextFontCombo.IsDropDownOpen && e.Key == Key.Enter)
            _fontCommitted = true; // Enter keeps the highlighted font
    }

    private void TextSize_ValueChanged(object? sender, EventArgs e)
    {
        if (_suppressInspector || _doc.Selected is not TextElement te) return;
        ArmInspectorUndo();
        te.FontSize = TextSize.Value;
        UpdateTextEditorStyle();
    }

    private void TextWrap_Toggled(object sender, RoutedEventArgs e)
    {
        if (_suppressInspector || _doc.Selected is not TextElement te) return;
        bool on = TextWrapCheck.IsChecked == true;
        PushUndo();
        if (on)
        {
            double w = TextWrapWidth.Value >= 40 ? TextWrapWidth.Value : 400;
            te.WrapWidth = w;
            TextWrapWidth.SetValue(w);
        }
        else
        {
            te.WrapWidth = 0;
        }
        TextWrapRow.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
        UpdateTextEditorStyle();
    }

    private void TextWrapWidth_ValueChanged(object? sender, EventArgs e)
    {
        if (_suppressInspector || _doc.Selected is not TextElement te) return;
        if (TextWrapCheck.IsChecked != true) return;
        ArmInspectorUndo();
        te.WrapWidth = TextWrapWidth.Value;
        UpdateTextEditorStyle();
    }

    private void TextStyle_Click(object sender, RoutedEventArgs e)
    {
        if (_suppressInspector || _doc.Selected is not TextElement te) return;
        PushUndo();
        te.IsBold = TextBoldBtn.IsChecked == true;
        te.IsItalic = TextItalicBtn.IsChecked == true;
        te.IsUnderline = TextUnderlineBtn.IsChecked == true;
        te.IsStrikethrough = TextStrikeBtn.IsChecked == true;
        RefreshInspector();
        UpdateTextEditorStyle();
    }

    private void TextFillMode_Checked(object sender, RoutedEventArgs e)
    {
        if (_suppressInspector || _doc.Selected is not TextElement te) return;
        PushUndo();
        te.IsGradient = TextFillGradientRadio.IsChecked == true;
        TextGradientOptions.Visibility = te.IsGradient ? Visibility.Visible : Visibility.Collapsed;
    }

    private void TextGradAngle_ValueChanged(object? sender, EventArgs e)
    {
        if (_suppressInspector || _doc.Selected is not TextElement te) return;
        ArmInspectorUndo();
        te.TextGradientAngle = TextGradAngle.Value;
    }

    private void TextAlign_Click(object sender, RoutedEventArgs e)
    {
        if (_suppressInspector || _doc.Selected is not TextElement te) return;
        var align = sender == TextAlignCenterBtn ? TextAlign.Center
                  : sender == TextAlignRightBtn ? TextAlign.Right
                  : TextAlign.Left;
        if (te.Alignment == align)
        {
            RefreshInspector(); // re-check the toggle the user just unchecked
            return;
        }
        PushUndo();
        te.Alignment = align;
        RefreshInspector();
        UpdateTextEditorStyle();
    }

    private void TextLetterSpacing_ValueChanged(object? sender, EventArgs e)
    {
        if (_suppressInspector || _doc.Selected is not TextElement te) return;
        ArmInspectorUndo();
        te.LetterSpacing = TextLetterSpacing.Value;
    }

    private void TextLineSpacing_ValueChanged(object? sender, EventArgs e)
    {
        if (_suppressInspector || _doc.Selected is not TextElement te) return;
        ArmInspectorUndo();
        te.LineSpacing = TextLineSpacing.Value;
    }

    private void TextShadow_Toggled(object sender, RoutedEventArgs e)
    {
        if (_suppressInspector || _doc.Selected is not TextElement te) return;
        bool on = TextShadowCheck.IsChecked == true;
        PushUndo();
        te.ShadowStyle = on ? ShadowStyle.Custom : ShadowStyle.None;
        TextShadowOptions.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
    }

    private void TextShadowX_ValueChanged(object? sender, EventArgs e)
    {
        if (_suppressInspector || _doc.Selected is not TextElement te) return;
        ArmInspectorUndo();
        te.ShadowX = TextShadowX.Value;
        te.ShadowStyle = ShadowStyle.Custom;
    }

    private void TextShadowY_ValueChanged(object? sender, EventArgs e)
    {
        if (_suppressInspector || _doc.Selected is not TextElement te) return;
        ArmInspectorUndo();
        te.ShadowY = TextShadowY.Value;
        te.ShadowStyle = ShadowStyle.Custom;
    }

    private void TextShadowBlur_ValueChanged(object? sender, EventArgs e)
    {
        if (_suppressInspector || _doc.Selected is not TextElement te) return;
        ArmInspectorUndo();
        te.ShadowBlur = TextShadowBlur.Value;
        te.ShadowStyle = ShadowStyle.Custom;
    }

    private void TextShadowOpacity_ValueChanged(object? sender, EventArgs e)
    {
        if (_suppressInspector || _doc.Selected is not TextElement te) return;
        ArmInspectorUndo();
        te.ShadowOpacity = TextShadowOpacity.Value;
        te.ShadowStyle = ShadowStyle.Custom;
    }

    private void FillText_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_suppressInspector) return;
        if (sender is not TextBox box || box.Tag is not string tag) return;
        if (!TryParseColor(box.Text, out var c)) return; // incomplete hex: wait for more typing

        if (tag == "Shadow")
        {
            if (_doc.Selected is not { } el) return;
            ArmInspectorUndo();
            el.ShadowColor = c;
            MarkShadowCustom(el);
            ShadowColorBox.Background = new SolidColorBrush(c);
            return;
        }

        if (tag is "TextFill1" or "TextFill2" or "TextShadow")
        {
            if (_doc.Selected is not TextElement te) return;
            ArmInspectorUndo();
            if (tag == "TextFill1")
            {
                te.TextColor1 = c;
                TextColor1Box.Background = new SolidColorBrush(c);
            }
            else if (tag == "TextFill2")
            {
                te.TextColor2 = c;
                TextColor2Box.Background = new SolidColorBrush(c);
            }
            else
            {
                te.ShadowColor = c;
                te.ShadowStyle = ShadowStyle.Custom;
                TextShadowColorBox.Background = new SolidColorBrush(c);
            }
            UpdateTextEditorStyle();
            return;
        }

        if (SelectedShape is not { } sh) return;
        ArmInspectorUndo(); // snapshot BEFORE applying
        if (tag == "Fill1") sh.FillColor1 = c;
        else if (tag == "Fill2") sh.FillColor2 = c;
        else if (tag == "Stroke") sh.StrokeColor = c;
    }

    private void InspX_ValueChanged(object? sender, EventArgs e)
    {
        if (_suppressInspector || _doc.Selected is not { } s) return;
        ArmInspectorUndo();
        s.X = InspX.Value;
    }

    private void InspY_ValueChanged(object? sender, EventArgs e)
    {
        if (_suppressInspector || _doc.Selected is not { } s) return;
        ArmInspectorUndo();
        s.Y = InspY.Value;
    }

    private void InspW_ValueChanged(object? sender, EventArgs e)
    {
        if (_suppressInspector || _doc.Selected is not { } s) return;
        ArmInspectorUndo();

        double oldWidth = s.Width;
        double oldHeight = s.Height;
        s.Width = Math.Max(MinSize, InspW.Value);
        if (_lockAspectRatio && oldWidth > 0)
            s.Height = Math.Max(MinSize, oldHeight * s.Width / oldWidth);

        _suppressInspector = true;
        InspW.SetValue(s.Width);
        InspH.SetValue(s.Height);
        _suppressInspector = false;
    }

    private void InspH_ValueChanged(object? sender, EventArgs e)
    {
        if (_suppressInspector || _doc.Selected is not { } s) return;
        ArmInspectorUndo();

        double oldWidth = s.Width;
        double oldHeight = s.Height;
        s.Height = Math.Max(MinSize, InspH.Value);
        if (_lockAspectRatio && oldHeight > 0)
            s.Width = Math.Max(MinSize, oldWidth * s.Height / oldHeight);

        _suppressInspector = true;
        InspW.SetValue(s.Width);
        InspH.SetValue(s.Height);
        _suppressInspector = false;
    }

    private static void UpdateGradientDirectionButtons(UniformGrid grid, double angle)
    {
        foreach (var button in grid.Children.OfType<ToggleButton>())
        {
            if (button.Tag is string tag &&
                double.TryParse(tag, NumberStyles.Float, CultureInfo.InvariantCulture, out double direction))
            {
                button.IsChecked = Math.Abs(DesignElement.NormalizeAngle(angle - direction)) < 0.5;
            }
        }
    }

    private void ShapeGradientDirection_Click(object sender, RoutedEventArgs e)
    {
        if (_suppressInspector || SelectedShape is not { } sh) return;
        if (sender is not ToggleButton { Tag: string tag } ||
            !double.TryParse(tag, NumberStyles.Float, CultureInfo.InvariantCulture, out double angle))
            return;

        UpdateGradientDirectionButtons(ShapeGradDirections, angle);
        if (Math.Abs(DesignElement.NormalizeAngle(sh.GradientAngle - angle)) < 0.5) return;

        PushUndo();
        _inspectorArmed = false;
        sh.GradientAngle = angle;
    }

    private void TextGradientDirection_Click(object sender, RoutedEventArgs e)
    {
        if (_suppressInspector || _doc.Selected is not TextElement te) return;
        if (sender is not ToggleButton { Tag: string tag } ||
            !double.TryParse(tag, NumberStyles.Float, CultureInfo.InvariantCulture, out double angle))
            return;

        UpdateGradientDirectionButtons(TextGradDirections, angle);
        if (Math.Abs(DesignElement.NormalizeAngle(te.TextGradientAngle - angle)) < 0.5) return;

        PushUndo();
        _inspectorArmed = false;
        te.TextGradientAngle = angle;
    }

    private void InspGradAngle_ValueChanged(object? sender, EventArgs e)
    {
        if (_suppressInspector || SelectedShape is not { } sh) return;
        ArmInspectorUndo();
        sh.GradientAngle = InspGradAngle.Value;
    }

    private void InspRot_ValueChanged(object? sender, EventArgs e)
    {
        if (_suppressInspector || _doc.Selected is not { } s) return;
        ArmInspectorUndo();
        s.RotationAngle = InspRot.Value;
    }

    private void InspRadius_ValueChanged(object? sender, EventArgs e)
    {
        if (_suppressInspector || _doc.Selected is not { } s) return;
        if (s.UseIndividualCornerRadii) return;
        ArmInspectorUndo();
        if (s is ShapeElement sh) sh.CornerRadius = InspRadius.Value;
        else if (s is ImageElement im) im.CornerRadius = InspRadius.Value;
    }

    private void InspIndividualCorners_Toggled(object sender, RoutedEventArgs e)
    {
        if (_suppressInspector || _doc.Selected is not { } element) return;
        ArmInspectorUndo();
        bool enabled = InspIndividualCornersCheck.IsChecked == true;
        if (enabled && !element.UseIndividualCornerRadii)
            element.IndividualCornerRadii = new CornerRadius(GetUniformCornerRadius(element));
        element.UseIndividualCornerRadii = enabled;
        RefreshInspector();
        RefreshShapeToolbar();
    }

    private void InspCornerIndividual_ValueChanged(object? sender, EventArgs e)
    {
        if (_suppressInspector || sender is not ValueSlider slider || slider.Tag is not string corner
            || _doc.Selected is not { } element || !element.UseIndividualCornerRadii) return;
        ArmInspectorUndo();
        element.IndividualCornerRadii = SetCornerValue(element.IndividualCornerRadii, corner, slider.Value);
        RefreshInspector();
        RefreshShapeToolbar();
    }

    private void InspStrokeThick_ValueChanged(object? sender, EventArgs e)
    {
        if (_suppressInspector || SelectedShape is not { } sh) return;
        ArmInspectorUndo();
        sh.StrokeThickness = InspStrokeThick.Value;
    }

    /// <summary>Ctrl+F edits the selected element color, or the active tool's color default.</summary>
    private void PickContextColor()
    {
        if (_doc.Selected is GroupElement group)
        {
            var shapes = group.Children.OfType<ShapeElement>().ToList();
            if (shapes.Count == 0)
            {
                StatusText.Text = "Ctrl+F changes the selected object's color or the active tool's color.";
                return;
            }
            var dlgGroup = new ColorPickerDialog(shapes[0].FillColor1, CanvasColorsWithPrimary()) { Owner = this };
            if (dlgGroup.ShowDialog() != true) return;
            PushUndo();
            foreach (var gShape in shapes) gShape.FillColor1 = dlgGroup.SelectedColor;
            RefreshInspector();
            RefreshShapeToolbar();
            StatusText.Text = $"Group fill set to {ToHex(dlgGroup.SelectedColor)}.";
            return;
        }

        if (_doc.Selected is LineElement)
        {
            AttrLineColor_Click(this, new RoutedEventArgs());
            return;
        }

        if (_doc.Selected is TextElement te)
        {
            var dlg = new ColorPickerDialog(te.TextColor1, CanvasColorsWithPrimary()) { Owner = this };
            if (dlg.ShowDialog() != true) return;
            PushUndo();
            te.TextColor1 = dlg.SelectedColor;
            RefreshInspector();
            UpdateTextEditorStyle();
            StatusText.Text = $"Text color set to {ToHex(dlg.SelectedColor)}.";
            return;
        }

        if (_doc.Selected is ImageElement image)
        {
            var adjustments = image.Adjustments;
            var initial = adjustments.RecolorAmount > 0 ? adjustments.RecolorColor : _primaryColor;
            var dlgImage = new ColorPickerDialog(initial, CanvasColorsWithPrimary()) { Owner = this };
            if (dlgImage.ShowDialog() != true) return;
            PushUndo();
            _inspectorArmed = false;
            adjustments.RecolorColor = dlgImage.SelectedColor;
            adjustments.RecolorAmount = 100;
            image.RefreshPhotoPreview();
            RefreshInspector();
            RefreshPhotoPanel();
            StatusText.Text = $"Image color set to {ToHex(dlgImage.SelectedColor)}.";
            return;
        }

        if (SelectedShape is not { } sh)
        {
            if (_doc.Selected != null)
            {
                StatusText.Text = "Ctrl+F changes the selected object's color or the active tool's color.";
                return;
            }
            switch (_tool)
            {
                case ActiveTool.Rectangle:
                    AttrShapeFill1_Click(this, new RoutedEventArgs());
                    break;
                case ActiveTool.Line:
                    AttrLineColor_Click(this, new RoutedEventArgs());
                    break;
                case ActiveTool.Text:
                    AttrTextColor_Click(this, new RoutedEventArgs());
                    break;
                case ActiveTool.Paintbrush:
                    AttrBrushColor_Click(this, new RoutedEventArgs());
                    break;
                default:
                    PickCanvasColor();
                    break;
            }
            return;
        }
        var dlgShape = new ColorPickerDialog(sh.FillColor1, CanvasColorsWithPrimary()) { Owner = this };
        if (dlgShape.ShowDialog() != true) return;
        PushUndo();
        sh.FillColor1 = dlgShape.SelectedColor;
        OnSelectionChanged(); // refresh swatches + hex boxes
        StatusText.Text = $"Shape background set to {ToHex(dlgShape.SelectedColor)}.";
    }

    private void ColorPreview_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button btn || btn.Tag is not string tag) return;

        Color original;
        Action<Color> apply;
        Action refresh;
        DesignElement? shadowTarget = null;
        ShadowStyle originalShadowStyle = ShadowStyle.None;

        switch (tag)
        {
            case "Shadow":
            case "TextShadow":
                if (_doc.Selected is not { } el) return;
                shadowTarget = el;
                originalShadowStyle = el.ShadowStyle;
                original = el.ShadowColor;
                apply = c => { el.ShadowColor = c; MarkShadowCustom(el); };
                refresh = RefreshInspector;
                break;
            case "TextFill1":
            case "TextFill2":
                if (_doc.Selected is not TextElement te) return;
                original = tag == "TextFill1" ? te.TextColor1 : te.TextColor2;
                apply = c =>
                {
                    if (tag == "TextFill1") te.TextColor1 = c;
                    else te.TextColor2 = c;
                    UpdateTextEditorStyle();
                };
                refresh = () => { RefreshInspector(); UpdateTextEditorStyle(); };
                break;
            case "Fill1":
            case "Fill2":
            case "Stroke":
                if (SelectedShape is not { } sh) return;
                original = tag switch
                {
                    "Fill1" => sh.FillColor1,
                    "Fill2" => sh.FillColor2,
                    _ => sh.StrokeColor,
                };
                apply = c =>
                {
                    if (tag == "Fill1") sh.FillColor1 = c;
                    else if (tag == "Fill2") sh.FillColor2 = c;
                    else sh.StrokeColor = c;
                };
                refresh = OnSelectionChanged;
                break;
            default:
                return;
        }

        PushUndo(); // one undo step for the whole picker session
        var dlg = new ColorPickerDialog(original, CanvasColorsWithPrimary()) { Owner = this };
        dlg.PreviewColorChanged += c => apply(c); // live on the canvas while the dialog is open
        if (dlg.ShowDialog() == true)
        {
            apply(dlg.SelectedColor);
        }
        else
        {
            apply(original); // cancel / close: restore the pre-dialog color
            if (shadowTarget != null) shadowTarget.ShadowStyle = originalShadowStyle;
        }
        refresh();
    }

    // =========================================================================
    // ELEMENTS / ICONIFY TOOL
    // =========================================================================

    private DispatcherTimer? _iconifySearchDebounce;
    private CancellationTokenSource? _iconifyCts;
    private ObservableCollection<IconItem> _iconItems = new();
    private string _iconQuery = "";
    private int _iconStart;      // offset already consumed from the library
    private bool _iconHasMore;
    private bool _iconLoadingMore;
    private string _iconFooterMessage = "";

    private void ToolElements_Click(object sender, RoutedEventArgs e)
    {
        if (_tool == ActiveTool.Elements)
            SetTool(ActiveTool.Pick);
        else
            SetTool(ActiveTool.Elements);
    }

    private void ElementsClose_Click(object sender, RoutedEventArgs e)
    {
        SetTool(ActiveTool.Pick);
    }

    private void CategoryIconify_Click(object sender, RoutedEventArgs e)
    {
        ElementsCategoriesView.Visibility = Visibility.Collapsed;
        ElementsIconifyView.Visibility = Visibility.Visible;
        IconifySearchBox.Focus();
        if (IconifyItemsList.ItemsSource == null)
        {
            LoadIconifyAsync(IconifySearchBox.Text);
        }
    }

    private void IconifyBack_Click(object sender, RoutedEventArgs e)
    {
        ElementsIconifyView.Visibility = Visibility.Collapsed;
        ElementsCategoriesView.Visibility = Visibility.Visible;
    }

    private void IconifySearch_TextChanged(object sender, TextChangedEventArgs e)
    {
        string query = IconifySearchBox.Text;
        IconifySearchPlaceholder.Visibility = string.IsNullOrEmpty(query) ? Visibility.Visible : Visibility.Collapsed;
        IconifySearchClearBtn.Visibility = string.IsNullOrEmpty(query) ? Visibility.Collapsed : Visibility.Visible;

        if (_iconifySearchDebounce == null)
        {
            _iconifySearchDebounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(350) };
            _iconifySearchDebounce.Tick += (_, _) =>
            {
                _iconifySearchDebounce.Stop();
                LoadIconifyAsync(IconifySearchBox.Text);
            };
        }
        _iconifySearchDebounce.Stop();
        _iconifySearchDebounce.Start();
    }

    private void IconifySearchClear_Click(object sender, RoutedEventArgs e)
    {
        IconifySearchBox.Text = string.Empty;
        IconifySearchBox.Focus();
    }

    private void IconifyRetry_Click(object sender, RoutedEventArgs e)
    {
        LoadIconifyAsync(IconifySearchBox.Text);
    }

    private void IconifyToggleDebug_Click(object sender, RoutedEventArgs e)
    {
        IconifyDebugText.Visibility = IconifyDebugText.Visibility == Visibility.Visible
            ? Visibility.Collapsed
            : Visibility.Visible;
    }

    private async void LoadIconifyAsync(string query)
    {
        _iconifyCts?.Cancel();
        _iconifyCts?.Dispose();
        _iconifyCts = new CancellationTokenSource();
        var ct = _iconifyCts.Token;

        _iconQuery = query;
        _iconStart = 0;
        _iconHasMore = false;
        _iconLoadingMore = false;
        _iconFooterMessage = "";
        _iconItems.Clear();

        IconifyLoadingBar.Visibility = Visibility.Visible;
        IconifyErrorPanel.Visibility = Visibility.Collapsed;
        IconifyDebugText.Visibility = Visibility.Collapsed;
        IconifyDebugText.Text = string.Empty;

        try
        {
            var result = await IconifyService.SearchIconsAsync(query, 0, ct);
            if (ct.IsCancellationRequested) return;

            _iconHasMore = result.HasMore;
            _iconStart += result.RawCount;
            _iconItems = new ObservableCollection<IconItem>(result.Items);
            IconifyItemsList.ItemsSource = _iconItems;
            UpdateIconifyFooter();
            if (_iconItems.Count == 0)
            {
                StatusText.Text = $"No icons found for '{query}'.";
            }
            else
            {
                StatusText.Text = string.IsNullOrWhiteSpace(query)
                    ? "Loaded popular icons. Click any icon to insert onto canvas."
                    : $"Found {_iconItems.Count} icons for '{query}'.";
            }
        }
        catch (OperationCanceledException)
        {
            // query was replaced
        }
        catch (Exception ex)
        {
            if (ct.IsCancellationRequested) return;
            IconifyErrorPanel.Visibility = Visibility.Visible;
            IconifyDebugText.Text = $"{ex.GetType().Name}: {ex.Message}\n{ex.StackTrace}";
            StatusText.Text = "Failed to load icons from Iconify library.";
        }
        finally
        {
            if (!ct.IsCancellationRequested)
            {
                IconifyLoadingBar.Visibility = Visibility.Collapsed;
            }
        }
    }

    /// <summary>Pull the next slice of the current icon search when the list is scrolled down.</summary>
    private async void LoadMoreIconsAsync()
    {
        if (_iconLoadingMore) return;
        if (!_iconHasMore)
        {
            UpdateIconifyFooter();
            return;
        }
        if (string.IsNullOrWhiteSpace(_iconQuery)) return;

        _iconLoadingMore = true;
        IconifyLoadingBar.Visibility = Visibility.Visible;
        IconifyListStatus.Text = "Loading more icons…";
        IconifyLoadMoreBtn.Visibility = Visibility.Collapsed;
        _iconFooterMessage = "";
        try
        {
            var result = await IconifyService.SearchIconsAsync(_iconQuery, _iconStart, CancellationToken.None);
            _iconHasMore = result.HasMore;
            _iconStart += result.RawCount;
            foreach (var item in result.Items) _iconItems.Add(item);
            StatusText.Text = $"Found {_iconItems.Count} icons for '{_iconQuery}'.";
        }
        catch (Exception ex)
        {
            _iconFooterMessage = "Could not load more icons: " + ex.Message;
            StatusText.Text = _iconFooterMessage;
        }
        finally
        {
            _iconLoadingMore = false;
            IconifyLoadingBar.Visibility = Visibility.Collapsed;
            UpdateIconifyFooter();
        }
    }

    /// <summary>Footer text + manual paging button for the icon list.</summary>
    private void UpdateIconifyFooter()
    {
        if (_iconItems.Count == 0 && _iconFooterMessage.Length == 0)
        {
            IconifyListStatus.Text = "";
            IconifyLoadMoreBtn.Visibility = Visibility.Collapsed;
            return;
        }

        if (_iconFooterMessage.Length > 0)
        {
            IconifyListStatus.Text = _iconFooterMessage;
            IconifyLoadMoreBtn.Visibility = _iconHasMore ? Visibility.Visible : Visibility.Collapsed;
            return;
        }

        IconifyListStatus.Text = _iconHasMore
            ? $"{_iconItems.Count} icons loaded — scroll for more"
            : $"{_iconItems.Count} icons loaded";
        IconifyLoadMoreBtn.Visibility = _iconHasMore ? Visibility.Visible : Visibility.Collapsed;
    }

    private void IconifyLoadMore_Click(object sender, RoutedEventArgs e) => LoadMoreIconsAsync();

    private void IconifyScroll_Changed(object sender, ScrollChangedEventArgs e)
    {
        if (e.VerticalChange <= 0) return;
        if (sender is not ScrollViewer sv) return;
        if (sv.ScrollableHeight - sv.VerticalOffset > 200) return; // not at the bottom yet
        LoadMoreIconsAsync();
    }

    private void IconifyTile_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement fe || fe.Tag is not IconItem item) return;
        if (string.IsNullOrWhiteSpace(item.PathData)) return;

        PushUndo();

        double maxDim = 96;
        double w = maxDim;
        double h = maxDim;
        try
        {
            var raw = Geometry.Parse(item.PathData);
            Rect b = raw.Bounds;
            if (b.Width > 0 && b.Height > 0)
            {
                if (b.Width >= b.Height)
                {
                    w = maxDim;
                    h = Math.Max(16, Math.Round(maxDim * b.Height / b.Width));
                }
                else
                {
                    h = maxDim;
                    w = Math.Max(16, Math.Round(maxDim * b.Width / b.Height));
                }
            }
        }
        catch { }

        var art = _doc.ArtboardRect;
        double x = art.X + (art.Width - w) / 2;
        double y = art.Y + (art.Height - h) / 2;

        var color = _primaryColor;
        if (color.A == 0) color = Color.FromRgb(0x2E, 0x9B, 0xFF);

        var shape = new ShapeElement
        {
            Kind = ShapeKind.CustomPath,
            CustomPathData = item.PathData,
            X = Math.Round(x),
            Y = Math.Round(y),
            Width = w,
            Height = h,
            FillColor1 = color,
            FillColor2 = color,
            IsGradient = false,
            StrokeColor = Colors.Transparent,
            StrokeThickness = 0,
            Opacity = 100
        };

        _doc.Elements.Add(shape);
        _doc.NormalizeZ();
        _doc.Select(shape);
        RefreshInspector();
        SelectionAdorner.InvalidateVisual();
        StatusText.Text = $"Inserted icon '{item.Id}' ({w:F0}x{h:F0}) onto canvas.";
    }

    // =========================================================================
    // ELEMENTS / PIXABAY PHOTOS TOOL
    // =========================================================================

    private CancellationTokenSource? _photosCts;
    private string _photosOrientation = "all";
    private DateTime _photosBlockedUntil = DateTime.MinValue;
    private ObservableCollection<PhotoItem> _photoItems = new();
    private string _photosQuery = "";
    private int _photosPage;
    private int _photosTotalHits;
    private int _photosRateLimit;
    private int _photosRateRemaining;
    private bool _photosHasMore;
    private bool _photosLoadingMore;
    private string _photosFooterMessage = "";

    private void CategoryPhotos_Click(object sender, RoutedEventArgs e)
    {
        ElementsCategoriesView.Visibility = Visibility.Collapsed;
        ElementsIconifyView.Visibility = Visibility.Collapsed;
        ElementsPhotosView.Visibility = Visibility.Visible;
        PhotosSearchBox.Focus();
        // No automatic search: an empty query would burn API quota on the
        // popular feed, so the user types what they want first.
    }

    private void PhotosBack_Click(object sender, RoutedEventArgs e)
    {
        ElementsPhotosView.Visibility = Visibility.Collapsed;
        ElementsCategoriesView.Visibility = Visibility.Visible;
    }

    private void PhotosSearch_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (PhotosSearchPlaceholder == null) return; // raised while the XAML loads
        string query = PhotosSearchBox.Text;
        PhotosSearchPlaceholder.Visibility = string.IsNullOrEmpty(query) ? Visibility.Visible : Visibility.Collapsed;
        PhotosSearchClearBtn.Visibility = string.IsNullOrEmpty(query) ? Visibility.Collapsed : Visibility.Visible;
    }

    /// <summary>Searching runs on Enter only — one request per search, never per keystroke.</summary>
    private void PhotosSearch_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        e.Handled = true;
        LoadPhotosAsync(PhotosSearchBox.Text);
    }

    private void PhotosSearchClear_Click(object sender, RoutedEventArgs e)
    {
        PhotosSearchBox.Text = string.Empty;
        PhotosSearchBox.Focus();
    }

    private void PhotosOrientation_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not ToggleButton tb || tb.Tag is not string tag) return;
        _photosOrientation = tag;
        PhotosOrientationAny.IsChecked = tag == "all";
        PhotosOrientationWide.IsChecked = tag == "horizontal";
        PhotosOrientationTall.IsChecked = tag == "vertical";
        if (string.IsNullOrWhiteSpace(PhotosSearchBox.Text)) return; // nothing to re-search yet
        LoadPhotosAsync(PhotosSearchBox.Text);
    }

    private void PhotosRetry_Click(object sender, RoutedEventArgs e)
    {
        LoadPhotosAsync(PhotosSearchBox.Text);
    }

    private void PhotosOpenSettings_Click(object sender, RoutedEventArgs e)
    {
        OpenSettings(showIntegrations: true);
        LoadPhotosAsync(PhotosSearchBox.Text);
    }

    private void PhotosToggleDebug_Click(object sender, RoutedEventArgs e)
    {
        PhotosDebugText.Visibility = PhotosDebugText.Visibility == Visibility.Visible
            ? Visibility.Collapsed
            : Visibility.Visible;
    }

    private void ShowPhotosMessage(string title, string body, bool offerSettings)
    {
        PhotosErrorTitle.Text = title;
        PhotosErrorBody.Text = body;
        PhotosOpenSettingsBtn.Visibility = offerSettings ? Visibility.Visible : Visibility.Collapsed;
        PhotosErrorPanel.Visibility = Visibility.Visible;
    }

    private async void LoadPhotosAsync(string query)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            _photoItems.Clear();
            PhotosItemsList.ItemsSource = _photoItems;
            PhotosEmptyHint.Text = "Type a search word to browse the photo library.";
            PhotosEmptyHint.Visibility = Visibility.Visible;
            PhotosErrorPanel.Visibility = Visibility.Collapsed;
            return;
        }

        if (query.Trim().Length < 2)
        {
            // Short terms match almost everything and only burn API quota.
            _photoItems.Clear();
            PhotosItemsList.ItemsSource = _photoItems;
            PhotosEmptyHint.Text = "Type at least 2 characters to search.";
            PhotosEmptyHint.Visibility = Visibility.Visible;
            PhotosErrorPanel.Visibility = Visibility.Collapsed;
            return;
        }

        string key = _settings.PixabayApiKey;
        if (string.IsNullOrWhiteSpace(key))
        {
            _photoItems.Clear();
            PhotosItemsList.ItemsSource = _photoItems;
            PhotosEmptyHint.Visibility = Visibility.Collapsed;
            ShowPhotosMessage(
                "API key needed",
                "Add your free Pixabay API key in Settings → Integrations to search photos.",
                offerSettings: true);
            return;
        }

        // After a rate-limit error the key is on cooldown: don't burn more
        // requests until the window resets.
        if (DateTime.UtcNow < _photosBlockedUntil)
        {
            int wait = (int)Math.Ceiling((_photosBlockedUntil - DateTime.UtcNow).TotalSeconds);
            PhotosErrorPanel.Visibility = Visibility.Visible;
            PhotosErrorTitle.Text = "Rate limit reached";
            PhotosErrorBody.Text = $"The photo library allows a limited number of requests per minute. Try again in {wait} seconds.";
            PhotosOpenSettingsBtn.Visibility = Visibility.Collapsed;
            return;
        }

        _photosCts?.Cancel();
        _photosCts?.Dispose();
        _photosCts = new CancellationTokenSource();
        var ct = _photosCts.Token;

        _photosQuery = query;
        _photosPage = 0;
        _photosTotalHits = 0;
        _photosHasMore = false;
        _photosLoadingMore = false;
        _photosFooterMessage = "";
        _photoItems.Clear();

        PhotosLoadingBar.Visibility = Visibility.Visible;
        PhotosErrorPanel.Visibility = Visibility.Collapsed;
        PhotosDebugText.Visibility = Visibility.Collapsed;
        PhotosDebugText.Text = string.Empty;

        try
        {
            var result = await PixabayService.SearchAsync(key, query, _photosOrientation, 1, ct);
            if (ct.IsCancellationRequested) return;

            _photosPage = result.Page;
            _photosHasMore = result.HasMore;
            _photosTotalHits = result.TotalHits;
            _photosRateLimit = result.RateLimitLimit;
            _photosRateRemaining = result.RateLimitRemaining;
            _photoItems = new ObservableCollection<PhotoItem>(result.Items);
            PhotosItemsList.ItemsSource = _photoItems;
            PhotosEmptyHint.Visibility = _photoItems.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            UpdatePhotosFooter();
            if (_photoItems.Count == 0)
            {
                StatusText.Text = $"No photos found for '{query}'.";
            }
            else
            {
                StatusText.Text = $"Found {_photoItems.Count} of {result.TotalHits} photos for '{query}'.";
            }
        }
        catch (OperationCanceledException)
        {
            // query was replaced
        }
        catch (Exception ex)
        {
            if (ct.IsCancellationRequested) return;
            bool limited = ex is PixabayRateLimitException;
            if (limited)
            {
                var limit = (PixabayRateLimitException)ex;
                _photosBlockedUntil = DateTime.UtcNow.AddSeconds(Math.Max(15, limit.RetryAfterSeconds));
            }
            ShowPhotosMessage(
                limited ? "Rate limit reached" : "Could not load photos.",
                ex.Message,
                offerSettings: false);
            PhotosEmptyHint.Visibility = Visibility.Collapsed;
            PhotosDebugText.Text = $"{ex.GetType().Name}: {ex.Message}\n{ex.StackTrace}";
            StatusText.Text = limited
                ? "Photo library request limit reached — try again in a minute."
                : "Failed to load photos from the Pixabay library.";
        }
        finally
        {
            if (!ct.IsCancellationRequested)
            {
                PhotosLoadingBar.Visibility = Visibility.Collapsed;
            }
        }
    }

    /// <summary>Pull the next page of the current photo search when the list is scrolled down.</summary>
    private async void LoadMorePhotosAsync()
    {
        if (_photosLoadingMore) return;
        if (!_photosHasMore)
        {
            UpdatePhotosFooter();
            return;
        }
        if (string.IsNullOrWhiteSpace(_photosQuery)) return;
        if (_photosPage <= 0) return;
        if (DateTime.UtcNow < _photosBlockedUntil)
        {
            int wait = (int)Math.Ceiling((_photosBlockedUntil - DateTime.UtcNow).TotalSeconds);
            PhotosListStatus.Text = $"Request limit reached — try again in {wait}s";
            return;
        }

        string key = _settings.PixabayApiKey;
        if (string.IsNullOrWhiteSpace(key))
        {
            PhotosListStatus.Text = "Add your Pixabay API key in Settings → Integrations.";
            return;
        }

        _photosLoadingMore = true;
        PhotosLoadingBar.Visibility = Visibility.Visible;
        PhotosListStatus.Text = "Loading more photos…";
        PhotosLoadMoreBtn.Visibility = Visibility.Collapsed;
        _photosFooterMessage = "";
        try
        {
            var result = await PixabayService.SearchAsync(
                key, _photosQuery, _photosOrientation, _photosPage + 1, CancellationToken.None);
            _photosPage = result.Page;
            _photosHasMore = result.HasMore;
            _photosTotalHits = result.TotalHits;
            _photosRateLimit = result.RateLimitLimit;
            _photosRateRemaining = result.RateLimitRemaining;
            foreach (var item in result.Items) _photoItems.Add(item);
            StatusText.Text = $"Found {_photoItems.Count} of {result.TotalHits} photos for '{_photosQuery}'.";
        }
        catch (PixabayRateLimitException ex)
        {
            _photosBlockedUntil = DateTime.UtcNow.AddSeconds(Math.Max(15, ex.RetryAfterSeconds));
            _photosFooterMessage = $"Request limit reached — try again in {Math.Max(15, ex.RetryAfterSeconds)}s";
            StatusText.Text = "Photo library request limit reached — try again in a minute.";
        }
        catch (Exception ex)
        {
            _photosFooterMessage = "Could not load more: " + ex.Message;
            StatusText.Text = _photosFooterMessage;
        }
        finally
        {
            _photosLoadingMore = false;
            PhotosLoadingBar.Visibility = Visibility.Collapsed;
            UpdatePhotosFooter();
        }
    }

    /// <summary>Footer text + manual paging button for the photo list.</summary>
    private void UpdatePhotosFooter()
    {
        if (_photoItems.Count == 0 && _photosFooterMessage.Length == 0)
        {
            PhotosListStatus.Text = "";
            PhotosQuotaText.Text = "";
            PhotosLoadMoreBtn.Visibility = Visibility.Collapsed;
            return;
        }

        if (_photosFooterMessage.Length > 0)
        {
            PhotosListStatus.Text = _photosFooterMessage;
            PhotosListStatus.Foreground = new SolidColorBrush(Color.FromRgb(0xFF, 0xB0, 0x20));
            PhotosLoadMoreBtn.Visibility = _photosHasMore ? Visibility.Visible : Visibility.Collapsed;
            return;
        }

        string count = _photosTotalHits > 0
            ? $"{_photoItems.Count} of {_photosTotalHits} photos"
            : $"{_photoItems.Count} photos";
        PhotosListStatus.Text = _photosHasMore ? count + " — scroll for more" : count + " — end of results";
        PhotosListStatus.Foreground = new SolidColorBrush(Color.FromRgb(0x8E, 0xA0, 0xB4));
        PhotosQuotaText.Text = _photosRateLimit > 0
            ? $"{_photosRateRemaining}/{_photosRateLimit} API requests left this minute"
            : "";
        PhotosLoadMoreBtn.Visibility = _photosHasMore ? Visibility.Visible : Visibility.Collapsed;
    }

    private void PhotosLoadMore_Click(object sender, RoutedEventArgs e) => LoadMorePhotosAsync();

    private void PhotosScroll_Changed(object sender, ScrollChangedEventArgs e)
    {
        if (e.VerticalChange <= 0) return;
        if (sender is not ScrollViewer sv) return;
        if (sv.ScrollableHeight - sv.VerticalOffset > 200) return; // not at the bottom yet
        LoadMorePhotosAsync();
    }

    private async void PhotosTile_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement fe || fe.Tag is not PhotoItem item) return;
        if (string.IsNullOrWhiteSpace(item.LargeUrl) && string.IsNullOrWhiteSpace(item.WebUrl)) return;

        PhotosLoadingBar.Visibility = Visibility.Visible;
        try
        {
            var bmp = await PixabayService.DownloadBestAsync(item);
            if (bmp == null) return;
            PlacePhoto(bmp, item);
        }
        catch (Exception ex)
        {
            StatusText.Text = "Could not download the photo: " + ex.Message;
        }
        finally
        {
            PhotosLoadingBar.Visibility = Visibility.Collapsed;
        }
    }

    /// <summary>Drop a downloaded photo on the canvas, fitted to the artboard.</summary>
    private void PlacePhoto(BitmapSource bmp, PhotoItem item)
    {
        PushUndo();

        var art = _doc.ArtboardRect;
        double scale = Math.Min(art.Width / bmp.PixelWidth, art.Height / bmp.PixelHeight);
        if (scale > 1) scale = 1; // never upscale a small photo
        double w = Math.Max(1, Math.Round(bmp.PixelWidth * scale));
        double h = Math.Max(1, Math.Round(bmp.PixelHeight * scale));

        var el = new ImageElement
        {
            X = Math.Round(art.X + (art.Width - w) / 2),
            Y = Math.Round(art.Y + (art.Height - h) / 2),
            Width = w,
            Height = h,
            Source = bmp,
        };

        _doc.Add(el);
        StatusText.Text = $"Placed photo '{item.DisplayName}' ({w:F0}×{h:F0} px) on the canvas.";
    }
}
