using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace YorteImpact.Controls;

/// <summary>
/// Compact 0..N numeric field with up/down steppers and a transient slider.
/// Press-drag the number to scrub the value; releasing closes the slider.
/// Double-click or Ctrl+A selects the text for typing.
/// </summary>
public partial class ValueSlider : UserControl
{
    private bool _suppress;
    private double _value;
    private int _decimals = 2;
    private static readonly HashSet<ValueSlider> OpenPopups = new();
    private bool _scrubTracking;
    private bool _scrubActive;
    private Point _scrubStart;
    private double _scrubStartValue;

    public ValueSlider()
    {
        InitializeComponent();
        Slider.ValueChanged += OnSliderChanged;
        InlineSlider.ValueChanged += OnSliderChanged;
        Box.TextChanged += OnBoxChanged;
        Box.KeyDown += OnBoxKeyDown;
        Box.PreviewMouseLeftButtonDown += Box_PreviewMouseDown;
        Box.PreviewMouseMove += Box_PreviewMouseMove;
        Box.PreviewMouseLeftButtonUp += Box_PreviewMouseUp;
        Box.LostMouseCapture += (_, _) => EndScrub(close: true);
        Box.LostFocus += (_, _) => CommitBoxText();
        PreviewKeyDown += OnValueKeyDown;
        Slider.PreviewKeyDown += OnValueKeyDown;
        PreviewMouseLeftButtonDown += (_, _) =>
        {
            if (_inline) InlineSlider.Focus();
            else Box.Focus();
        };
        UpBtn.Click += (_, _) => Nudge(+Step);
        DownBtn.Click += (_, _) => Nudge(-Step);
        SliderPopup.Opened += (_, _) =>
        {
            foreach (var other in OpenPopups.ToList())
            {
                if (!ReferenceEquals(other, this)) other.SliderPopup.IsOpen = false;
            }
            OpenPopups.Add(this);
        };
        SliderPopup.Closed += (_, _) =>
        {
            OpenPopups.Remove(this);
            EndScrub(close: false);
        };
        Slider.PreviewMouseLeftButtonUp += (_, _) => SliderPopup.IsOpen = false;
        Unloaded += (_, _) =>
        {
            SliderPopup.IsOpen = false;
            OpenPopups.Remove(this);
        };
    }

    /// <summary>Close every open numeric popup except when the pointer is inside one.</summary>
    public static void CloseAllPopups()
    {
        foreach (var slider in OpenPopups.ToList())
        {
            if (slider.SliderPopup.Child?.IsMouseOver == true) continue;
            slider.SliderPopup.IsOpen = false;
        }
    }

    public string Caption
    {
        get => CaptionText.Text;
        set { CaptionText.Text = value; InlineCaption.Text = value; }
    }

    public string Suffix
    {
        get => SuffixText.Text;
        set { SuffixText.Text = value; UpdateInlineText(); }
    }

    public double Minimum
    {
        get => Slider.Minimum;
        set { Slider.Minimum = value; InlineSlider.Minimum = value; }
    }

    public double Maximum
    {
        get => Slider.Maximum;
        set { Slider.Maximum = value; InlineSlider.Maximum = value; }
    }

    public double Step { get; set; } = 1;

    /// <summary>Show a label, live value and a permanent inline slider instead of the popup editor.</summary>
    public bool Inline
    {
        get => _inline;
        set
        {
            _inline = value;
            CompactRoot.Visibility = value ? Visibility.Collapsed : Visibility.Visible;
            InlineRoot.Visibility = value ? Visibility.Visible : Visibility.Collapsed;
            if (value) SliderPopup.IsOpen = false;
            UpdateInlineText();
        }
    }

    private bool _inline;

    /// <summary>
    /// Optional colored track for the inline slider (for example blue→warm for
    /// temperature). Null keeps the normal accent track.
    /// </summary>
    public Brush? TrackBrush
    {
        get => _trackBrush;
        set
        {
            _trackBrush = value;
            if (InlineSlider == null) return;
            if (value == null)
            {
                InlineSlider.ClearValue(Control.BackgroundProperty);
                InlineSlider.Style = (Style)Resources["VsSlider"];
            }
            else
            {
                InlineSlider.Style = (Style)Resources["VsGradientSlider"];
                InlineSlider.Background = value;
            }
        }
    }

