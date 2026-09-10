namespace ClypDat.Core.Settings;

public static class AppSettingsMigrations
{
    public const int CurrentSchemaVersion = 11;

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

        if (settings.SettingsSchemaVersion < 10)
        {
            // Enabled used to mean "burn into gameplay". Source selection and
            // layout remain meaningful, so never clear either on upgrade.
            settings.VideoOverlays ??= new VideoOverlaySettings();
        }

        settings.CustomThemes ??= new();
        settings.RecentThemeColors ??= new();
        settings.CustomThemes.RemoveAll(theme => string.IsNullOrWhiteSpace(theme.Id) ||
            !CustomThemeLibrary.IsColor(theme.BaseColor) || !CustomThemeLibrary.IsColor(theme.AccentColor) ||
            !CustomThemeLibrary.TryNormalizeName(theme.Name, settings.CustomThemes, theme.Id, out _, out _));
        settings.RecentThemeColors = settings.RecentThemeColors.Where(CustomThemeLibrary.IsColor)
            .Select(color => color.ToUpperInvariant()).Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(CustomThemeLibrary.RecentColorLimit).ToList();
        // Prune malformed sets, then resolve the selection against what is left:
        // IsKnown can only check a custom reference's shape, so a reference to a
        // set that was deleted is caught here, where the list is in scope.
        settings.CustomKeyboardLayouts ??= new();
        settings.CustomKeyboardLayouts.RemoveAll(layout => layout is null || !Guid.TryParse(layout.Id, out _));
        var validLayouts = new List<CustomKeyboardLayout>();
        foreach (var layout in settings.CustomKeyboardLayouts)
        {
            if (!CustomKeyboardLibrary.TryNormalizeName(layout.Name, validLayouts, null, out var name, out _)) continue;
            layout.Name = name;
            validLayouts.Add(layout);
        }
        settings.CustomKeyboardLayouts = validLayouts;
        settings.CustomKeyboardLayouts = settings.CustomKeyboardLayouts.DistinctBy(layout => layout.Id, StringComparer.OrdinalIgnoreCase).ToList();
        foreach (var layout in settings.CustomKeyboardLayouts) layout.Keys = CustomKeyboardLibrary.Sanitize(layout.Keys);

        settings.VideoOverlays ??= new VideoOverlaySettings();
        settings.CustomGameSettings ??= new(StringComparer.OrdinalIgnoreCase);
        foreach (var overlays in settings.CustomGameSettings.Values.Select(profile => profile.VideoOverlays)
                     .Append(settings.VideoOverlays).OfType<VideoOverlaySettings>()) NormalizeOverlays(overlays, settings.CustomKeyboardLayouts);

        settings.SettingsSchemaVersion = CurrentSchemaVersion;
        return true;
    }

    private static void NormalizeOverlays(VideoOverlaySettings overlays, IReadOnlyList<CustomKeyboardLayout> layouts)
    {
        var keyboardLayout = overlays.KeyboardLayout;
        var customSelection = CustomKeyboardLibrary.IsCustomSelection(keyboardLayout);
        overlays.KeyboardLayout =
            KeyboardOverlayCatalog.IsKnown(keyboardLayout) &&
            (!customSelection || CustomKeyboardLibrary.Find(layouts, keyboardLayout) is not null)
                ? keyboardLayout : "None";
        overlays.CameraTransform = VideoOverlayLayout.Normalize(overlays.CameraTransform, VideoOverlayLayout.CameraAspectRatio);
        overlays.KeyboardTransform = VideoOverlayLayout.Normalize(overlays.KeyboardTransform,
            KeyboardOverlayCatalog.Resolve(overlays.KeyboardLayout, layouts).AspectRatio);
    }
}
