using System.Windows;
using System.Windows.Navigation;
using System.Diagnostics;
using YorteImpact.Services;

namespace YorteImpact;

/// <summary>
/// Edits the persisted startup defaults (default canvas size + background).
/// </summary>
public partial class SettingsDialog : Window
{
    public AppSettings Settings { get; }

    public SettingsDialog(AppSettings settings, bool showIntegrations = false)
    {
        InitializeComponent();
        SourceInitialized += (_, _) => DarkTitleBar.Apply(this);

        Settings = settings;
        WidthBox.Text = settings.DefaultCanvasWidth.ToString();
        HeightBox.Text = settings.DefaultCanvasHeight.ToString();
        PixabayKeyBox.Text = settings.PixabayApiKey ?? "";
        if (settings.DefaultCanvasTransparent) BgTransparent.IsChecked = true;
        else BgWhite.IsChecked = true;
        if (showIntegrations) IntegrationsTab_Click(this, new RoutedEventArgs());
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        if (!int.TryParse(WidthBox.Text, out int w) || !int.TryParse(HeightBox.Text, out int h)
            || w < 1 || h < 1 || w > 8000 || h > 8000)
        {
            ErrorText.Text = "Enter whole pixels, 1 – 8000 per side.";
            ErrorText.Visibility = Visibility.Visible;
            return;
        }

        Settings.DefaultCanvasWidth = w;
        Settings.DefaultCanvasHeight = h;
        Settings.DefaultCanvasTransparent = BgTransparent.IsChecked == true;
        Settings.PixabayApiKey = PixabayKeyBox.Text.Trim();
        Settings.Save();
        DialogResult = true;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;

    private void GeneralTab_Click(object sender, RoutedEventArgs e)
    {
        GeneralTab.Visibility = Visibility.Visible;
        IntegrationsTab.Visibility = Visibility.Collapsed;
        GeneralTabBtn.IsChecked = true;
        IntegrationsTabBtn.IsChecked = false;
    }

    private void IntegrationsTab_Click(object sender, RoutedEventArgs e)
    {
        GeneralTab.Visibility = Visibility.Collapsed;
        IntegrationsTab.Visibility = Visibility.Visible;
        GeneralTabBtn.IsChecked = false;
        IntegrationsTabBtn.IsChecked = true;
    }

    private void RepositoryLink_RequestNavigate(object sender, RequestNavigateEventArgs e)
    {
        Process.Start(new ProcessStartInfo(e.Uri.AbsoluteUri) { UseShellExecute = true });
        e.Handled = true;
    }
}