    private Brush? _trackBrush;

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
    public void SetValue(double v, bool raise = false, bool updateBox = true, bool forceBox = false)
    {
        v = Math.Clamp(v, Slider.Minimum, Slider.Maximum);
        v = _decimals <= 0 ? Math.Round(v) : Math.Round(v, _decimals);
        _value = v;
        _suppress = true;
        Slider.Value = v;
        InlineSlider.Value = v;
        if (updateBox && (!Box.IsKeyboardFocused || forceBox))
        {
            Box.Text = _decimals <= 0
                ? v.ToString("F0", CultureInfo.InvariantCulture)
                : v.ToString("0." + new string('#', _decimals), CultureInfo.InvariantCulture);
        }
        UpdateInlineText();
        _suppress = false;
        if (raise) ValueChanged?.Invoke(this, EventArgs.Empty);
    }

    private void CommitBoxText()
    {
        if (double.TryParse(Box.Text, NumberStyles.Any, CultureInfo.InvariantCulture, out var v))
        {
            if (Math.Abs(v - _value) > 0.000001) SetValue(v, raise: true, forceBox: true);
            else SetValue(_value, forceBox: true); // normalize formatting without another change event
        }
        else
        {
            SetValue(_value);
        }
    }

    private void UpdateInlineText()
    {
        if (InlineValue == null) return;
        string value = _decimals <= 0
            ? _value.ToString("F0", CultureInfo.InvariantCulture)
            : _value.ToString("F" + _decimals, CultureInfo.InvariantCulture);
        string suffix = SuffixText.Text;
        InlineValue.Text = string.IsNullOrWhiteSpace(suffix) ? value : $"{value} {suffix}";
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
            SetValue(v, raise: true, updateBox: false);
    }

    private void OnBoxKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Up) { Nudge(+Step); e.Handled = true; }
        else if (e.Key == Key.Down) { Nudge(-Step); e.Handled = true; }
        else if (e.Key == Key.Enter) { CommitBoxText(); e.Handled = true; }
    }

    /// <summary>Up/Down changes the value from any part of the control (field, arrows or slider).</summary>
    private void OnValueKeyDown(object sender, KeyEventArgs e)
    {
        if (!_inline && e.Key == Key.A && (Keyboard.Modifiers & ModifierKeys.Control) != 0)
        {
            Box.Focus();
            Box.SelectAll();
            e.Handled = true;
            return;
        }

        double step = Step > 0 ? Step : 1;
        if (e.Key == Key.Up) { Nudge(+step); e.Handled = true; }
        else if (e.Key == Key.Down) { Nudge(-step); e.Handled = true; }
    }

    private void Nudge(double delta) => SetValue(_value + delta, raise: true, forceBox: true);

    private void Box_PreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left || _inline) return;

        if (e.ClickCount >= 2)
        {
            EndScrub(close: true);
            Box.Focus();
            Box.SelectAll();
            e.Handled = true;
            return;
        }

        _scrubTracking = true;
        _scrubActive = false;
        _scrubStart = e.GetPosition(this);
        _scrubStartValue = _value;
        Box.Focus();
        Box.CaptureMouse();
        int caret = Box.GetCharacterIndexFromPoint(e.GetPosition(Box), true);
        if (caret >= 0) Box.CaretIndex = caret;
        e.Handled = true;
    }

    private void Box_PreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (!_scrubTracking || e.LeftButton != MouseButtonState.Pressed || _inline) return;

        var pos = e.GetPosition(this);
        if (!_scrubActive)
        {
            if (Math.Abs(pos.X - _scrubStart.X) < 3) return;
            _scrubActive = true;
            SliderPopup.IsOpen = true;
            Box.CaptureMouse();
        }

        double range = Slider.Maximum - Slider.Minimum;
        if (range <= 0) return;
        double track = Slider.Width > 0 ? Slider.Width : 190;
        double delta = (pos.X - _scrubStart.X) / track * range;
        SetValue(_scrubStartValue + delta, raise: true, forceBox: true);
        e.Handled = true;
    }

    private void Box_PreviewMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (!_scrubTracking) return;
        bool wasActive = _scrubActive;
        EndScrub(close: true);
        if (wasActive) e.Handled = true;
    }

    private void EndScrub(bool close)
    {
        _scrubTracking = false;
        _scrubActive = false;
        if (Box.IsMouseCaptured) Box.ReleaseMouseCapture();
        if (close) SliderPopup.IsOpen = false;
    }
}
