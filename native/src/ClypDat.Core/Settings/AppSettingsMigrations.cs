namespace ClypDat.Core.Settings;

public static class AppSettingsMigrations
{
    public const int CurrentSchemaVersion = 2;

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

        settings.SettingsSchemaVersion = CurrentSchemaVersion;
        return true;
    }
}
