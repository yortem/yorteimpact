using System;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace YorteImpact.Controls;

/// <summary>
/// Compact 0..N numeric field with up/down steppers and a dropdown that opens a
/// slider. Used by the attribute toolbar so any percentage
/// value (transparency, corner, soft edge, …) can be dragged or typed.
/// </summary>
public partial class ValueSlider : UserControl
{
    private bool _suppress;
    private double _value;
    private int _decimals = 2;

    // Press-and-drag "scrub" on the dropdown button: hold and move sideways to
    // grab the value immediately, instead of having to aim at the popup slider.
    private bool _scrubbing;
    private bool _didScrub;
    private bool _popupWasOpen;
    private double _scrubStartX;
    private double _scrubStartValue;

    public ValueSlider()
    {
        InitializeComponent();
        Slider.ValueChanged += OnSliderChanged;
        Box.TextChanged += OnBoxChanged;
        Box.KeyDown += OnBoxKeyDown;
        UpBtn.Click += (_, _) => Nudge(+Step);
        DownBtn.Click += (_, _) => Nudge(-Step);
        DropBtn.Checked += (_, _) => SliderPopup.IsOpen = true;
        DropBtn.Unchecked += (_, _) => SliderPopup.IsOpen = false;
        SliderPopup.Closed += (_, _) => DropBtn.IsChecked = false;

        DropBtn.PreviewMouseLeftButtonDown += Drop_MouseDown;
        DropBtn.PreviewMouseMove += Drop_MouseMove;
        DropBtn.PreviewMouseLeftButtonUp += Drop_MouseUp;
    }

    public string Caption { get => CaptionText.Text; set => CaptionText.Text = value; }
    public string Suffix { get => SuffixText.Text; set => SuffixText.Text = value; }
    public double Minimum { get => Slider.Minimum; set => Slider.Minimum = value; }
    public double Maximum { get => Slider.Maximum; set => Slider.Maximum = value; }
    public double Step { get; set; } = 1;

    private bool _compact;

    /// <summary>Narrow variant for two-field inspector rows.</summary>
    public bool Compact
    {
        get => _compact;
        set
        {
            _compact = value;
            Box.Width = value ? 30 : 40;
            Box.Padding = value ? new Thickness(3, 2, 3, 2) : new Thickness(5, 3, 5, 3);
            Box.FontSize = value ? 10.5 : 11.5;
            UpBtn.Width = DownBtn.Width = value ? 11 : 15;
            UpBtn.Height = DownBtn.Height = value ? 10 : 12;
            DropBtn.Width = value ? 13 : 18;
            DropBtn.Height = value ? 22 : 24;
            SuffixText.FontSize = value ? 9.5 : 10.5;
            SuffixText.Margin = value ? new Thickness(2, 0, 0, 0) : new Thickness(4, 0, 0, 0);
        }
    }

    /// <summary>Decimal places shown/kept. 0 = whole numbers only (rounded).</summary>
    public int Decimals
    {
        get => _decimals;
        set
        {
            int d = Math.Clamp(value, 0, 6);
            if (d == _decimals) return;
            _decimals = d;
            SetValue(_value);
        }
    }

    /// <summary>The inner editor, so the host can hook focus for undo arming.</summary>
    public TextBox Editor => Box;

    public double Value => _value;
    public event EventHandler? ValueChanged;

    /// <summary>Set the displayed value; <paramref name="raise"/> fires <see cref="ValueChanged"/>.</summary>
    public void SetValue(double v, bool raise = false)
    {
        v = Math.Clamp(v, Slider.Minimum, Slider.Maximum);
        v = _decimals <= 0 ? Math.Round(v) : Math.Round(v, _decimals);
        _value = v;
        _suppress = true;
        Slider.Value = v;
        Box.Text = _decimals <= 0
            ? v.ToString("F0", CultureInfo.InvariantCulture)
            : v.ToString("0." + new string('#', _decimals), CultureInfo.InvariantCulture);
        _suppress = false;
        if (raise) ValueChanged?.Invoke(this, EventArgs.Empty);
    }

    private void OnSliderChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_suppress) return;
        SetValue(e.NewValue, raise: true);
    }

    private void OnBoxChanged(object sender, TextChangedEventArgs e)
    {
        if (_suppress) return;
        if (double.TryParse(Box.Text, NumberStyles.Any, CultureInfo.InvariantCulture, out var v))
            SetValue(v, raise: true);
    }

    private void OnBoxKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Up) { Nudge(+Step); e.Handled = true; }
        else if (e.Key == Key.Down) { Nudge(-Step); e.Handled = true; }
    }

    private void Nudge(double delta) => SetValue(_value + delta, raise: true);

    // ---- press-and-drag scrub on the dropdown triangle ----

    private void Drop_MouseDown(object sender, MouseButtonEventArgs e)
    {
        _scrubbing = true;
        _didScrub = false;
        _popupWasOpen = SliderPopup.IsOpen;
        _scrubStartX = e.GetPosition(this).X;
        _scrubStartValue = _value;

        DropBtn.CaptureMouse();
        e.Handled = true;
    }

    private void Drop_MouseMove(object sender, MouseEventArgs e)
    {
        if (!_scrubbing) return;
        double dx = e.GetPosition(this).X - _scrubStartX;
        if (Math.Abs(dx) < 2) return;

        if (!_didScrub)
        {
            _didScrub = true;
            if (!SliderPopup.IsOpen) DropBtn.IsChecked = true; // show while scrubbing
        }

        double span = Slider.Maximum - Slider.Minimum;
        SetValue(_scrubStartValue + dx * span / 220.0, raise: true);
    }

    private void Drop_MouseUp(object sender, MouseButtonEventArgs e)
    {
        if (!_scrubbing) return;
        _scrubbing = false;
        DropBtn.ReleaseMouseCapture();

        if (!_didScrub)
        {
            // A plain click toggles on release (opening on mousedown made the
            // StaysOpen=False popup close under the same click).
            bool open = !_popupWasOpen;
            DropBtn.IsChecked = open;
            SliderPopup.IsOpen = open;
        }
        // A drag keeps the popup open for fine-tuning.

        e.Handled = true;
    }
}
