using System.Text.Json;

namespace ClypDat.Core.Settings;

public static class AppSettingsStore
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public static string SettingsPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "ClypDat",
        "settings.json");

    public static AppSettings Load()
    {
        try
        {
            if (!File.Exists(SettingsPath)) return new AppSettings
            {
                HasSeenOnboarding = false,
                ReplayBitrateDefault15Applied = true
            };
            var json = File.ReadAllText(SettingsPath);
            var settings = JsonSerializer.Deserialize<AppSettings>(json) ?? new AppSettings();
            MigrateReplayBitrate(json, settings);
            if (!settings.ReplayBitrateDefault15Applied)
            {
                if (settings.ReplayBitrateMbps == 40) settings.ReplayBitrateMbps = 15;
                settings.ReplayBitrateDefault15Applied = true;
                Save(settings);
            }
            settings.ClipEdits ??= new Dictionary<string, ClipEditSettings>(StringComparer.OrdinalIgnoreCase);
            settings.GameAudioExcludedProcesses ??= new List<string>();
            if (string.IsNullOrWhiteSpace(settings.ClipFileNameScheme)) settings.ClipFileNameScheme = "Standard";
            if (string.IsNullOrWhiteSpace(settings.CustomClipFileNameTemplate)) settings.CustomClipFileNameTemplate = "{datetime:yyyy-MM-dd HH-mm-ss} - {title}";
            if (string.IsNullOrWhiteSpace(settings.ReplayEncoderPreset)) settings.ReplayEncoderPreset = "P4";
            if (!string.Equals(settings.ReplayVideoCodec, "H.264", StringComparison.OrdinalIgnoreCase)) settings.ReplayVideoCodec = "Auto";
            if (!string.Equals(settings.ReplayCaptureSource, "Desktop", StringComparison.OrdinalIgnoreCase)) settings.ReplayCaptureSource = "Game";
            settings.ReplayDesktopMonitorDeviceName ??= string.Empty;
            // Clamped on both ends, not just guarded against <= 0 - a
            // hand-edited or pre-validation settings.json holding an
            // out-of-range value (e.g. 52 before the UI's own 1-51 clamp
            // existed) used to load as-is and show in the box indefinitely
            // while the encoder silently re-clamped it underneath, reading as
            // though the limit wasn't enforced at all.
            settings.ReplayBitrateMbps = Math.Clamp(settings.ReplayBitrateMbps <= 0 ? 15 : settings.ReplayBitrateMbps, 5, 1000);
            if (settings.ReplayFrameRate <= 0) settings.ReplayFrameRate = 60;
            // One-time switch-on: the floating hover bar is the default now,
            // but existing settings.json files already carry an explicit false
            // for it, so the changed property default alone would only reach
            // new installs. Same guard shape as the reset above - runs once,
            // then leaves the setting alone for good.
            if (!settings.HoverBarDefaultOnApplied)
            {
                settings.EditorHoverBarEnabled = true;
                settings.HoverBarDefaultOnApplied = true;
            }
            if (settings.ReplayMaxHeight <= 0) settings.ReplayMaxHeight = 1080;
            if (string.IsNullOrWhiteSpace(settings.ExportVideoCodec)) settings.ExportVideoCodec = "H.264";
            settings.ProcessPriority = settings.ProcessPriority switch
            {
                "Idle" or "BelowNormal" or "Normal" or "AboveNormal" or "High" => settings.ProcessPriority,
                _ => "Normal"
            };
            settings.ChatAudioProcessName ??= string.Empty;
            settings.ChatAudioProcessNames ??= new List<string>();
            settings.MicrophoneDeviceIds ??= new List<string>();
            settings.IgnoredGameExecutables ??= new List<string>();
            settings.GameCaptureOverrides ??= new List<GameCaptureOverride>();
            foreach (var game in settings.GameCaptureOverrides)
            {
                // Older settings had no origin. A display name meant user
                // intentionally added process, while empty names only stored
                // a backend choice.
                if (game.Origin is null) game.Origin = string.IsNullOrWhiteSpace(game.DisplayName) ? "Backend" : "UserCustom";
            }
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

    private static void MigrateReplayBitrate(string json, AppSettings settings)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            if (root.TryGetProperty("ReplayBitrateMbps", out var current) && current.TryGetInt32(out var currentValue))
            {
                settings.ReplayBitrateMbps = currentValue;
                return;
            }

            // Existing explicit CBR users retain their saved target. CQ and
            // missing/unknown modes get fixed-CBR default 15 Mbps.
            var mode = root.TryGetProperty("ReplayRateControlMode", out var modeValue)
                ? modeValue.GetString()
                : null;
            if (string.Equals(mode, "Constant bitrate", StringComparison.OrdinalIgnoreCase)
                && root.TryGetProperty("ReplayMaxBitrateMbps", out var oldBitrate)
                && oldBitrate.TryGetInt32(out var oldValue))
            {
                settings.ReplayBitrateMbps = oldValue;
            }
            else
            {
                settings.ReplayBitrateMbps = 15;
            }
        }
        catch
        {
            settings.ReplayBitrateMbps = 15;
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
