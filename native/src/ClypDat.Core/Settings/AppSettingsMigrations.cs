namespace ClypDat.Core.Settings;

public static class AppSettingsMigrations
{
    public const int CurrentSchemaVersion = 1;

    public static bool Apply(AppSettings settings)
    {
        if (settings.SettingsSchemaVersion >= CurrentSchemaVersion) return false;

        // Replay quality has always been persisted as independent values. Keep
        // that triplet intact when the UI's presets or defaults evolve: a user
        // who chose 1440p at 90 FPS must remain on that exact configuration.
        settings.SettingsSchemaVersion = CurrentSchemaVersion;
        return true;
    }
}
