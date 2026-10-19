using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using YorteImpact.Models;
using YorteImpact.Services;
using YorteImpact.ViewModels;

namespace YorteImpact.Controls;

/// <summary>
/// Self-contained color picker: a saturation/value field,
/// a hue strip, hex + RGB entry, a swatch palette and a live preview. It
/// blends with the app chrome and returns a
/// <see cref="Color"/> directly from <see cref="SelectedColor"/>.
/// </summary>
public partial class ColorPickerDialog : Window
{
    private double _h;      // 0..360
    private double _s = 1;  // 0..1
    private double _v = 1;  // 0..1
    private bool _suppress;
    private bool _screenPicking;

    public Color SelectedColor { get; private set; } = Colors.Black;

    /// <summary>Fires for every live color change so the caller can preview it
    /// on the canvas; OK keeps the last preview, Cancel reverts.</summary>
    public event Action<Color>? PreviewColorChanged;

    public ColorPickerDialog(Color initial, IEnumerable<Color>? canvasColors = null)
    {
        InitializeComponent();
        SourceInitialized += (_, _) => DarkTitleBar.Apply(this);
        BuildHueBar();
        BuildPalette();
        BuildPalettes();
        BuildCanvasColors(canvasColors);
        FromColor(initial);
        Loaded += (_, _) => Refresh();
    }

    // ---------------- hue strip + palette construction ----------------

    private void BuildHueBar()
    {
        var stops = new GradientStopCollection();
        for (int i = 0; i <= 6; i++)
            stops.Add(new GradientStop(HsvToRgb(i * 60, 1, 1), i / 6.0));
        HueBar.Fill = new LinearGradientBrush(stops, new Point(0.5, 0), new Point(0.5, 1));
    }

    private void BuildPalette()
    {
        // Compact modern palette: neutrals + soft pastels, one row.
        Color[] swatches =
        {
            Color.FromRgb(0x11, 0x18, 0x27), Color.FromRgb(0x6B, 0x72, 0x80),
            Color.FromRgb(0xD1, 0xD5, 0xDB), Colors.White,
            Color.FromRgb(0xFC, 0xA5, 0xA5), Color.FromRgb(0xFD, 0xBA, 0x74),
            Color.FromRgb(0xFD, 0xE6, 0x8A), Color.FromRgb(0xBB, 0xF7, 0xD0),
            Color.FromRgb(0xA7, 0xF3, 0xD0), Color.FromRgb(0xA5, 0xF3, 0xFC),
            Color.FromRgb(0xBF, 0xDB, 0xFE), Color.FromRgb(0xC7, 0xD2, 0xFE),
            Color.FromRgb(0xDD, 0xD6, 0xFE), Color.FromRgb(0xFB, 0xCF, 0xE8),
            Color.FromRgb(0xE9, 0xD5, 0xFF), Color.FromRgb(0xFE, 0xD7, 0xAA),
        };
        foreach (var c in swatches)
        {
            var swatch = MakeSwatch(c, 20);
            var captured = c;
            swatch.MouseLeftButtonDown += (_, _) => { FromColor(captured); Refresh(); };
            PaletteWrap.Children.Add(swatch);
        }
    }

