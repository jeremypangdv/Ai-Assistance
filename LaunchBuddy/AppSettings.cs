using System.Text.Json;

namespace LaunchBuddy;

internal sealed class AppSettings
{
    private static readonly string FilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "LaunchBuddy", "settings.json");

    public bool VoiceEnabled { get; set; }
    public bool PushToTalkEnabled { get; set; } = true;
    // Windows virtual-key code; 0xA2 is Left Ctrl.
    public int PushToTalkKey { get; set; } = 0xA2;
    // Shortcut that pauses/resumes push-to-talk, stored as WinForms Keys (key code plus modifier flags).
    public bool QuickToggleEnabled { get; set; } = true;
    public int QuickToggleKeys { get; set; } = (int)(Keys.Control | Keys.Alt | Keys.M);

    public static AppSettings Load()
    {
        try
        {
            return File.Exists(FilePath)
                ? JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath)) ?? new AppSettings()
                : new AppSettings();
        }
        catch (Exception exception)
        {
            AppLog.Error("Settings", exception);
            return new AppSettings();
        }
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception exception)
        {
            AppLog.Error("Settings", exception);
        }
    }
}
