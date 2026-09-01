using System.Text.Json;

namespace ClypDat.Core.Settings;

public static class AppSettingsStore
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public static string SettingsPath => Path.Combine(AppDataPaths.Root, "settings.json");

    /// <summary>
    /// Set when settings.json existed but could not be read or parsed - as opposed to
    /// simply not existing yet. Callers can surface this; a silent reset looks identical
    /// to a fresh install from the UI, while actually having discarded the library
    /// folder, hotkeys, quotas and the GSI auth token.
    /// </summary>
    public static string? LastLoadError { get; private set; }

    /// <summary>Path the previous settings file was preserved to when it could not be read.</summary>
    public static string? PreservedUnreadablePath { get; private set; }
    public static string? LastSaveError { get; private set; }

    public static AppSettings Load()
    {
        try
        {
            if (!File.Exists(SettingsPath)) return new AppSettings
            {
                HasSeenOnboarding = false,
                ReplayBitrateDefault15Applied = true,
                ReplayH264DefaultApplied = true,
                SettingsSchemaVersion = AppSettingsMigrations.CurrentSchemaVersion
            };
            var json = File.ReadAllText(SettingsPath);
            var settings = JsonSerializer.Deserialize<AppSettings>(json) ?? new AppSettings();
            var migrated = AppSettingsMigrations.Apply(settings);
            var hasExplicitReplayBitrate = MigrateReplayBitrate(json, settings);
            if (!settings.ReplayBitrateDefault15Applied)
            {
                // An explicit bitrate is a user choice, including the old 40
                // Mbps value. Only replace the former implicit default.
                if (!hasExplicitReplayBitrate && settings.ReplayBitrateMbps == 40) settings.ReplayBitrateMbps = 15;
                settings.ReplayBitrateDefault15Applied = true;
                Save(settings);
            }
            settings.ClipEdits ??= new Dictionary<string, ClipEditSettings>(StringComparer.OrdinalIgnoreCase);
            settings.GameRailFolders ??= new List<GameRailFolder>();
            settings.GameRailOrder ??= new List<string>();
            settings.GameAudioExcludedProcesses ??= new List<string>();
            if (string.IsNullOrWhiteSpace(settings.FontFamilyName)) settings.FontFamilyName = "Inter";
            if (string.IsNullOrWhiteSpace(settings.ClipFileNameScheme)) settings.ClipFileNameScheme = "Standard";
            if (string.IsNullOrWhiteSpace(settings.CustomClipFileNameTemplate)) settings.CustomClipFileNameTemplate = "{datetime:yyyy-MM-dd HH-mm-ss} - {title}";
            if (!settings.ReplayH264DefaultApplied)
            {
                if (string.Equals(settings.ReplayVideoCodec, "Auto", StringComparison.OrdinalIgnoreCase))
                    settings.ReplayVideoCodec = "H.264";
                settings.ReplayH264DefaultApplied = true;
                Save(settings);
            }
            settings.ReplayVideoCodec = string.Equals(settings.ReplayVideoCodec, "AV1", StringComparison.OrdinalIgnoreCase)
                ? "AV1"
                : "H.264";
            settings.ReplayEncoderMode = string.Equals(settings.ReplayEncoderMode, "CPU", StringComparison.OrdinalIgnoreCase)
                ? "CPU"
                : "GPU";
            if (settings.ReplayEncoderMode == "CPU") settings.ReplayVideoCodec = "H.264";
            if (!string.Equals(settings.ReplayCaptureSource, "Desktop", StringComparison.OrdinalIgnoreCase)) settings.ReplayCaptureSource = "Game";
            settings.ReplayDesktopMonitorDeviceName ??= string.Empty;
            // Clamped on both ends, not just guarded against <= 0 - a
            // hand-edited or pre-validation settings.json holding an
            // out-of-range value (e.g. 52 before the UI's own 1-51 clamp
            // existed) used to load as-is and show in the box indefinitely
            // while the encoder silently re-clamped it underneath, reading as
            // though the limit wasn't enforced at all.
            settings.ReplayBitrateMbps = Math.Clamp(settings.ReplayBitrateMbps <= 0 ? 15 : settings.ReplayBitrateMbps, 5, 100);
            var normalizedReplayFrameRate = ReplayFrameRatePolicy.NormalizePersisted(settings.ReplayFrameRate);
            var replayFrameRateChanged = settings.ReplayFrameRate != normalizedReplayFrameRate;
            settings.ReplayFrameRate = normalizedReplayFrameRate;
            settings.ReplayFrameRateMode = string.Equals(settings.ReplayFrameRateMode, "CFR", StringComparison.OrdinalIgnoreCase)
                ? "CFR"
                : "VFR";
            if (settings.ReplayMaxHeight <= 0) settings.ReplayMaxHeight = 1080;
            if (string.IsNullOrWhiteSpace(settings.ExportVideoCodec)) settings.ExportVideoCodec = "H.264";
            settings.ProcessPriority = settings.ProcessPriority switch
            {
                "Idle" or "BelowNormal" or "Normal" or "AboveNormal" or "High" => settings.ProcessPriority,
                _ => "Normal"
            };
            settings.ChatAudioProcessName ??= string.Empty;
            settings.ChatAudioProcessNames ??= new List<string>();
            settings.AdditionalAudioProcesses ??= new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            settings.GameAudioVolumePercent = Math.Clamp(settings.GameAudioVolumePercent, 0, 150);
            settings.MicrophoneVolumePercent = Math.Clamp(settings.MicrophoneVolumePercent, 0, 150);
            settings.MicrophoneChannelMode = string.Equals(settings.MicrophoneChannelMode, "Stereo", StringComparison.OrdinalIgnoreCase)
                ? "Stereo"
                : "Mono";
            settings.MicrophoneNoiseGateThresholdDb = double.IsFinite(settings.MicrophoneNoiseGateThresholdDb)
                ? Math.Clamp(settings.MicrophoneNoiseGateThresholdDb, -100, -25)
                : -100;
            settings.CustomGameSettings ??= new Dictionary<string, CustomGameProfile>(StringComparer.OrdinalIgnoreCase);
            // A hand-edited or partially-written profile must not be able to
            // push an out-of-range value into a recording - these are the same
            // bounds the sliders enforce.
            foreach (var profile in settings.CustomGameSettings.Values)
            {
                if (profile is null) continue;
                profile.Groups ??= new List<string>();
                profile.GameCaptureMethod = string.Equals(profile.GameCaptureMethod, "Hook", StringComparison.OrdinalIgnoreCase)
                    ? "Hook"
                    : "Display";
                profile.AdditionalAudioProcesses ??= new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
                profile.ReplayBitrateMbps = Math.Clamp(profile.ReplayBitrateMbps, 1, 200);
                profile.ReplayFrameRate = Math.Clamp(profile.ReplayFrameRate, 10, 480);
                profile.ReplayMaxHeight = Math.Clamp(profile.ReplayMaxHeight, 360, 4320);
                profile.ReplayDurationSeconds = Math.Clamp(profile.ReplayDurationSeconds, 5, 7200);
                profile.GameAudioVolumePercent = Math.Clamp(profile.GameAudioVolumePercent, 0, 150);
                profile.MicrophoneVolumePercent = Math.Clamp(profile.MicrophoneVolumePercent, 0, 150);
                profile.FullSessionQuotaGb = Math.Max(0, profile.FullSessionQuotaGb);
                profile.MicrophoneNoiseGateThresholdDb = double.IsFinite(profile.MicrophoneNoiseGateThresholdDb)
                    ? Math.Clamp(profile.MicrophoneNoiseGateThresholdDb, -100, -25)
                    : -100;
                if (string.IsNullOrWhiteSpace(profile.ReplayVideoCodec)) profile.ReplayVideoCodec = "H.264";
                if (string.IsNullOrWhiteSpace(profile.ReplayEncoderMode)) profile.ReplayEncoderMode = "GPU";
                if (string.IsNullOrWhiteSpace(profile.FullSessionVideoCodec)) profile.FullSessionVideoCodec = "H.264";
                if (string.IsNullOrWhiteSpace(profile.SaveReplayHotkey)) profile.SaveReplayHotkey = settings.SaveReplayHotkey;
            }
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
            if (migrated || replayFrameRateChanged) Save(settings);
            return settings;
        }
        catch (Exception error)
        {
            // A file that exists but will not parse is NOT the same as no file at all.
            // Returning defaults here and letting the next Save() write over the
            // original destroyed the user's settings permanently, with nothing shown.
            // Preserve whatever is there before anything overwrites it, and record why.
            LastLoadError = error.Message;
            PreservedUnreadablePath = TryPreserveUnreadableSettings();
            foreach (var candidate in RecoveryCandidates())
            {
                try
                {
                    if (!File.Exists(candidate)) continue;
                    var recovered = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(candidate));
                    if (recovered is null) continue;
                    File.Copy(candidate, SettingsPath, overwrite: true);
                    return recovered;
                }
                catch
                {
                    // Try next preserved snapshot.
                }
            }
            return new AppSettings();
        }
    }

    private static IEnumerable<string> RecoveryCandidates()
    {
        yield return SettingsPath + ".backup.json";
        var folder = Path.GetDirectoryName(SettingsPath);
        if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder)) yield break;
        foreach (var path in Directory.EnumerateFiles(folder, "settings.unreadable-*.json")
                     .OrderByDescending(File.GetLastWriteTimeUtc)) yield return path;
    }

    // Moves an unreadable settings.json aside so the next Save cannot destroy it.
    // Best-effort: if this fails there is nothing more useful to do than continue with
    // defaults, which is what the caller does anyway.
    private static string? TryPreserveUnreadableSettings()
    {
        try
        {
            if (!File.Exists(SettingsPath)) return null;
            var stamp = DateTime.UtcNow.ToString("yyyyMMdd-HHmmss");
            var preserved = Path.Combine(
                Path.GetDirectoryName(SettingsPath) ?? AppDataPaths.Root,
                $"settings.unreadable-{stamp}.json");
            File.Copy(SettingsPath, preserved, overwrite: false);
            return preserved;
        }
        catch
        {
            return null;
        }
    }

    private static bool MigrateReplayBitrate(string json, AppSettings settings)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            if (root.TryGetProperty("ReplayBitrateMbps", out var current) && current.TryGetInt32(out var currentValue))
            {
                settings.ReplayBitrateMbps = currentValue;
                return true;
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
                return true;
            }
            else
            {
                settings.ReplayBitrateMbps = 15;
                return false;
            }
        }
        catch
        {
            settings.ReplayBitrateMbps = 15;
            return false;
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

    public static bool Save(AppSettings settings)
    {
        try
        {
            var folder = Path.GetDirectoryName(SettingsPath);
            if (!string.IsNullOrWhiteSpace(folder)) Directory.CreateDirectory(folder);

            // Write-then-rename rather than writing in place. A crash or power loss
            // during a direct WriteAllText leaves settings.json truncated, which fails
            // to parse on next launch and - before the catch above was fixed - silently
            // reset every setting. Same shape MedalImportHistoryStore already uses.
            var temporaryPath = SettingsPath + ".tmp";
            File.WriteAllText(temporaryPath, JsonSerializer.Serialize(settings, JsonOptions));
            if (File.Exists(SettingsPath)) File.Copy(SettingsPath, SettingsPath + ".backup.json", overwrite: true);
            File.Move(temporaryPath, SettingsPath, overwrite: true);
            LastSaveError = null;
            return true;
        }
        catch (Exception error)
        {
            // Settings persistence should not block the editor.
            LastSaveError = error.Message;
            return false;
        }
    }
}
