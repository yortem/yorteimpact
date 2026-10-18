using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using YorteImpact.Services;

namespace YorteImpact;

/// <summary>
/// New-canvas dialog. Two clear modes: <b>Manual</b> (type pixels) or a
/// size <b>preset</b> (pick a visual aspect-ratio card). Background is only
/// white or transparent — the exact color can be changed later on the canvas.
/// </summary>
public partial class NewCanvasDialog : Window
{
    public int CanvasWidth { get; private set; } = 1200;
    public int CanvasHeight { get; private set; } = 800;
    public Color BackgroundColor { get; private set; } = Colors.White;
    public bool IsTransparent { get; private set; }

    private sealed class Preset
    {
        public string Category { get; }
        public string Name { get; }
        public int W { get; }
        public int H { get; }
        public double ThumbW { get; }
        public double ThumbH { get; }
        public string SizeText => $"{W} × {H}";

        public Preset(string category, string name, int w, int h)
        {
            Category = category;
            Name = name;
            W = w;
            H = h;
            double s = System.Math.Min(56.0 / w, 44.0 / h);
            ThumbW = System.Math.Max(10, w * s);
            ThumbH = System.Math.Max(10, h * s);
        }
    }

    private readonly List<Preset> _presets = new();
    private bool _ready;    // ignore handler churn during construction
    private bool _syncing;  // ignore size<->card feedback loops

    public NewCanvasDialog(int currentWidth = 1200, int currentHeight = 800,
                           Color? background = null, bool transparent = false)
    {
        InitializeComponent();
        SourceInitialized += (_, _) => DarkTitleBar.Apply(this);
        BuildPresets();

        _syncing = true;
        WidthBox.Text = currentWidth.ToString();
        HeightBox.Text = currentHeight.ToString();
        _syncing = false;

        if (transparent) BgTransparent.IsChecked = true;
        else BgWhite.IsChecked = true;

        _ready = true;
        ChipManual.IsChecked = true;   // Manual is the default mode
        ShowCategory("Manual");
        UpdateManualPreview();
    }

    private void BuildPresets()
    {
        void Add(string cat, string name, int w, int h) =>
            _presets.Add(new Preset(cat, name, w, h));

        Add("Web", "Website", 1200, 800);
        Add("Web", "Full HD", 1920, 1080);
        Add("Web", "HD", 1280, 720);

        Add("Paper", "A4", 794, 1123);
        Add("Paper", "A3", 1123, 1587);
        Add("Paper", "Letter", 816, 1056);

        Add("Mobile", "Phone", 390, 844);

        Add("Social", "Square", 1080, 1080);
        Add("Social", "Story", 1080, 1920);
    }

    private void Category_Checked(object sender, RoutedEventArgs e)
    {
        if (!_ready || PresetList == null) return;
        if (sender is RadioButton { Tag: string cat })
            ShowCategory(cat);
    }

    /// <summary>Manual shows the px fields; every other category shows its cards.</summary>
    private void ShowCategory(string category)
    {
        bool manual = category == "Manual";
        ManualPanel.Visibility = manual ? Visibility.Visible : Visibility.Collapsed;
        PresetPanel.Visibility = manual ? Visibility.Collapsed : Visibility.Visible;
        if (manual) { UpdateManualPreview(); return; }

        PresetList.ItemsSource = _presets.Where(p => p.Category == category).ToList();
        var match = _presets.FirstOrDefault(
            p => p.Category == category && p.W == Parse(WidthBox) && p.H == Parse(HeightBox));
        object? selected = match;
        if (selected == null && PresetList.Items.Count > 0) selected = PresetList.Items[0];
        PresetList.SelectedItem = selected;
    }

    private static int Parse(TextBox box) =>
        int.TryParse(box.Text, out int v) ? v : 0;

    private void PresetList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_ready || _syncing) return;
        if (PresetList.SelectedItem is not Preset p) return;
        _syncing = true;
        WidthBox.Text = p.W.ToString();
        HeightBox.Text = p.H.ToString();
        _syncing = false;
    }

    private void Size_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_syncing) return;
        UpdateManualPreview();
    }

    /// <summary>Little live aspect-ratio sample next to the manual px fields.</summary>
    private void UpdateManualPreview()
    {
        if (ManualPreview == null) return;
        int w = Parse(WidthBox), h = Parse(HeightBox);
        if (w <= 0 || h <= 0) { ManualPreview.Width = 48; ManualPreview.Height = 36; return; }
        double s = System.Math.Min(96.0 / w, 72.0 / h);
        ManualPreview.Width = System.Math.Max(6, w * s);
        ManualPreview.Height = System.Math.Max(6, h * s);
    }

    private void Create_Click(object sender, RoutedEventArgs e)
    {
        if (!int.TryParse(WidthBox.Text, out int w) || !int.TryParse(HeightBox.Text, out int h)
            || w < 50 || h < 50 || w > 8000 || h > 8000)
        {
            ErrorText.Text = "Enter whole pixels, 50 – 8000 per side.";
            ErrorText.Visibility = Visibility.Visible;
            return;
        }
        CanvasWidth = w;
        CanvasHeight = h;
        IsTransparent = BgTransparent.IsChecked == true;
        BackgroundColor = Colors.White;
        DialogResult = true;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;
}
