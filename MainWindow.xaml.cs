using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
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
    private enum ActiveTool { Pick, Slice, Rectangle, Text, EyeDropper, Erase }
    private enum DragMode { None, Move, Resize, Rotate, Slice, Draw }

    private sealed class AddConverter : IValueConverter
    {
        private readonly double _offset;
        public AddConverter(double offset) => _offset = offset;

        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
            => value is double d ? d + _offset : value;

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
            => throw new NotSupportedException();
    }

    private const double MinSize = 10;
    private const double MinZoom = 0.05;
    private const double MaxZoom = 8.0;

    [DllImport("user32.dll")]
    private static extern uint GetClipboardSequenceNumber();

    private readonly DesignDocument _doc = new();
    private ActiveTool _tool = ActiveTool.Pick;
    private DragMode _drag = DragMode.None;

    // Drag start snapshot.
    private Point _dragStartCanvas;
    private Point _drawStart;
    private double _origX, _origY, _origW, _origH;
    private double _origFontSize;
    private Point _origCenter;
    private double _startPointerAngle;
    private double _startElementAngle;
    private HandleKind _activeHandle = HandleKind.None;

    // In-place text editing.
    private TextElement? _editingText;
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
    private readonly List<SliceRegion> _slices = new(); // every drawn region (add more freely)
    private SliceRegion? _activeSlice;                  // target for export / copy / punch
    private Rect? _pendingSlice;                        // live drag preview
    private bool _sliceVisible = true;
    private bool _sliceAddMode;                         // false = each drag replaces; true = accumulate (+)
    private DesignElement? _copiedPrototype;
    private uint _copyClipboardSeq;
    private int _untitledCounter = 1;
    private string? _projectPath;
    private readonly UndoService _undo = new();
    private bool _inspectorArmed;

    /// <summary>Project primary color: sampled with the eyedropper, used for new shapes, saved in .yimpact.</summary>
    private System.Windows.Media.Color _primaryColor = System.Windows.Media.Color.FromRgb(0x3B, 0x82, 0xF6);

    private bool _suppressInspector;

    /// <summary>Classic transparency checkerboard shown when the canvas has no background.</summary>
    private static readonly Brush CheckerBrush = BuildCheckerBrush();

    /// <summary>New-canvas-parameterized editor; started from <see cref="App"/> after the New Canvas dialog.</summary>
    public MainWindow(int width, int height, Color background, bool transparent)
    {
        InitializeComponent();
        SourceInitialized += (_, _) => DarkTitleBar.Apply(this);
        DataContext = _doc;
        _doc.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(DesignDocument.Selected)
                or nameof(DesignDocument.HasSelection))
                OnSelectionChanged();
            else if (e.PropertyName is nameof(DesignDocument.CanvasColor)
                or nameof(DesignDocument.CanvasTransparent))
                UpdateArtboardFill();
        };
        _doc.NewCanvas("Untitled-1", width, height, background, transparent);
        OnSelectionChanged();
        UpdateArtboardFill();
        UpdateToolUI();
        UpdateZoomUI();
        UpdateInkSwatch();
        RefreshUndoButtons();
        foreach (var tb in new[] { Fill1Text, Fill2Text, StrokeText,
                                   ShadowText, ShadowX.Editor, ShadowY.Editor, ShadowBlur.Editor, ShadowOpacity.Editor,
                                   TextColor1Text, TextColor2Text, TextShadowText,
                                   TextSize.Editor, TextGradAngle.Editor, TextLetterSpacing.Editor, TextLineSpacing.Editor,
                                   TextWrapWidth.Editor,
                                   TextShadowX.Editor, TextShadowY.Editor, TextShadowBlur.Editor, TextShadowOpacity.Editor,
                                   OpacityStrength.Editor, OpacityStart.Editor, OpacityEnd.Editor,
                                   InspX.Editor, InspY.Editor, InspW.Editor, InspH.Editor,
                                   InspRot.Editor, InspRadius.Editor, InspGradAngle.Editor, InspStrokeThick.Editor,
                                   AttrOpacity.Editor, AttrCorner.Editor, AttrSoft.Editor })
            tb.GotFocus += (_, _) => _inspectorArmed = true; // next keystroke snapshots first
        // Any click on the pick-tool settings (sliders, steppers) arms one undo snapshot.
        AttrPickPanel.PreviewMouseLeftButtonDown += (_, _) => _inspectorArmed = true;
        InspectorPanel.PreviewMouseLeftButtonDown += (_, _) => _inspectorArmed = true;
        ShadowCard.PreviewMouseLeftButtonDown += (_, _) => _inspectorArmed = true;
        TextCard.PreviewMouseLeftButtonDown += (_, _) => _inspectorArmed = true;

        _allFonts.AddRange(Fonts.SystemFontFamilies
            .Select(f => f.Source).Distinct()
            .OrderBy(n => n, StringComparer.CurrentCultureIgnoreCase));
        TextFontCombo.ItemsSource = _allFonts;
        TextFontCombo.SelectedItem = "Segoe UI";
        AttrEraseTolerance.SetValue(25); // sensible default magic-wand tolerance
        Loaded += (_, _) => ZoomToFit(); // show 100% of the canvas once layout exists
        Closed += (_, _) => ScreenColorSampler.Stop();
    }

    /// <summary>Snapshot pre-mutation state for Ctrl+Z. Call BEFORE changing the document.</summary>
    private void PushUndo()
    {
        _undo.Capture(_doc);
        RefreshUndoButtons();
    }

    /// <summary>Snapshot once per inspector focus session (called before applying the edit).</summary>
    private void ArmInspectorUndo()
    {
        if (!_inspectorArmed) return;
        _inspectorArmed = false;
        PushUndo();
    }

    private void RefreshUndoButtons()
    {
        BtnUndo.IsEnabled = _undo.CanUndo;
        BtnRedo.IsEnabled = _undo.CanRedo;
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

    private void CanvasBgColor_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new ColorPickerDialog(_doc.CanvasColor, ColorPickerDialog.CanvasColors(_doc)) { Owner = this };
        if (dlg.ShowDialog() != true) return;
        PushUndo();
        _doc.CanvasTransparent = false;
        _doc.CanvasColor = dlg.SelectedColor;
        StatusText.Text = $"Canvas background set to {ToHex(_doc.CanvasColor)}.";
    }

    private void CanvasTransparent_Click(object sender, RoutedEventArgs e)
    {
        PushUndo();
        _doc.CanvasTransparent = CanvasTransparentMenuItem.IsChecked;
        StatusText.Text = _doc.CanvasTransparent
            ? "Canvas background is transparent — PNG export keeps no background."
            : $"Canvas background set to {ToHex(_doc.CanvasColor)}.";
    }

    /// <summary>The background used for export/copy: null when transparent, else the paper color.</summary>
    private Color? ExportBackground => _doc.CanvasTransparent ? (Color?)null : _doc.CanvasColor;

    // =================================================================
    // Selection
    // =================================================================

    private DesignElement? _watched;

    /// <summary>
    /// Rebuild the selection outline. While dragging, images get the cheap
    /// bounding outline (no pixel re-trace per mousemove); the exact contour
    /// is restored on mouse-up. Shapes are analytic and always exact.
    /// </summary>
    private void RefreshContour()
    {
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
        if (_watched != null) _watched.PropertyChanged -= Watched_PropertyChanged;
        _watched = _doc.Selected;
        if (_watched != null) _watched.PropertyChanged += Watched_PropertyChanged;

        SelectionAdorner.Target = _doc.Selected;
        RefreshContour();
        SelectionAdorner.InvalidateVisual();
        SelectionText.Text = _doc.Selected is null
            ? "Nothing selected"
            : $"{_doc.Selected.GetType().Name}  {_doc.Selected.Width:F0}×{_doc.Selected.Height:F0}  @({_doc.Selected.X:F0},{_doc.Selected.Y:F0})  ∠{_doc.Selected.RotationAngle:F0}°  Z={_doc.Selected.ZIndex}";
        RefreshInspector();
    }

    private void Watched_PropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        SelectionAdorner.InvalidateVisual();
        if (e.PropertyName is nameof(DesignElement.X) or nameof(DesignElement.Y)
            or nameof(DesignElement.Width) or nameof(DesignElement.Height)
            or nameof(DesignElement.RotationAngle) or nameof(DesignElement.ZIndex)
            or nameof(ImageElement.Source)
            or nameof(ShapeElement.FillColor1) or nameof(ShapeElement.FillColor2)
            or nameof(ShapeElement.IsGradient) or nameof(ShapeElement.GradientAngle)
            or nameof(ShapeElement.StrokeColor) or nameof(ShapeElement.StrokeThickness)
            or nameof(ShapeElement.CornerRadius))
        {
            OnSelectionChanged();
        }
    }

    // =================================================================
    // Mouse: Pick (select / move / resize / rotate) + Slice rectangle
    // =================================================================

    private void Pasteboard_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (_textEditor != null && e.OriginalSource is DependencyObject src
            && IsInsideVisual(src, _textEditor))
            return; // clicks inside the in-place editor belong to the TextBox

        Viewport.Focus(); // keep Ctrl+V / Delete shortcuts working
        var pos = e.GetPosition(Pasteboard);

        // Eyedropper: sample the canvas pixel and become primary (hook covers outside clicks).
        if (_tool == ActiveTool.EyeDropper && e.ChangedButton == MouseButton.Left)
        {
            ApplySampledColor(SampleCanvasColor(pos), "canvas");
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
            _drag = DragMode.Slice;
            _dragStartCanvas = pos;
            _sliceVisible = true; // a fresh drag always shows its marker
            _pendingSlice = new Rect(pos, new Size(0, 0));
            RefreshSliceLayer();
            Pasteboard.CaptureMouse();
            e.Handled = true;
            return;
        }

        if (_tool == ActiveTool.Rectangle && e.ChangedButton == MouseButton.Left)
        {
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
            if (PickTopmost(pos) is TextElement hitText) BeginTextEdit(hitText);
            else CreateTextAt(pos);
            e.Handled = true;
            return;
        }

        if (e.ChangedButton != MouseButton.Left) return;

        // Pick: double-click a text object to edit it in place.
        if (_tool == ActiveTool.Pick && e.ClickCount == 2 && PickTopmost(pos) is TextElement dbl)
        {
            SetTool(ActiveTool.Text);
            BeginTextEdit(dbl);
            e.Handled = true;
            return;
        }

        // 1. Handles first (only when something is selected).
        if (_doc.Selected != null)
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
            _doc.Select(picked);
            BeginMove(picked, pos);
            e.Handled = true;
            return;
        }

        // 3. Clicked empty pasteboard: clear selection.
        _doc.Select(null);
    }

    private void Pasteboard_MouseMove(object sender, MouseEventArgs e)
    {
        var pos = e.GetPosition(Pasteboard);

        if (_drag == DragMode.None && e.LeftButton == MouseButtonState.Released)
        {
            if (_tool == ActiveTool.EyeDropper)
            {
                var hover = SampleCanvasColor(pos);
                StatusText.Text = $"#{hover.R:X2}{hover.G:X2}{hover.B:X2} — click to sample (works outside the window too).";
                Cursor = Cursors.Cross;
            }
            else if (_tool == ActiveTool.Pick)
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

        if (_drag == DragMode.None || (_doc.Selected == null && _drag is not (DragMode.Slice or DragMode.Draw)))
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

    private void Pasteboard_MouseUp(object sender, MouseButtonEventArgs e)
    {
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
                    ? $"Slice {rect.Width:F0}×{rect.Height:F0} added ({_slices.Count} total) — Export / Ctrl+C copy / Delete-cut the active one."
                    : $"Slice {rect.Width:F0}×{rect.Height:F0} — Export / Ctrl+C copy / Delete-cut it. Turn on +Add slice to keep several.";
            }
            else
            {
                // Plain click: activate the topmost region under the cursor, if any.
                var pos = e.GetPosition(Pasteboard);
                var hit = _slices.LastOrDefault(r => r.Rect.Contains(pos));
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
        _drag = DragMode.None;
        _activeHandle = HandleKind.None;
        if (Pasteboard.IsMouseCaptured) Pasteboard.ReleaseMouseCapture();
        if (_doc.Selected != null) RefreshContour(); // exact ants back after the drag
    }

    // ----- drag primitives -----

    private void BeginMove(DesignElement el, Point pos)
    {
        PushUndo(); // one undo step per gesture, not per mousemove
        _drag = DragMode.Move;
        _dragStartCanvas = pos;
        _origX = el.X; _origY = el.Y;
        Pasteboard.CaptureMouse();
        Cursor = Cursors.SizeAll;
    }

    private void DoMove(DesignElement el, Point pos)
    {
        el.X = _origX + (pos.X - _dragStartCanvas.X);
        el.Y = _origY + (pos.Y - _dragStartCanvas.Y);
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
        Pasteboard.CaptureMouse();
    }

    /// <summary>
    /// Rotation-aware resize. Pointer positions are mapped into the element's
    /// local (unrotated) frame; the rect edge(s) for the active handle follow
    /// the pointer while the opposite edge stays pinned.
    /// </summary>
    private void DoResize(DesignElement el, Point pos)
    {
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

        // Shift on a corner = keep the original aspect ratio
        // (constrain). The opposite corner stays pinned while the box scales.
        bool lockAspect = (Keyboard.IsKeyDown(Key.LeftShift) || Keyboard.IsKeyDown(Key.RightShift))
            && _activeHandle is HandleKind.NW or HandleKind.NE or HandleKind.SW or HandleKind.SE;
        if (lockAspect && _origW > 0 && _origH > 0)
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
            AddSliceVisual(region.Rect, ReferenceEquals(region, _activeSlice));
        if (_drag == DragMode.Slice && _pendingSlice is Rect pr && pr.Width >= 1 && pr.Height >= 1)
            AddSliceVisual(pr, true);
    }

    private void AddSliceVisual(Rect rect, bool active)
    {
        var back = new System.Windows.Shapes.Rectangle
        {
            Stroke = Brushes.White,
            StrokeThickness = 1,
            StrokeDashArray = new DoubleCollection { 7, 5 },
            StrokeDashOffset = 6,
            Fill = Brushes.Transparent,
            IsHitTestVisible = false,
        };
        var front = new System.Windows.Shapes.Rectangle
        {
            Stroke = new SolidColorBrush(active
                ? Color.FromRgb(0x00, 0xBF, 0xFF)   // active: cyan
                : Color.FromRgb(0x8A, 0x94, 0xA6)), // inactive: dim gray
            StrokeThickness = 1,
            StrokeDashArray = new DoubleCollection { 7, 5 },
            Fill = Brushes.Transparent,
            IsHitTestVisible = false,
        };
        foreach (var r in new[] { back, front })
        {
            Canvas.SetLeft(r, rect.X);
            Canvas.SetTop(r, rect.Y);
            r.Width = Math.Max(1, rect.Width);
            r.Height = Math.Max(1, rect.Height);
            SliceLayer.Children.Add(r);
        }
    }

    private void ClearSlice()
    {
        _slices.Clear();
        _activeSlice = null;
        _pendingSlice = null;
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

        if (_doc.Selected is { } sel)
        {
            PushUndo();
            sel.X += dx;
            sel.Y += dy;
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
        var fill = new SolidColorBrush(_primaryColor);
        fill.Freeze();
        DrawRectVisual.Fill = fill;
        var stroke = new SolidColorBrush(CanvasSampler.Darken(_primaryColor, 0.65));
        stroke.Freeze();
        DrawRectVisual.Stroke = stroke;
        DrawRectVisual.StrokeThickness = 2;
        DrawRectVisual.RadiusX = 14;
        DrawRectVisual.RadiusY = 14;

        Canvas.SetLeft(DrawRectVisual, rect.X);
        Canvas.SetTop(DrawRectVisual, rect.Y);
        DrawRectVisual.Width = Math.Max(1, rect.Width);
        DrawRectVisual.Height = Math.Max(1, rect.Height);
        DrawRectVisual.Visibility = Visibility.Visible;
    }

    private void UpdateDrawVisual(Point pos) => PositionDrawVisual(NormalizedRect(_drawStart, pos));

    private void FinishDraw(Point pos)
    {
        var rect = NormalizedRect(_drawStart, pos);
        DrawRectVisual.Visibility = Visibility.Collapsed;
        if (rect.Width < 5 || rect.Height < 5) return; // plain click: draw nothing

        PushUndo();
        var el = new ShapeElement
        {
            X = rect.X, Y = rect.Y,
            Width = rect.Width, Height = rect.Height,
            CornerRadius = 14,
            FillColor1 = _primaryColor, IsGradient = false, // drawn in the project primary
            StrokeColor = CanvasSampler.Darken(_primaryColor, 0.65), StrokeThickness = 2,
        };
        _doc.Add(el); // selects the new shape
        StatusText.Text = $"Drew rectangle {rect.Width:F0}×{rect.Height:F0}. Drag another, or press V for Pick.";
        NoteIfOutsideCanvas();
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
                    if (el != null && el.ZIndex >= bestZ) { bestZ = el.ZIndex; found = el; }
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
        var te = new TextElement
        {
            X = pos.X,
            Y = pos.Y,
            TextColor1 = _primaryColor,
        };
        _doc.Add(te);
        te.EnableAnchoring();
        BeginTextEdit(te, snapshot: false);
        NoteIfOutsideCanvas();
        StatusText.Text = "Type your text — Enter adds a line, Esc or click outside finishes.";
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
        Viewport.ScrollToHorizontalOffset(_pasteOffsetX + anchorModel.X * _zoom - anchorView.X);
        Viewport.ScrollToVerticalOffset(_pasteOffsetY + anchorModel.Y * _zoom - anchorView.Y);
        UpdateZoomUI();
    }

    private void SetZoom(double z) =>
        ApplyZoom(z, ViewportCenterModel(),
            new Point(Viewport.ViewportWidth / 2, Viewport.ViewportHeight / 2));

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
        var model = Mouse.GetPosition(Pasteboard); // model coords (pre-zoom local)
        var view = e.GetPosition(Viewport);
        ApplyZoom(_zoom * (e.Delta > 0 ? 1.15 : 1 / 1.15), model, view);
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
        Title = $"YorteImpact — {txt}";
    }

    /// <summary>
    /// Fit 100% of the canvas (artboard) into the viewport and center it.
    /// The resulting zoom can be anything (25%, 63%, …); zooming in/out
    /// afterwards pivots around the viewport center.
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

        bool ctrl = Keyboard.IsKeyDown(Key.LeftCtrl) || Keyboard.IsKeyDown(Key.RightCtrl);

        if (ctrl && e.Key == Key.N) { NewCanvas_Click(sender, e); e.Handled = true; }
        else if (ctrl && e.Key == Key.OemComma) { OpenSettings(); e.Handled = true; }
        else if (ctrl && e.Key == Key.O) { OpenProject_Click(sender, e); e.Handled = true; }
        else if (ctrl && e.Key == Key.S && (Keyboard.IsKeyDown(Key.LeftShift) || Keyboard.IsKeyDown(Key.RightShift))) { SaveProjectAs_Click(sender, e); e.Handled = true; }
        else if (ctrl && e.Key == Key.S) { SaveProject_Click(sender, e); e.Handled = true; }
        else if (ctrl && e.Key == Key.C) { CopySelectionOrSlice(); e.Handled = true; }
        else if (ctrl && e.Key == Key.X) { CutSelectionOrSlice(); e.Handled = true; }
        else if (ctrl && e.Key == Key.F) { PickElementFill(); e.Handled = true; }
        else if (ctrl && e.Key == Key.R) { FitCanvasToSelection(); e.Handled = true; }
        else if (ctrl && e.Key == Key.V) { PasteFromClipboardOrPrototype(); e.Handled = true; }
        else if (ctrl && e.Key == Key.D) { if (_doc.Selected != null) PushUndo(); _doc.DuplicateSelected(); e.Handled = true; }
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
        else if (e.Key == Key.I && !ctrl) { SetTool(ActiveTool.EyeDropper); e.Handled = true; }
        else if (e.Key == Key.T && !ctrl) { SetTool(ActiveTool.Text); e.Handled = true; }
        else if (e.Key == Key.E && !ctrl) { SetTool(ActiveTool.Erase); e.Handled = true; }
        else if (e.Key == Key.Escape)
        {
            if (_drag == DragMode.Draw) DrawRectVisual.Visibility = Visibility.Collapsed;
            if (_drag != DragMode.None) { _drag = DragMode.None; Pasteboard.ReleaseMouseCapture(); }
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
        var slice = _sliceVisible ? _activeSlice : null;
        bool hasSlice = slice != null && !slice.IsEmpty;

        // A visible slice acts like a marquee: it defines WHAT gets copied.
        // With a selection, that means the slice area OF the selected object.
        if (hasSlice)
        {
            if (_doc.Selected is { } sel) CopySliceOfElement(sel, slice!.Rect);
            else CopySliceOfCanvas(slice!.Rect);
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
    private static void PutSliceOnClipboard(DesignElement el, Rect inter)
    {
        try { SetSystemClipboardImage(SliceExportService.CropElementRegion(el, inter, 1.0)); }
        catch { /* nothing renderable to share */ }
    }

    /// <summary>Copy the slice area of the selected element (the slice acts as a marquee).</summary>
    private void CopySliceOfElement(DesignElement el, Rect sliceRect)
    {
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
            PutSliceOnClipboard(el, inter);
            _copyClipboardSeq = SafeClipboardSeq();
        }
        catch (Exception ex)
        {
            StatusText.Text = "Copy failed: " + ex.Message;
        }
    }

    /// <summary>Copy the slice area off the canvas (no selection): only the on-canvas part is real content.</summary>
    private void CopySliceOfCanvas(Rect sliceRect)
    {
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
            try { SetSystemClipboardImage(SliceExportService.RenderRegion(ElementHost, region, ExportBackground, 1.0)); }
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
        var mouse = Mouse.GetPosition(Pasteboard);
        if (mouse.X >= 0 && mouse.Y >= 0 && mouse.X <= Pasteboard.Width && mouse.Y <= Pasteboard.Height)
            return mouse;
        return ViewportCenterModel();
    }

    private void PasteFromClipboardOrPrototype()
    {
        var at = PasteTarget();
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
        el.SetBitmap(bmp);
        el.X = at.X - el.Width / 2;
        el.Y = at.Y - el.Height / 2;
        _doc.Add(el);
        StatusText.Text = $"Pasted image {bmp.PixelWidth}×{bmp.PixelHeight}px.";
        NoteIfOutsideCanvas();
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
                bool fullyCovered = inter.X <= outer.X + 0.5 && inter.Y <= outer.Y + 0.5
                    && inter.Right >= outer.Right - 0.5 && inter.Bottom >= outer.Bottom - 0.5;
                if (fullyCovered)
                {
                    _doc.RemoveSelected();
                    StatusText.Text = "Selection was fully inside the slice — removed.";
                    return;
                }

                if (s is ImageElement im)
                {
                    var r = SliceCutService.EraseImageRegion(im, _activeSlice.Rect);
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
                    var flat = SliceCutService.CutShapeFlat(sh, _activeSlice.Rect);
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

    private bool ConfirmDiscard()
    {
        if (_doc.Elements.Count == 0) return true;
        return MessageBox.Show(this, "Discard the current canvas?", "YorteImpact",
            MessageBoxButton.YesNo, MessageBoxImage.Warning) == MessageBoxResult.Yes;
    }

    private void NewCanvas_Click(object sender, RoutedEventArgs e)
    {
        CommitTextEdit();
        var dlg = new NewCanvasDialog((int)_doc.ArtboardWidth, (int)_doc.ArtboardHeight,
            _doc.CanvasColor, _doc.CanvasTransparent) { Owner = this };
        if (dlg.ShowDialog() != true) return;
        if (!ConfirmDiscard()) return;
        PushUndo(); // New canvas itself is undoable
        _untitledCounter++;
        _projectPath = null;
        _doc.NewCanvas($"Untitled-{_untitledCounter}", dlg.CanvasWidth, dlg.CanvasHeight,
            dlg.BackgroundColor, dlg.IsTransparent);
        ClearSlice();
        _copiedPrototype = null;
        _primaryColor = System.Windows.Media.Color.FromRgb(0x3B, 0x82, 0xF6);
        UpdateInkSwatch();
        ZoomToFit();
        SetTool(ActiveTool.Pick);
        StatusText.Text = dlg.IsTransparent
            ? $"New transparent canvas {dlg.CanvasWidth}×{dlg.CanvasHeight}px."
            : $"New canvas {dlg.CanvasWidth}×{dlg.CanvasHeight}px, background {ToHex(dlg.BackgroundColor)}.";
    }

    private void Settings_Click(object sender, RoutedEventArgs e) => OpenSettings();

    private void OpenSettings()
    {
        var dlg = new SettingsDialog(AppSettings.Load()) { Owner = this };
        if (dlg.ShowDialog() != true) return;
        StatusText.Text =
            $"Settings saved — default canvas {dlg.Settings.DefaultCanvasWidth}×{dlg.Settings.DefaultCanvasHeight} px on next launch.";
    }

    private void OpenProject_Click(object sender, RoutedEventArgs e)
    {
        CommitTextEdit();
        var dlg = new OpenFileDialog { Filter = YorteProjectService.Filter };
        if (dlg.ShowDialog(this) != true) return;
        if (!ConfirmDiscard()) return;
        PushUndo();
        try
        {
            var file = YorteProjectService.Load(dlg.FileName);
            YorteProjectService.ApplyTo(file, _doc);
            _projectPath = dlg.FileName;
            if (TryParseColor(file.Primary, out var prim)) _primaryColor = prim;
            UpdateInkSwatch();
            ClearSlice();
            _copiedPrototype = null;
            ZoomToFit();
            SetTool(ActiveTool.Pick);
            StatusText.Text = $"Opened {System.IO.Path.GetFileName(dlg.FileName)} ({_doc.Elements.Count} elements).";
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, "Could not open project:\n" + ex.Message, "YorteImpact", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void SaveProject_Click(object sender, RoutedEventArgs e) => SaveDocument(forceDialog: false);

    private void SaveProjectAs_Click(object sender, RoutedEventArgs e) => SaveDocument(forceDialog: true);

    /// <summary>
    /// Save / Save As. The dialog offers the editable .yimpact project plus the
    /// flat image formats (PNG / GIF / WebP); the choice is picked from the file
    /// extension, so "Save" can also produce a PNG when no project path exists.
    /// </summary>
    private void SaveDocument(bool forceDialog)
    {
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
                SliceExportService.ExportCanvasRegion(ElementHost, artboard, ExportBackground, path, 96, _zoom);
                StatusText.Text = "Canvas exported to " + path;
                return;
            }

            YorteProjectService.Save(_doc, path, ToHex(_primaryColor));
            _projectPath = path;
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
        e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private void Pasteboard_Drop(object sender, DragEventArgs e)
    {
        if (!e.Data.GetDataPresent(DataFormats.FileDrop)) return;
        var files = (string[])e.Data.GetData(DataFormats.FileDrop);
        var pos = e.GetPosition(Pasteboard);
        bool added = false;
        foreach (var f in files)
        {
            var ext = System.IO.Path.GetExtension(f).ToLowerInvariant();
            if (ext is not (".png" or ".jpg" or ".jpeg" or ".bmp" or ".gif" or ".webp" or ".tif" or ".tiff"))
                continue;
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
                el.X = pos.X - el.Width / 2;
                el.Y = pos.Y - el.Height / 2;
                if (!added) { PushUndo(); added = true; } // one step per drop
                _doc.Add(el);
                pos.Offset(24, 24);
            }
            catch { /* skip unreadable file */ }
        }
        if (_doc.Selected != null)
        {
            NoteIfOutsideCanvas();
        }
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

    private void AttrCorner_ValueChanged(object sender, EventArgs e)
    {
        if (_suppressInspector || _doc.Selected is not { } el) return;
        ArmInspectorUndo();
        if (el is ShapeElement sh) sh.CornerRadius = AttrCorner.Value;
        else if (el is ImageElement im) im.CornerRadius = AttrCorner.Value;
    }

    private void AttrSoft_ValueChanged(object sender, EventArgs e)
    {
        if (_suppressInspector || _doc.Selected is not { } el) return;
        ArmInspectorUndo();
        el.SoftEdge = AttrSoft.Value;
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
        if (_doc.Selected is not { } el)
        {
            StatusText.Text = "Select an object first — Ctrl+R fits the canvas to it.";
            return;
        }
        PushUndo();
        _doc.ArtboardX = el.X;
        _doc.ArtboardY = el.Y;
        _doc.ArtboardWidth = el.Width;
        _doc.ArtboardHeight = el.Height;
        ZoomToFit();
        StatusText.Text = $"Canvas fitted to the selection ({el.Width:F0}×{el.Height:F0} px). Other objects kept their positions.";
    }

    /// <summary>Align the selected object to the canvas edges / center (snap to edges).</summary>
    private void AlignToCanvas_Click(object sender, RoutedEventArgs e)
    {
        if (_doc.Selected is not { } el) return;
        if (sender is not Button btn || btn.Tag is not string tag) return;
        PushUndo();
        double ax = _doc.ArtboardX, ay = _doc.ArtboardY, aw = _doc.ArtboardWidth, ah = _doc.ArtboardHeight;
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
        StatusText.Text = "Object aligned to the canvas.";
    }

    private void BringFront_Click(object sender, RoutedEventArgs e) { if (_doc.Selected != null) PushUndo(); _doc.BringToFront(); }
    private void BringFwd_Click(object sender, RoutedEventArgs e) { if (_doc.Selected != null) PushUndo(); _doc.BringForward(); }
    private void SendBwd_Click(object sender, RoutedEventArgs e) { if (_doc.Selected != null) PushUndo(); _doc.SendBackward(); }
    private void SendBack_Click(object sender, RoutedEventArgs e) { if (_doc.Selected != null) PushUndo(); _doc.SendToBack(); }
    private void Duplicate_Click(object sender, RoutedEventArgs e) { CommitTextEdit(); if (_doc.Selected != null) PushUndo(); _doc.DuplicateSelected(); }
    private void Delete_Click(object sender, RoutedEventArgs e) { CommitTextEdit(); DeleteSelectedRespectingSlice(); }

    private void Undo_Click(object sender, RoutedEventArgs e) { CommitTextEdit(); DoUndo(); }
    private void Redo_Click(object sender, RoutedEventArgs e) { CommitTextEdit(); DoRedo(); }

    private void DoUndo()
    {
        if (_drag != DragMode.None) return; // never yank state mid-gesture
        StatusText.Text = _undo.Undo(_doc) ? "Undone." : "Nothing to undo.";
        RefreshUndoButtons();
    }

    private void DoRedo()
    {
        if (_drag != DragMode.None) return;
        StatusText.Text = _undo.Redo(_doc) ? "Redone." : "Nothing to redo.";
        RefreshUndoButtons();
    }

    // =================================================================
    // Tools
    // =================================================================

    private void ToolPick_Click(object sender, RoutedEventArgs e) => SetTool(ActiveTool.Pick);
    private void ToolSlice_Click(object sender, RoutedEventArgs e) => SetTool(ActiveTool.Slice);
    private void ToolRect_Click(object sender, RoutedEventArgs e) => SetTool(ActiveTool.Rectangle);
    private void ToolText_Click(object sender, RoutedEventArgs e) => SetTool(ActiveTool.Text);
    private void ToolEye_Click(object sender, RoutedEventArgs e) => SetTool(ActiveTool.EyeDropper);
    private void ToolErase_Click(object sender, RoutedEventArgs e) => SetTool(ActiveTool.Erase);

    /// <summary>Top-bar "+" toggle: accumulate slices instead of replacing.</summary>
    private void SliceAdd_Click(object sender, RoutedEventArgs e)
    {
        _sliceAddMode = TopAddSliceBtn.IsChecked == true;
        StatusText.Text = _sliceAddMode
            ? "Slice: add mode — each drag adds another region."
            : "Slice: replace mode — each drag replaces the previous region.";
    }

    private void SetTool(ActiveTool tool)
    {
        if (_tool == ActiveTool.EyeDropper && tool != ActiveTool.EyeDropper)
            ScreenColorSampler.Stop(); // never leak the global click hook
        if (_editingText != null && tool != ActiveTool.Text)
            CommitTextEdit(); // leave the in-place editor before switching tools
        bool leftSlice = _tool == ActiveTool.Slice && tool != ActiveTool.Slice;
        _tool = tool;
        if (leftSlice) ClearSlice(); // slice regions belong to the Slice tool: leaving it resets them
        UpdateToolUI();
        if (tool == ActiveTool.EyeDropper)
        {
            ScreenColorSampler.StartGlobalClick(OnGlobalClick);
            StatusText.Text = "Eyedropper: click anywhere on screen to sample — it becomes the project primary. Esc cancels.";
            return;
        }
        StatusText.Text = tool switch
        {
            ActiveTool.Pick => "Pick tool: click to select, drag to move, handles resize (Shift = keep proportions). Ctrl+C copies selection or slice.",
            ActiveTool.Slice => _sliceAddMode
                ? "Slice tool (add): each drag adds a region — Export / Ctrl+C / Delete-cut the active one."
                : "Slice tool: each drag replaces the region — Export / Ctrl+C / Delete-cut it. +Add slice to accumulate.",
            ActiveTool.Rectangle => "Rectangle tool: drag on the canvas to draw a shape. V returns to Pick.",
            ActiveTool.Text => "Text tool: click the canvas and type. Enter = new line, Esc or click outside = done. RTL is automatic (button in the inspector).",
            ActiveTool.Erase => "Background remover: click an image to erase that color's connected area, or press Auto to clear the border background.",
            _ => StatusText.Text,
        };
    }

    private void UpdateToolUI()
    {
        SidePickBtn.IsChecked = _tool == ActiveTool.Pick;
        SideSliceBtn.IsChecked = _tool == ActiveTool.Slice;
        SideRectBtn.IsChecked = _tool == ActiveTool.Rectangle;
        SideTextBtn.IsChecked = _tool == ActiveTool.Text;
        SideEyeBtn.IsChecked = _tool == ActiveTool.EyeDropper;
        SideEraseBtn.IsChecked = _tool == ActiveTool.Erase;
        Cursor = _tool switch
        {
            ActiveTool.Pick => Cursors.Arrow,
            ActiveTool.Text => Cursors.IBeam,
            _ => Cursors.Cross,
        };

        // Attribute toolbar (tool settings row) — shows the active
        // tool's options here, never on the top bar.
        AttrPickPanel.Visibility = _tool == ActiveTool.Pick ? Visibility.Visible : Visibility.Collapsed;
        AttrSlicePanel.Visibility = _tool == ActiveTool.Slice ? Visibility.Visible : Visibility.Collapsed;
        AttrErasePanel.Visibility = _tool == ActiveTool.Erase ? Visibility.Visible : Visibility.Collapsed;
        bool hint = _tool is not (ActiveTool.Pick or ActiveTool.Slice or ActiveTool.Erase);
        AttrHint.Visibility = hint ? Visibility.Visible : Visibility.Collapsed;
        AttrToolName.Text = _tool switch
        {
            ActiveTool.Pick => "PICK",
            ActiveTool.Slice => "SLICE",
            ActiveTool.Rectangle => "RECTANGLE",
            ActiveTool.Text => "TEXT",
            ActiveTool.EyeDropper => "EYEDROPPER",
            ActiveTool.Erase => "ERASE BACKGROUND",
            _ => "PICK",
        };
        AttrHint.Text = _tool switch
        {
            ActiveTool.Rectangle => "Drag on the canvas to draw a rectangle in the working color.",
            ActiveTool.Text => "Click the canvas and type. Enter = new line; Esc or click outside finishes. Auto RTL.",
            ActiveTool.EyeDropper => "Click anywhere on screen to sample a color — it becomes the project primary.",
            _ => "",
        };
    }

    // =================================================================
    // Eyedropper: sample anywhere, set the project primary color
    // =================================================================

    private void OnGlobalClick(Point screenPoint)
    {
        // Low-level hook fires on the installing (UI) thread; be safe anyway.
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(() => OnGlobalClick(screenPoint));
            return;
        }
        try
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            if (ScreenColorSampler.IsPointInWindow(screenPoint, hwnd))
                return; // our own window: WPF handles canvas clicks, chrome clicks stay normal
            ApplySampledColor(ScreenColorSampler.SampleScreenPixel(screenPoint), "screen");
        }
        catch
        {
            SetTool(ActiveTool.Pick);
        }
    }

    /// <summary>Promote a sampled color to project primary (+ paint the selected shape).</summary>
    private void ApplySampledColor(System.Windows.Media.Color c, string source)
    {
        _primaryColor = c;
        UpdateInkSwatch();
        if (SelectedShape is { } sh)
        {
            PushUndo();
            sh.FillColor1 = c;
            StatusText.Text = $"Sampled {ToHex(c)} from {source} — working color, applied to the selected shape.";
        }
        else
        {
            StatusText.Text = $"Sampled {ToHex(c)} from {source} — working color (new shapes use it; click the PRIMARY well to open the picker).";
        }
        SetTool(ActiveTool.Pick); // pickers revert after one pick; hook stops in SetTool
    }

    private void UpdateInkSwatch()
    {
        InkSwatch.Background = new SolidColorBrush(_primaryColor);
        InkSwatch.ToolTip = $"Working color {ToHex(_primaryColor)} — click to open the picker (also applies to the selected shape)";
    }

    private void InkSwatch_Click(object sender, RoutedEventArgs e)
    {
        // Clicking the working-color well opens the in-app picker (like
        // working color box); choosing a color also paints the selection.
        var dlg = new ColorPickerDialog(_primaryColor, ColorPickerDialog.CanvasColors(_doc)) { Owner = this };
        if (dlg.ShowDialog() != true) return;
        _primaryColor = dlg.SelectedColor;
        UpdateInkSwatch();
        if (SelectedShape is { } sh)
        {
            PushUndo();
            sh.FillColor1 = _primaryColor;
            StatusText.Text = $"Primary set to {ToHex(_primaryColor)} — applied to the selected shape.";
        }
        else
        {
            StatusText.Text = $"Primary set to {ToHex(_primaryColor)} — new shapes use it.";
        }
    }

    /// <summary>Exact canvas color under a pasteboard point (no rendering: analytic + 1px reads).</summary>
    private System.Windows.Media.Color SampleCanvasColor(Point p)
    {
        var behind = CanvasBackgroundAt(p);
        var el = PickTopmost(p);
        if (el is ShapeElement s) return CanvasSampler.SampleShape(s, p, behind);
        if (el is ImageElement im) return CanvasSampler.SampleImagePixel(im, p, behind);
        return behind;
    }

    private System.Windows.Media.Color CanvasBackgroundAt(Point p) =>
        p.X >= _doc.ArtboardX && p.X <= _doc.ArtboardX + _doc.ArtboardWidth
        && p.Y >= _doc.ArtboardY && p.Y <= _doc.ArtboardY + _doc.ArtboardHeight
            ? System.Windows.Media.Colors.White
            : System.Windows.Media.Color.FromRgb(0x09, 0x0D, 0x15); // pasteboard blue-black

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
            InspectorPanel.IsEnabled = s != null;
            InspX.SetValue(s?.X ?? 0);
            InspY.SetValue(s?.Y ?? 0);
            InspW.SetValue(s?.Width ?? 0);
            InspH.SetValue(s?.Height ?? 0);
            double rot = s?.RotationAngle ?? 0;
            rot = ((rot + 180) % 360 + 360) % 360 - 180; // show as -180..180
            InspRot.SetValue(rot);
            InspRadius.SetValue(s switch
            {
                ShapeElement sh => sh.CornerRadius,
                ImageElement im => im.CornerRadius,
                _ => 0,
            });
            AttrOpacity.SetValue(s?.Opacity ?? 0);
            AttrCorner.SetValue(s switch
            {
                ShapeElement sh => sh.CornerRadius,
                ImageElement im => im.CornerRadius,
                _ => 0,
            });
            AttrSoft.SetValue(s?.SoftEdge ?? 0);
            AttrOpacity.IsEnabled = AttrCorner.IsEnabled = AttrSoft.IsEnabled = s != null;
            RefreshAppearance();
            RefreshShadow();
            RefreshText();
            RefreshTransparency();
        }
        finally { _suppressInspector = false; }
    }

    /// <summary>Programmatic text update that never yanks a field the user is typing in.</summary>
    private static void SetText(TextBox box, string text)
    {
        if (!box.IsKeyboardFocused) box.Text = text;
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
        GradTypeCombo.SelectedIndex = (int)sh.GradientType;
        StrokeBox.Background = new SolidColorBrush(sh.StrokeColor);
        SetText(StrokeText, ToHex(sh.StrokeColor));
        InspStrokeThick.SetValue(sh.StrokeThickness);

        UpdateAppearanceEnabled(sh.IsGradient, sh.GradientType);
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

    private void RefreshShadow()
    {
        var el = _doc.Selected;
        bool isText = el is TextElement;
        ShadowCard.Visibility = el == null || isText ? Visibility.Collapsed : Visibility.Visible;
        if (el == null || isText) return;

        ShadowTypeCombo.SelectedIndex = (int)el.ShadowStyle;
        ShadowColorBox.Background = new SolidColorBrush(el.ShadowColor);
        SetText(ShadowText, ToHex(el.ShadowColor));
        ShadowX.SetValue(el.ShadowX);
        ShadowY.SetValue(el.ShadowY);
        ShadowBlur.SetValue(el.ShadowBlur);
        ShadowOpacity.SetValue(el.ShadowOpacity);
        UpdateShadowVisibility(el.ShadowStyle != ShadowStyle.None);
    }

    private void UpdateShadowVisibility(bool on) =>
        ShadowOptions.Visibility = on ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>Free-hand edit: switch the preset label to Custom without re-applying it.</summary>
    private void MarkShadowCustom(DesignElement el)
    {
        if (el.ShadowStyle is ShadowStyle.Custom or ShadowStyle.None) return;
        el.ShadowStyle = ShadowStyle.Custom;
        _suppressInspector = true;
        ShadowTypeCombo.SelectedIndex = (int)ShadowStyle.Custom;
        _suppressInspector = false;
    }

    private void ShadowType_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressInspector || _doc.Selected is not { } el) return;
        if (ShadowTypeCombo.SelectedIndex < 0) return;
        var style = (ShadowStyle)ShadowTypeCombo.SelectedIndex;

        if (style == ShadowStyle.Custom)
        {
            if (el.ShadowStyle == ShadowStyle.Custom) return;
            PushUndo();
            if (el.ShadowStyle == ShadowStyle.None) el.ApplyShadowPreset(ShadowStyle.Medium);
            el.ShadowStyle = ShadowStyle.Custom;
        }
        else
        {
            PushUndo();
            el.ApplyShadowPreset(style);
        }

        RefreshInspector();
        StatusText.Text = style == ShadowStyle.None ? "Shadow removed." : $"Shadow: {style}.";
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
        OpacityDirectionCombo.SelectedIndex = (int)el.OpacityDirection;
        OpacityStrength.SetValue(el.OpacityStrength);
        OpacityStart.SetValue(el.OpacityStart);
        OpacityEnd.SetValue(el.OpacityEnd);
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
    }

    private void OpacityDirection_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressInspector || _doc.Selected is not { } el) return;
        if (OpacityDirectionCombo.SelectedIndex < 0) return;
        PushUndo();
        el.OpacityDirection = (OpacityFadeDirection)OpacityDirectionCombo.SelectedIndex;
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
        s.Width = Math.Max(MinSize, InspW.Value);
    }

    private void InspH_ValueChanged(object? sender, EventArgs e)
    {
        if (_suppressInspector || _doc.Selected is not { } s) return;
        ArmInspectorUndo();
        s.Height = Math.Max(MinSize, InspH.Value);
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
        ArmInspectorUndo();
        if (s is ShapeElement sh) sh.CornerRadius = InspRadius.Value;
        else if (s is ImageElement im) im.CornerRadius = InspRadius.Value;
    }

    private void InspStrokeThick_ValueChanged(object? sender, EventArgs e)
    {
        if (_suppressInspector || SelectedShape is not { } sh) return;
        ArmInspectorUndo();
        sh.StrokeThickness = InspStrokeThick.Value;
    }

    /// <summary>Ctrl+F: pick the selected shape's background (fill) color.</summary>
    private void PickElementFill()
    {
        if (SelectedShape is not { } sh)
        {
            StatusText.Text = "Select a shape first — Ctrl+F picks its background (fill) color.";
            return;
        }
        var dlg = new ColorPickerDialog(sh.FillColor1, ColorPickerDialog.CanvasColors(_doc)) { Owner = this };
        if (dlg.ShowDialog() != true) return;
        PushUndo();
        sh.FillColor1 = dlg.SelectedColor;
        OnSelectionChanged(); // refresh swatches + hex boxes
        StatusText.Text = $"Shape background set to {ToHex(dlg.SelectedColor)}.";
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
        var dlg = new ColorPickerDialog(original, ColorPickerDialog.CanvasColors(_doc)) { Owner = this };
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
}
