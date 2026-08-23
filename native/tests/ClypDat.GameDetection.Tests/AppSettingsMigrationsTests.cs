using ClypDat.Core.Settings;
using Xunit;

namespace ClypDat.GameDetection.Tests;

public sealed class AppSettingsMigrationsTests
{
    [Fact]
    public void KeepsLegacyReplayQualitySelectionIntact()
    {
        var settings = new AppSettings
        {
            ReplayMaxHeight = 1440,
            ReplayFrameRate = 90,
            ReplayBitrateMbps = 40
        };

        var changed = AppSettingsMigrations.Apply(settings);

        Assert.True(changed);
        Assert.Equal(AppSettingsMigrations.CurrentSchemaVersion, settings.SettingsSchemaVersion);
        Assert.Equal(1440, settings.ReplayMaxHeight);
        Assert.Equal(90, settings.ReplayFrameRate);
        Assert.Equal(40, settings.ReplayBitrateMbps);
    }

    [Fact]
    public void DoesNotReapplyCompletedMigration()
    {
        var settings = new AppSettings { SettingsSchemaVersion = AppSettingsMigrations.CurrentSchemaVersion };

        Assert.False(AppSettingsMigrations.Apply(settings));
    }
}
