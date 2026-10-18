using System.Windows;
using System.Windows.Media;
using YorteImpact.Services;

namespace YorteImpact;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // Open the editor straight onto the configured default canvas
        // (2000 x 2000 unless changed in Settings). The New Canvas dialog
        // is only shown when the user explicitly picks File > New.
        var settings = AppSettings.Load();
        var background = settings.DefaultCanvasTransparent ? Colors.Transparent : Colors.White;

        var window = new MainWindow(
            settings.DefaultCanvasWidth,
            settings.DefaultCanvasHeight,
            background,
            settings.DefaultCanvasTransparent);

        MainWindow = window;
        ShutdownMode = ShutdownMode.OnMainWindowClose;
        window.Show();
    }
}
