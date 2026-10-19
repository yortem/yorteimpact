using System.Windows;
using System.Windows.Media;
using YorteImpact.Services;

namespace YorteImpact;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // Start on the configured default canvas, unless the shell passed a
        // project file (Open with / command line) to open as the first tab.
        var settings = AppSettings.Load();
        var background = settings.DefaultCanvasTransparent ? Colors.Transparent : Colors.White;

        string? startupProject = e.Args.FirstOrDefault(a =>
            !string.IsNullOrWhiteSpace(a) &&
            string.Equals(System.IO.Path.GetExtension(a), ".yimpact", StringComparison.OrdinalIgnoreCase) &&
            System.IO.File.Exists(a));

        var window = new MainWindow(
            settings.DefaultCanvasWidth,
            settings.DefaultCanvasHeight,
            background,
            settings.DefaultCanvasTransparent,
            startupProject);

        MainWindow = window;
        ShutdownMode = ShutdownMode.OnMainWindowClose;
        window.Show();
    }
}
