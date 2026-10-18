using System.IO;
using System.Text.Json;

namespace MyIdm.Desktop.Services;

public sealed class AppSettings
{
    public string DownloadDirectory { get; set; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        "Downloads",
        "NovaGet");

    public int DefaultConnections { get; set; } = 8;
    public string DefaultQuality { get; set; } = "best";
    public bool MinimizeToTrayOnClose { get; set; } = true;
    public bool ShowCompletionNotifications { get; set; } = true;
    public bool AutoStartBrowserDownloads { get; set; } = true;
    public bool ShowSegmentDetailsPanel { get; set; } = false;
}

public static class AppSettingsStore
{
    private static readonly string SettingsFilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "MyIDM",
        "settings.json");

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true
    };

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(SettingsFilePath))
            {
                string json = File.ReadAllText(SettingsFilePath);
                var loaded = JsonSerializer.Deserialize<AppSettings>(json);
                if (loaded is not null)
                {
                    if (string.IsNullOrWhiteSpace(loaded.DownloadDirectory))
                    {
                        loaded.DownloadDirectory = Path.Combine(
                            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                            "Downloads",
                            "NovaGet");
                    }
                    Directory.CreateDirectory(loaded.DownloadDirectory);
                    return loaded;
                }
            }
        }
        catch { }

        var defaults = new AppSettings();
        try { Directory.CreateDirectory(defaults.DownloadDirectory); } catch { }
        return defaults;
    }

    public static void Save(AppSettings settings)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(SettingsFilePath)!);
            if (!string.IsNullOrWhiteSpace(settings.DownloadDirectory))
            {
                Directory.CreateDirectory(settings.DownloadDirectory);
            }
            string json = JsonSerializer.Serialize(settings, JsonOpts);
            File.WriteAllText(SettingsFilePath, json);
        }
        catch { }
    }
}