    private static Border MakeSwatch(Color c, double size)
    {
        return new Border
        {
            Width = size, Height = size, Margin = new Thickness(2),
            Background = new SolidColorBrush(c),
            BorderBrush = new SolidColorBrush(Color.FromRgb(0x3A, 0x3A, 0x44)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(3),
            Cursor = Cursors.Hand,
            ToolTip = $"#{c.R:X2}{c.G:X2}{c.B:X2}",
        };
    }

    private void BuildCanvasColors(IEnumerable<Color>? colors)
    {
        var list = colors?.ToList() ?? new List<Color>();
        _canvasColors.Clear();
        _canvasColors.AddRange(list);
        CopyPaletteBtn.IsEnabled = list.Count > 0;
        if (list.Count == 0)
        {
            CanvasEmptyHint.Visibility = Visibility.Visible;
            return;
        }
        foreach (var c in list)
        {
            var captured = c;
            var border = MakeSwatch(captured, 20);
            border.MouseLeftButtonDown += (_, _) => { FromColor(captured); Refresh(); };
            CanvasWrap.Children.Add(border);
        }
    }

    private readonly List<Color> _canvasColors = new();

    private void CopyPalette_Click(object sender, RoutedEventArgs e)
    {
        if (_canvasColors.Count == 0) return;
        try
        {
            Clipboard.SetText(string.Join(", ",
                _canvasColors.Select(c => $"#{c.R:X2}{c.G:X2}{c.B:X2}")));
            Status("Copied the canvas palette.");
        }
        catch
        {
            Status("Clipboard is busy - try again.");
        }
    }

    // ---------------- image palette ----------------

    private void PasteImage_Click(object sender, RoutedEventArgs e)
    {
        var bmp = ClipboardHelper.TryGetImage();
        if (bmp == null)
        {
            Status("Clipboard has no image.");
            return;
        }
        ShowImagePalette(bmp);
    }

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.V && (Keyboard.Modifiers & ModifierKeys.Control) != 0
            && Keyboard.FocusedElement is not TextBox)
        {
            PasteImage_Click(this, new RoutedEventArgs());
            e.Handled = true;
        }
    }

    private void Window_DragOver(object sender, DragEventArgs e)
    {
        e.Effects = TryGetDroppedImage(e.Data) != null ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private void Window_Drop(object sender, DragEventArgs e)
    {
        var bmp = TryGetDroppedImage(e.Data);
        if (bmp == null)
        {
            Status("Drop a PNG, JPG, BMP, GIF or WebP image.");
            return;
        }
        ShowImagePalette(bmp);
        e.Handled = true;
    }

    private static BitmapSource? TryGetDroppedImage(IDataObject data)
    {
        if (!data.GetDataPresent(DataFormats.FileDrop)) return null;
        if (data.GetData(DataFormats.FileDrop) is not string[] files) return null;

        foreach (string? file in files)
        {
            if (file == null) continue;
            string ext = System.IO.Path.GetExtension(file).ToLowerInvariant();
            if (ext is not (".png" or ".jpg" or ".jpeg" or ".bmp" or ".gif" or ".webp" or ".tif" or ".tiff"))
                continue;
            try
            {
                var bmp = new BitmapImage();
                bmp.BeginInit();
                bmp.UriSource = new Uri(file, UriKind.Absolute);
                bmp.CacheOption = BitmapCacheOption.OnLoad;
                bmp.EndInit();
                if (bmp.CanFreeze) bmp.Freeze();
                return bmp;
            }
            catch { /* try the next file */ }
        }
        return null;
    }

    private void ShowImagePalette(BitmapSource source)
    {
        ImageWrap.Children.Clear();
        var colors = ImageColors(source);
        if (colors.Count == 0)
        {
            ImageEmptyHint.Visibility = Visibility.Visible;
            Status("Could not read colors from that image.");
            return;
        }

        ImageEmptyHint.Visibility = Visibility.Collapsed;
        foreach (var c in colors)
        {
            var captured = c;
            var border = MakeSwatch(captured, 22);
            border.MouseLeftButtonDown += (_, _) => { FromColor(captured); Refresh(); };
            ImageWrap.Children.Add(border);
        }
        Status($"Read {colors.Count} colors from the image.");
    }

    /// <summary>
    /// Distinct colors of an image, most-frequent first. The bitmap is scaled
    /// down, bucketed to 12-bit and near-duplicate buckets are merged so the
    /// result reads as a usable palette instead of a pixel histogram.
    /// </summary>
    private static List<Color> ImageColors(BitmapSource src, int max = 18)
    {
        var counts = new Dictionary<int, int>();
        try
        {
            const int target = 96;
            double scale = Math.Min(1.0,
                Math.Min((double)target / src.PixelWidth, (double)target / src.PixelHeight));
            BitmapSource small = scale < 1.0
                ? new TransformedBitmap(src, new ScaleTransform(scale, scale))
                : src;
            var conv = new FormatConvertedBitmap(small, PixelFormats.Bgra32, null, 0);
            int w = conv.PixelWidth, h = conv.PixelHeight;
            int stride = w * 4;
            var px = new byte[stride * h];
            conv.CopyPixels(px, stride, 0);

            for (int y = 0; y < h; y++)
            {
                int row = y * stride;
                for (int x = 0; x < w; x++)
                {
                    int i = row + x * 4;
                    if (px[i + 3] < 64) continue;
                    int key = (px[i + 2] >> 4) << 8 | (px[i + 1] >> 4) << 4 | (px[i] >> 4);
                    counts[key] = counts.TryGetValue(key, out int n) ? n + 1 : 1;
                }
            }
        }
        catch
        {
            return new List<Color>();
        }

        var result = new List<Color>();
        foreach (var kv in counts.OrderByDescending(kv => kv.Value))
        {
            var c = Color.FromRgb(
                (byte)(((kv.Key >> 8) & 0xF) * 16 + 8),
                (byte)(((kv.Key >> 4) & 0xF) * 16 + 8),
                (byte)((kv.Key & 0xF) * 16 + 8));
            if (result.Any(existing => Distance(existing, c) < 24)) continue;
            result.Add(c);
            if (result.Count >= max) break;
        }
        return result;
    }

    private static int Distance(Color a, Color b)
    {
        int dr = a.R - b.R, dg = a.G - b.G, db = a.B - b.B;
        return dr * dr + dg * dg + db * db;
    }

    private void Status(string text) => StatusText.Text = text;

    private void BuildPalettes()
    {
        foreach (var (name, hexes) in PalettePresets)
        {
            var sp = new StackPanel { Margin = new Thickness(0, 0, 0, 12) };
            sp.Children.Add(new TextBlock
            {
                Text = name,
                Foreground = new SolidColorBrush(Color.FromRgb(0xA0, 0xA0, 0xAB)),
                FontSize = 10,
                Margin = new Thickness(0, 0, 0, 4),
            });

            var grid = new UniformGrid { Rows = 1, Columns = hexes.Length };
            foreach (var hex in hexes)
            {
                Color c;
                try { c = (Color)ColorConverter.ConvertFromString(hex); }
                catch { continue; }
                var captured = c;
                var sw = new Border
                {
                    Height = 44,
                    Background = new SolidColorBrush(captured),
                    Cursor = Cursors.Hand,
                    ToolTip = hex.ToUpperInvariant(),
                };
                sw.MouseLeftButtonDown += (_, _) => { FromColor(captured); Refresh(); };
                grid.Children.Add(sw);
            }

            sp.Children.Add(new Border
            {
                CornerRadius = new CornerRadius(6),
                ClipToBounds = true,
                BorderBrush = new SolidColorBrush(Color.FromRgb(0x3A, 0x3A, 0x44)),
                BorderThickness = new Thickness(1),
                Child = grid,
            });
            PalettesHost.Children.Add(sp);
        }
    }

    // ---------------- canvas color extraction ----------------

    /// <summary>
    /// The distinct colors used by the document: the paper color, every shape's
    /// fill(s) and stroke, text fill/border, photo tone colors and a coarse
    /// sample of each image. Colors are bucketed to 12-bit and returned
    /// most-frequent-first so the picker can offer the artwork's own palette.
    /// </summary>
    public static List<Color> CanvasColors(DesignDocument doc)
    {
        var counts = new Dictionary<int, int>();

        void Add(Color c, int weight)
        {
            if (c.A == 0) return;
            int key = (c.R >> 4) << 8 | (c.G >> 4) << 4 | (c.B >> 4);
            counts[key] = counts.TryGetValue(key, out var n) ? n + weight : weight;
        }

        if (!doc.CanvasTransparent) Add(doc.CanvasColor, 2);

        foreach (var el in doc.Elements)
        {
            switch (el)
            {
                case ShapeElement s:
                    Add(s.FillColor1, 3);
                    if (s.IsGradient) Add(s.FillColor2, 3);
                    if (s.StrokeThickness > 0) Add(s.StrokeColor, 1);
                    break;
                case ImageElement im:
                    SampleImage(im.DisplaySource as BitmapSource, Add);
                    if (im.Adjustments.ToneAmount > 0) Add(im.Adjustments.ToneColor, 1);
                    break;
                case TextElement t:
                    Add(t.TextColor1, 3);
                    if (t.IsGradient) Add(t.TextColor2, 3);
                    if (t.HasBorder) Add(t.BorderColor, 1);
                    break;
            }
        }

        return counts
            .OrderByDescending(kv => kv.Value)
            .Take(48)
            .Select(kv => Color.FromRgb(
                (byte)(((kv.Key >> 8) & 0xF) * 16 + 8),
                (byte)(((kv.Key >> 4) & 0xF) * 16 + 8),
                (byte)((kv.Key & 0xF) * 16 + 8)))
            .ToList();
    }

    private static void SampleImage(BitmapSource? src, Action<Color, int> add)
    {
        if (src == null || src.PixelWidth < 1 || src.PixelHeight < 1) return;
        try
        {
            const int target = 32;
            double scale = Math.Min(1.0, Math.Min((double)target / src.PixelWidth, (double)target / src.PixelHeight));
            BitmapSource small = scale < 1.0
                ? new TransformedBitmap(src, new ScaleTransform(scale, scale))
                : src;
            var conv = new FormatConvertedBitmap(small, PixelFormats.Bgra32, null, 0);
            int w = conv.PixelWidth, h = conv.PixelHeight;
            int stride = w * 4;
            var px = new byte[stride * h];
            conv.CopyPixels(px, stride, 0);
            for (int y = 0; y < h; y++)
            {
                int row = y * stride;
                for (int x = 0; x < w; x++)
                {
                    int i = row + x * 4;
                    if (px[i + 3] < 32) continue; // skip near-transparent
                    add(Color.FromRgb(px[i + 2], px[i + 1], px[i]), 1);
                }
            }
        }
        catch { /* unreadable bitmap: just skip it */ }
    }

    private static readonly (string Name, string[] Hexes)[] PalettePresets =
    {
        ("Sunset Glow",     new[] { "#FF6B6B", "#FFA36B", "#FFD93D", "#6BCB77", "#4D96FF" }),
        ("Ocean Deep",      new[] { "#05445E", "#189AB4", "#75E6DA", "#D4F1F4", "#F9F9F9" }),
        ("Forest Mist",     new[] { "#2D6A4F", "#40916C", "#52B788", "#95D5B2", "#D8F3DC" }),
        ("Berry Smoothie",  new[] { "#6A0572", "#AB13B3", "#D65DB1", "#FF6F91", "#FFC75F" }),
        ("Desert Sand",     new[] { "#7F4F24", "#936639", "#A68A64", "#C2A878", "#E6CCB2" }),
        ("Neon Night",      new[] { "#0D0221", "#0F084B", "#26408B", "#3E92CC", "#A6CFD5" }),
        ("Pastel Dream",    new[] { "#FFADAD", "#FFD6A5", "#FDFFB6", "#CAFFBF", "#9BF6FF" }),
        ("Coffee House",    new[] { "#271C19", "#55423D", "#965B3B", "#C68B59", "#E5C6A0" }),
        ("Fresh Citrus",    new[] { "#F9C74F", "#F9844A", "#F94144", "#90BE6D", "#43AA8B" }),
        ("Purple Haze",     new[] { "#240046", "#3C096C", "#7B2CBF", "#9D4EDD", "#C77DFF" }),
        ("Mono Slate",      new[] { "#0F172A", "#334155", "#64748B", "#CBD5E1", "#F1F5F9" }),
        ("Retro Pop",       new[] { "#F72585", "#B5179E", "#7209B7", "#3A0CA3", "#4361EE" }),
        ("Vintage Warm",    new[] { "#8C2F2F", "#B85C38", "#E0A458", "#EBCB8B", "#F2E9E4" }),
        ("Mint Fresh",      new[] { "#00B4D8", "#48CAE4", "#90E0EF", "#CAF0F8", "#E0FBFC" }),
    };

    // ---------------- state <-> UI ----------------

    private Color Current => HsvToRgb(_h, _s, _v);

    private void FromColor(Color c)
    {
        RgbToHsv(c, out _h, out _s, out _v);
        SelectedColor = c;
    }

    private void Refresh()
    {
        // SV field: white -> pure hue horizontally, transparent -> black vertically.
        SvSquare.Fill = new LinearGradientBrush(Colors.White, HsvToRgb(_h, 1, 1), new Point(0, 0.5), new Point(1, 0.5));
        SvShade.Fill = new LinearGradientBrush(
            Color.FromArgb(0, 0, 0, 0), Colors.Black, new Point(0.5, 0), new Point(0.5, 1));

        double w = SvHost.ActualWidth <= 0 ? 230 : SvHost.ActualWidth;
        double h = SvHost.ActualHeight <= 0 ? 230 : SvHost.ActualHeight;
        Canvas.SetLeft(Crosshair, _s * w - Crosshair.Width / 2);
        Canvas.SetTop(Crosshair, (1 - _v) * h - Crosshair.Height / 2);

        double hueH = HueHost.ActualHeight <= 0 ? 230 : HueHost.ActualHeight;
        Canvas.SetTop(HueMarker, _h / 360.0 * hueH - HueMarker.Height / 2);

        var c = Current;
        SelectedColor = c;
        PreviewColorChanged?.Invoke(c);
        PreviewSwatch.Background = new SolidColorBrush(c);

        _suppress = true;
        HexBox.Text = $"#{c.R:X2}{c.G:X2}{c.B:X2}";
        RBox.Text = c.R.ToString(CultureInfo.InvariantCulture);
        GBox.Text = c.G.ToString(CultureInfo.InvariantCulture);
        BBox.Text = c.B.ToString(CultureInfo.InvariantCulture);
        _suppress = false;
    }

    // ---------------- interaction ----------------

    private void SetSv(Point p)
    {
        double w = SvHost.ActualWidth <= 0 ? 230 : SvHost.ActualWidth;
        double h = SvHost.ActualHeight <= 0 ? 230 : SvHost.ActualHeight;
        _s = Math.Clamp(p.X / w, 0, 1);
        _v = Math.Clamp(1 - p.Y / h, 0, 1);
        Refresh();
    }

    private void SetHue(Point p)
    {
        double h = HueHost.ActualHeight <= 0 ? 230 : HueHost.ActualHeight;
        _h = Math.Clamp(p.Y / h, 0, 1) * 360.0;
        Refresh();
    }

    private void Sv_MouseDown(object sender, MouseButtonEventArgs e)
    {
        SvHost.CaptureMouse();
        SetSv(e.GetPosition(SvHost));
    }

    private void Sv_MouseMove(object sender, MouseEventArgs e)
    {
        if (e.LeftButton == MouseButtonState.Pressed && SvHost.IsMouseCaptured)
            SetSv(e.GetPosition(SvHost));
    }

    private void Sv_MouseUp(object sender, MouseButtonEventArgs e)
    {
        if (SvHost.IsMouseCaptured) SvHost.ReleaseMouseCapture();
    }

    private void Hue_MouseDown(object sender, MouseButtonEventArgs e)
    {
        HueHost.CaptureMouse();
        SetHue(e.GetPosition(HueHost));
    }

    private void Hue_MouseMove(object sender, MouseEventArgs e)
    {
        if (e.LeftButton == MouseButtonState.Pressed && HueHost.IsMouseCaptured)
            SetHue(e.GetPosition(HueHost));
    }

    private void Hue_MouseUp(object sender, MouseButtonEventArgs e)
    {
        if (HueHost.IsMouseCaptured) HueHost.ReleaseMouseCapture();
    }

    private void Hex_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_suppress) return;
        try
        {
            if (ColorConverter.ConvertFromString(HexBox.Text.Trim()) is Color c)
            {
                _suppress = true;
                FromColor(c);
                _suppress = false;
                Refresh();
            }
        }
        catch (FormatException) { /* keep typing */ }
    }

    private void Rgb_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_suppress) return;
        if (byte.TryParse(RBox.Text, out var r) &&
            byte.TryParse(GBox.Text, out var g) &&
            byte.TryParse(BBox.Text, out var b))
        {
            FromColor(Color.FromRgb(r, g, b));
            Refresh();
        }
    }

    private void ScreenPick_Click(object sender, RoutedEventArgs e)
    {
        if (_screenPicking) return;
        _screenPicking = true;
        try
        {
            var overlay = new Window
            {
                WindowStyle = WindowStyle.None,
                AllowsTransparency = true,
                Background = Brushes.Transparent,
                Content = new Border { Background = Brushes.Transparent },
                Topmost = true,
                ShowInTaskbar = false,
                ResizeMode = ResizeMode.NoResize,
                Cursor = Cursors.Cross,
                WindowStartupLocation = WindowStartupLocation.Manual,
                Left = SystemParameters.VirtualScreenLeft,
                Top = SystemParameters.VirtualScreenTop,
                Width = SystemParameters.VirtualScreenWidth,
                Height = SystemParameters.VirtualScreenHeight,
                Owner = this,
            };

            Color? picked = null;
            overlay.MouseLeftButtonDown += (_, me) =>
            {
                picked = ScreenColorSampler.SampleScreenPixel(
                    overlay.PointToScreen(me.GetPosition(overlay)));
                overlay.DialogResult = true;
            };
            overlay.PreviewKeyDown += (_, ke) =>
            {
                if (ke.Key == Key.Escape) overlay.DialogResult = false;
            };

            overlay.ShowDialog();
            if (picked is { } color)
            {
                FromColor(color);
                Refresh();
            }
        }
        finally
        {
            _screenPicking = false;
        }
    }

    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        SelectedColor = Current;
        DialogResult = true;
    }

    // ---------------- color math ----------------

    private static Color HsvToRgb(double h, double s, double v)
    {
        h = ((h % 360) + 360) % 360;
        double c = v * s;
        double x = c * (1 - Math.Abs((h / 60.0) % 2 - 1));
        double m = v - c;
        double r, g, b;
        if (h < 60) { r = c; g = x; b = 0; }
        else if (h < 120) { r = x; g = c; b = 0; }
        else if (h < 180) { r = 0; g = c; b = x; }
        else if (h < 240) { r = 0; g = x; b = c; }
        else if (h < 300) { r = x; g = 0; b = c; }
        else { r = c; g = 0; b = x; }
        return Color.FromRgb(
            (byte)Math.Round((r + m) * 255),
            (byte)Math.Round((g + m) * 255),
            (byte)Math.Round((b + m) * 255));
    }

    private static void RgbToHsv(Color c, out double h, out double s, out double v)
    {
        double r = c.R / 255.0, g = c.G / 255.0, b = c.B / 255.0;
        double max = Math.Max(r, Math.Max(g, b));
        double min = Math.Min(r, Math.Min(g, b));
        double d = max - min;
        v = max;
        s = max <= 0 ? 0 : d / max;
        if (d <= 0) { h = 0; return; }
        if (max == r) h = 60 * (((g - b) / d) % 6);
        else if (max == g) h = 60 * ((b - r) / d + 2);
        else h = 60 * ((r - g) / d + 4);
        if (h < 0) h += 360;
    }
}
