namespace ClypDat.Core.Settings;

public static class AppSettingsMigrations
{
    public const int CurrentSchemaVersion = 9;

    public static bool Apply(AppSettings settings)
    {
        if (settings.SettingsSchemaVersion >= CurrentSchemaVersion) return false;

        if (settings.SettingsSchemaVersion < 2)
        {
            // Recording Audio supersedes the old Chat Audio picker. Preserve
            // every legacy selection as an independent 100% app track.
            settings.AdditionalAudioProcesses ??= new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            foreach (var name in (settings.ChatAudioProcessNames ?? []).Append(settings.ChatAudioProcessName).Where(name => !string.IsNullOrWhiteSpace(name)))
            {
                if (!settings.AdditionalAudioProcesses.ContainsKey(name)) settings.AdditionalAudioProcesses[name] = 100;
            }
            settings.ChatAudioProcessName = string.Empty;
            settings.ChatAudioProcessNames?.Clear();
            settings.MultiChatAppEnabled = false;
        }

        if (settings.SettingsSchemaVersion < 3)
        {
            settings.AdditionalAudioProcesses = AudioProcessIdentity.NormalizeDictionary(settings.AdditionalAudioProcesses);
            settings.ChatAudioProcessName = AudioProcessIdentity.Normalize(settings.ChatAudioProcessName);
            settings.ChatAudioProcessNames = AudioProcessIdentity.NormalizeList(settings.ChatAudioProcessNames);
            settings.GameAudioExcludedProcesses = AudioProcessIdentity.NormalizeList(settings.GameAudioExcludedProcesses);
        }

        if (settings.SettingsSchemaVersion < 4)
        {
            // DXGI is now the only capture path. Keep legacy fields readable,
            // but never let an old selection revive a retired implementation.
            settings.ReplayBackend = "Native";
            foreach (var game in settings.GameCaptureOverrides ?? []) game.CaptureBackend = "Native";
        }

        if (settings.SettingsSchemaVersion < 5)
        {
            if (settings.LastSettingsSection is "Themes" or "Fonts") settings.LastSettingsSection = "Appearance";
            settings.CustomThemes ??= new();
            settings.RecentThemeColors ??= new();
        }

        if (settings.SettingsSchemaVersion < 6)
        {
            // Profile hotkeys were stored while their UI was absent. Make an
            // upgrade deterministic: enabling Replay later keeps today's
            // global key instead of reviving an old invisible override.
            if (settings.CustomGameSettings is not null)
                foreach (var profile in settings.CustomGameSettings.Values)
                    profile.SaveReplayHotkey = settings.SaveReplayHotkey;
        }

        if (settings.SettingsSchemaVersion < 7 && string.IsNullOrWhiteSpace(settings.MicrophoneDeviceId))
        {
            settings.MicrophoneDeviceId = "default";
        }

        if (settings.SettingsSchemaVersion < 8)
        {
            // New source is deliberately opt-in. Existing recordings stay
            // byte-for-byte unchanged until a user enables it.
            settings.VideoOverlays ??= new VideoOverlaySettings();
            settings.VideoOverlays.Enabled = false;
        }

        if (settings.SettingsSchemaVersion < 9)
        {
            settings.VideoOverlays ??= new VideoOverlaySettings();
            // Existing normalized transforms remain authoritative.  Anchors
            // only restore which corner picker owns each legacy source.
            settings.VideoOverlays.CameraAnchor ??= "Top Right";
            settings.VideoOverlays.KeyboardAnchor ??= "Bottom Left";
        }

        settings.CustomThemes ??= new();
        settings.RecentThemeColors ??= new();
        settings.CustomThemes.RemoveAll(theme => string.IsNullOrWhiteSpace(theme.Id) ||
            !CustomThemeLibrary.IsColor(theme.BaseColor) || !CustomThemeLibrary.IsColor(theme.AccentColor) ||
            !CustomThemeLibrary.TryNormalizeName(theme.Name, settings.CustomThemes, theme.Id, out _, out _));
        settings.RecentThemeColors = settings.RecentThemeColors.Where(CustomThemeLibrary.IsColor)
            .Select(color => color.ToUpperInvariant()).Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(CustomThemeLibrary.RecentColorLimit).ToList();
        settings.VideoOverlays ??= new VideoOverlaySettings();
        settings.VideoOverlays.KeyboardLayout = settings.VideoOverlays.KeyboardLayout is "None" or "QWERTY Compact" or "QWERTY Full" or "Arrows" or "AZERTY Compact"
            ? settings.VideoOverlays.KeyboardLayout : "None";
        settings.VideoOverlays.CameraTransform = VideoOverlayLayout.Normalize(settings.VideoOverlays.CameraTransform, VideoOverlayLayout.CameraAspectRatio);
        settings.VideoOverlays.KeyboardTransform = VideoOverlayLayout.Normalize(settings.VideoOverlays.KeyboardTransform, 2.4);

        settings.SettingsSchemaVersion = CurrentSchemaVersion;
        return true;
    }
}
