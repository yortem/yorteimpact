using System.Windows;
using System.Windows.Navigation;
using System.Diagnostics;
using YorteImpact.Services;
using YorteImpact.Services.Ai;

namespace YorteImpact;

/// <summary>
/// Edits the persisted startup defaults (default canvas size + background),
/// integrations and the AI assistant connection.
/// </summary>
public partial class SettingsDialog : Window
{
    private AiProviderKind _aiProvider;
    private bool _aiTesting;

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

        var ai = settings.Ai ?? new AiProviderSettings();
        ai.Normalize();
        _aiProvider = ai.Provider;
        AiBaseUrlBox.Text = ai.BaseUrl;
        AiModelBox.Text = ai.Model;
        AiKeyBox.Password = ai.ApiKey;
        AiTwoPassBtn.IsChecked = ai.TwoPass;
        RefreshProviderButtons();

        if (showIntegrations) IntegrationsTab_Click(this, new RoutedEventArgs());
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        if (!int.TryParse(WidthBox.Text, out int w) || !int.TryParse(HeightBox.Text, out int h)
            || w < 1 || h < 1 || w > 8000 || h > 8000)
        {
            ShowError("Enter whole pixels, 1 – 8000 per side.");
            return;
        }

        var ai = BuildAiSettings();
        string baseUrl = ai.EffectiveBaseUrl;
        if (baseUrl.Length > 0
            && (!Uri.TryCreate(baseUrl, UriKind.Absolute, out Uri? endpoint)
                || (endpoint.Scheme != Uri.UriSchemeHttps && endpoint.Scheme != Uri.UriSchemeHttp)))
        {
            ShowError("Enter a complete http or https base URL.");
            return;
        }
        if (string.IsNullOrWhiteSpace(ai.Model))
        {
            ShowError("Enter a model name.");
            return;
        }

        Settings.DefaultCanvasWidth = w;
        Settings.DefaultCanvasHeight = h;
        Settings.DefaultCanvasTransparent = BgTransparent.IsChecked == true;
        Settings.PixabayApiKey = PixabayKeyBox.Text.Trim();
        Settings.Ai = ai;
        Settings.Save();
        DialogResult = true;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;

    private void GeneralTab_Click(object sender, RoutedEventArgs e)
    {
        GeneralTab.Visibility = Visibility.Visible;
        IntegrationsTab.Visibility = Visibility.Collapsed;
        AiTab.Visibility = Visibility.Collapsed;
        GeneralTabBtn.IsChecked = true;
        IntegrationsTabBtn.IsChecked = false;
        AiTabBtn.IsChecked = false;
    }

    private void IntegrationsTab_Click(object sender, RoutedEventArgs e)
    {
        GeneralTab.Visibility = Visibility.Collapsed;
        IntegrationsTab.Visibility = Visibility.Visible;
        AiTab.Visibility = Visibility.Collapsed;
        GeneralTabBtn.IsChecked = false;
        IntegrationsTabBtn.IsChecked = true;
        AiTabBtn.IsChecked = false;
    }

    private void AiTab_Click(object sender, RoutedEventArgs e)
    {
        GeneralTab.Visibility = Visibility.Collapsed;
        IntegrationsTab.Visibility = Visibility.Collapsed;
        AiTab.Visibility = Visibility.Visible;
        GeneralTabBtn.IsChecked = false;
        IntegrationsTabBtn.IsChecked = false;
        AiTabBtn.IsChecked = true;
    }

    private void AiProvider_Click(object sender, RoutedEventArgs e)
    {
        var provider = sender == AiOpenAiBtn ? AiProviderKind.OpenAiCompatible : AiProviderKind.Gemini;
        if (_aiProvider == provider)
        {
            RefreshProviderButtons();
            return;
        }

        var previousDefaults = AiProviderSettings.CreateDefault(_aiProvider);
        string baseUrl = AiBaseUrlBox.Text.Trim();
        string model = AiModelBox.Text.Trim();
        if (baseUrl.Length == 0 || baseUrl.TrimEnd('/') == previousDefaults.EffectiveBaseUrl)
            AiBaseUrlBox.Text = AiProviderSettings.CreateDefault(provider).BaseUrl;
        if (model.Length == 0 || model == previousDefaults.Model)
            AiModelBox.Text = AiProviderSettings.CreateDefault(provider).Model;

        _aiProvider = provider;
        RefreshProviderButtons();
        AiTestText.Text = "";
    }

    private async void AiTest_Click(object sender, RoutedEventArgs e)
    {
        if (_aiTesting) return;

        var ai = BuildAiSettings();
        if (string.IsNullOrWhiteSpace(ai.ApiKey))
        {
            AiTestText.Foreground = System.Windows.Media.Brushes.OrangeRed;
            AiTestText.Text = "Enter an API key first.";
            return;
        }

        _aiTesting = true;
        AiTestBtn.IsEnabled = false;
        AiTestText.Foreground = FindResource("Muted") as System.Windows.Media.Brush;
        AiTestText.Text = "Testing… (up to 30 seconds)";

        try
        {
            var (ok, message) = await new AiClient().TestAsync(ai);
            AiTestText.Foreground = ok
                ? System.Windows.Media.Brushes.LightGreen
                : System.Windows.Media.Brushes.OrangeRed;
            AiTestText.Text = message;
        }
        catch (Exception ex)
        {
            AiTestText.Foreground = System.Windows.Media.Brushes.OrangeRed;
            AiTestText.Text = ex.Message;
        }
        finally
        {
            _aiTesting = false;
            AiTestBtn.IsEnabled = true;
        }
    }

    private AiProviderSettings BuildAiSettings()
    {
        var current = Settings.Ai ?? new AiProviderSettings();
        var ai = new AiProviderSettings
        {
            Provider = _aiProvider,
            BaseUrl = AiBaseUrlBox.Text.Trim(),
            Model = AiModelBox.Text.Trim(),
            ApiKey = AiKeyBox.Password,
            Temperature = current.Temperature,
            MaxTokens = current.MaxTokens,
            TwoPass = AiTwoPassBtn.IsChecked == true,
        };
        ai.Normalize();
        return ai;
    }

    private void RefreshProviderButtons()
    {
        AiGeminiBtn.IsChecked = _aiProvider == AiProviderKind.Gemini;
        AiOpenAiBtn.IsChecked = _aiProvider == AiProviderKind.OpenAiCompatible;
    }

    private void ShowError(string message)
    {
        ErrorText.Text = message;
        ErrorText.Visibility = Visibility.Visible;
    }

    private void RepositoryLink_RequestNavigate(object sender, RequestNavigateEventArgs e)
    {
        Process.Start(new ProcessStartInfo(e.Uri.AbsoluteUri) { UseShellExecute = true });
        e.Handled = true;
    }
}
