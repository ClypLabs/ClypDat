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
            if (string.IsNullOrWhiteSpace(settings.ClipFileNameScheme)) settings.ClipFileNameScheme = "Standard";
            if (string.IsNullOrWhiteSpace(settings.CustomClipFileNameTemplate)) settings.CustomClipFileNameTemplate = "{datetime:yyyy-MM-dd HH-mm-ss} - {title}";
            if (string.IsNullOrWhiteSpace(settings.ReplayQualityPreset)) settings.ReplayQualityPreset = "Balanced";
            if (settings.ReplayFrameRate <= 0) settings.ReplayFrameRate = 60;
            if (settings.ReplayMaxHeight <= 0) settings.ReplayMaxHeight = 1080;
            if (string.IsNullOrWhiteSpace(settings.ExportVideoCodec)) settings.ExportVideoCodec = "H.264";
            settings.ChatAudioProcessName ??= string.Empty;
            settings.ChatAudioProcessNames ??= new List<string>();
            settings.MicrophoneDeviceIds ??= new List<string>();
            settings.IgnoredGameExecutables ??= new List<string>();
            settings.AutoClipping ??= new AutoClippingSettings();
            settings.AutoClipping.Games ??= new Dictionary<string, AutoClipGameSettings>(StringComparer.OrdinalIgnoreCase);
            foreach (var game in settings.AutoClipping.Games.Values)
            {
                game.Events ??= new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
            }
            MigrateCs2AutoClip(settings);
            return settings;
        }
        catch
        {
            return new AppSettings();
        }
    }

    private static void MigrateCs2AutoClip(AppSettings settings)
    {
        const string gameId = "cs2";
        if (settings.AutoClipping.Games.ContainsKey(gameId)) return;

        var legacy = settings.Cs2AutoClip ?? new Cs2AutoClipSettings();
        settings.AutoClipping.Games[gameId] = new AutoClipGameSettings
        {
            Enabled = legacy.Enabled,
            ListenerPort = legacy.GsiPort,
            Events = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase)
            {
                ["kill"] = legacy.Kill,
                ["2k"] = legacy.TwoKill,
                ["3k"] = legacy.ThreeKill,
                ["4k"] = legacy.FourKill,
                ["ace"] = legacy.Ace,
                ["headshot"] = legacy.Headshot,
                ["death"] = legacy.Death,
                ["assist"] = legacy.Assist
            }
        };
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
