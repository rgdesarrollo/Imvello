using System.IO;
using System.Text.Json;

namespace Imvello.Models;

public class AppSettings
{
    public static string SettingsPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "LetMail", "settings.json");

    public string DataDirectory { get; set; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "LetMail");
    public string? GoogleClientId { get; set; }
    public string? GoogleClientSecret { get; set; }
    public string? MicrosoftClientId { get; set; }
    public int SyncIntervalMinutes { get; set; } = 5;
    public bool DownloadFullMessages { get; set; }
    public bool BlockRemoteImages { get; set; } = true;
    public string ReadingPanePosition { get; set; } = "Right";
    public bool ShowPreview { get; set; } = true;
    public bool ShowFolderPane { get; set; } = true;
    public bool MarkAsReadWhenViewed { get; set; } = true;
    public bool ConfirmDelete { get; set; }
    public double WindowWidth { get; set; } = 1400;
    public double WindowHeight { get; set; } = 850;
    public bool WindowMaximized { get; set; }
    public string Language { get; set; } = "System";

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(SettingsPath))
                return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(SettingsPath)) ?? new AppSettings();
        }
        catch
        {
        }
        return new AppSettings();
    }

    public void Save()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(SettingsPath)!);
        File.WriteAllText(SettingsPath, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
    }
}
