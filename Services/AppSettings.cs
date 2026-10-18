using System;
using System.IO;
using System.Text.Json;

namespace YorteImpact.Services;

/// <summary>
/// Persisted user preferences. Stored as JSON under
/// <c>%AppData%\YorteImpact\settings.json</c>; a missing or corrupt file
/// falls back to the defaults (2000 x 2000 white canvas).
/// </summary>
public sealed class AppSettings
{
    public int DefaultCanvasWidth { get; set; } = 2000;
    public int DefaultCanvasHeight { get; set; } = 2000;
    public bool DefaultCanvasTransparent { get; set; }

    private static string FilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "YorteImpact", "settings.json");

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(FilePath))
                return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath))
                       ?? new AppSettings();
        }
        catch
        {
            // Corrupt or unreadable: use defaults rather than fail to start.
        }
        return new AppSettings();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.WriteAllText(FilePath,
                JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch
        {
            // Settings are best-effort; never crash the editor over them.
        }
    }
}
