namespace ClypDat.Core.Settings;

public static class AppSettingsMigrations
{
    public const int CurrentSchemaVersion = 4;

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

        settings.SettingsSchemaVersion = CurrentSchemaVersion;
        return true;
    }
}
