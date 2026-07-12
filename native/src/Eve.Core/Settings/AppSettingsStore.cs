using System.Text.Json;

namespace Eve.Core.Settings;

public static class AppSettingsStore
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public static string SettingsPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "EVE",
        "settings.json");

    public static AppSettings Load()
    {
        try
        {
            if (!File.Exists(SettingsPath)) return new AppSettings { HasSeenOnboarding = false };
            var json = File.ReadAllText(SettingsPath);
            var settings = JsonSerializer.Deserialize<AppSettings>(json) ?? new AppSettings();
            settings.ClipEdits ??= new Dictionary<string, ClipEditSettings>(StringComparer.OrdinalIgnoreCase);
            settings.GameAudioExcludedProcesses ??= new List<string>();
            if (string.IsNullOrWhiteSpace(settings.ReplayQualityPreset)) settings.ReplayQualityPreset = "Balanced";
            if (settings.ReplayFrameRate <= 0) settings.ReplayFrameRate = 60;
            if (settings.ReplayMaxHeight <= 0) settings.ReplayMaxHeight = 1080;
            if (string.IsNullOrWhiteSpace(settings.ExportVideoCodec)) settings.ExportVideoCodec = "H.264";
            settings.ChatAudioProcessName ??= string.Empty;
            settings.ChatAudioProcessNames ??= new List<string>();
            settings.MicrophoneDeviceIds ??= new List<string>();
            // Migrate a pre-multi-select single choice into the new list, once -
            // only when the list is empty so it doesn't re-add something the user
            // has since removed.
            if (settings.ChatAudioProcessNames.Count == 0 && !string.IsNullOrWhiteSpace(settings.ChatAudioProcessName))
            {
                settings.ChatAudioProcessNames.Add(settings.ChatAudioProcessName);
            }

            if (settings.MicrophoneDeviceIds.Count == 0 && !string.IsNullOrWhiteSpace(settings.MicrophoneDeviceId) && settings.MicrophoneDeviceId != "default")
            {
                settings.MicrophoneDeviceIds.Add(settings.MicrophoneDeviceId);
            }

            return settings;
        }
        catch
        {
            return new AppSettings();
        }
    }

    public static void Save(AppSettings settings)
    {
        try
        {
            var folder = Path.GetDirectoryName(SettingsPath);
            if (!string.IsNullOrWhiteSpace(folder)) Directory.CreateDirectory(folder);
            File.WriteAllText(SettingsPath, JsonSerializer.Serialize(settings, JsonOptions));
        }
        catch
        {
            // Settings persistence should not block the editor.
        }
    }
}
