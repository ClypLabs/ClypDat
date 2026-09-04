using ClypDat.App.Services;
using ClypDat.Core.Settings;
using Xunit;

namespace ClypDat.App.Tests;

public sealed class CustomGameSettingsTests
{
    [Fact]
    public void AllGroups_ContainsOnlySupportedGroups()
    {
        Assert.Equal(
            [
                CustomGameSettingsResolver.RecordingModeGroup,
                CustomGameSettingsResolver.QualityGroup,
                CustomGameSettingsResolver.ReplayGroup,
                CustomGameSettingsResolver.AudioGroup
            ],
            CustomGameSettingsResolver.AllGroups);
    }

    [Fact]
    public void PruneUnknownGroups_RemovesRetiredGroupsWithoutChangingValidOverrides()
    {
        var profile = new CustomGameProfile
        {
            RecordingMode = CustomGameSettingsResolver.OffMode,
            Groups = [CustomGameSettingsResolver.RecordingModeGroup, "CaptureMethod", "Retired", "recordingmode"]
        };

        profile.PruneUnknownGroups();

        Assert.Equal([CustomGameSettingsResolver.RecordingModeGroup], profile.Groups);
        Assert.Equal(CustomGameSettingsResolver.OffMode, profile.RecordingMode);
    }

    [Fact]
    public void ReplayHotkey_OnlyAppliesWhenReplayGroupEnabled()
    {
        var settings = new AppSettings { SaveReplayHotkey = "Ctrl+Shift+F9" };
        settings.CustomGameSettings["game.exe"] = new CustomGameProfile
        {
            SaveReplayHotkey = "Alt+F9",
            Groups = [CustomGameSettingsResolver.ReplayGroup]
        };

        Assert.Equal("Alt+F9", CustomGameSettingsResolver.Resolve(settings, "game.exe").SaveReplayHotkey);
        settings.CustomGameSettings["game.exe"].Groups.Clear();
        Assert.Equal("Ctrl+Shift+F9", CustomGameSettingsResolver.Resolve(settings, "game.exe").SaveReplayHotkey);
    }

    [Fact]
    public void FullSessionHotkey_OnlyAppliesWhenReplayGroupEnabled()
    {
        var settings = new AppSettings { FullSessionHotkey = "F8" };
        settings.CustomGameSettings["game.exe"] = new CustomGameProfile
        {
            FullSessionHotkey = "F10",
            Groups = [CustomGameSettingsResolver.ReplayGroup]
        };

        Assert.Equal("F10", CustomGameSettingsResolver.Resolve(settings, "game.exe").FullSessionHotkey);
        settings.CustomGameSettings["game.exe"].Groups.Clear();
        Assert.Equal("F8", CustomGameSettingsResolver.Resolve(settings, "game.exe").FullSessionHotkey);
    }

    [Fact]
    public void ReplayGroupSeedsBothRecordingHotkeys()
    {
        var settings = new AppSettings { SaveReplayHotkey = "Insert", FullSessionHotkey = "F8" };
        var profile = new CustomGameProfile { SaveReplayHotkey = "F9", FullSessionHotkey = "F10" };

        CustomGameSettingsResolver.SeedGroupFromGlobal(settings, profile, CustomGameSettingsResolver.ReplayGroup);

        Assert.Equal("Insert", profile.SaveReplayHotkey);
        Assert.Equal("F8", profile.FullSessionHotkey);
    }

    [Fact]
    public void ReplayHotkey_ResolvesAliasProfile()
    {
        var settings = new AppSettings { SaveReplayHotkey = "Ctrl+Shift+F9" };
        settings.GameCaptureOverrides.Add(new GameCaptureOverride { ExecutableName = "steam-1", DisplayName = "Game" });
        settings.GameCaptureOverrides.Add(new GameCaptureOverride { ExecutableName = "game.exe", DisplayName = "Game" });
        settings.CustomGameSettings["game.exe"] = new CustomGameProfile
        {
            SaveReplayHotkey = "Alt+F9",
            Groups = [CustomGameSettingsResolver.ReplayGroup]
        };

        Assert.Equal("Alt+F9", CustomGameSettingsResolver.Resolve(settings, "steam-1").SaveReplayHotkey);
    }

    [Fact]
    public void V5Migration_ResetsDormantProfileHotkeysToGlobalHotkey()
    {
        var settings = new AppSettings { SettingsSchemaVersion = 5, SaveReplayHotkey = "Ctrl+Alt+F9" };
        settings.CustomGameSettings["game.exe"] = new CustomGameProfile { SaveReplayHotkey = "Alt+F9" };

        Assert.True(AppSettingsMigrations.Apply(settings));
        Assert.Equal(AppSettingsMigrations.CurrentSchemaVersion, settings.SettingsSchemaVersion);
        Assert.Equal("Ctrl+Alt+F9", settings.CustomGameSettings["game.exe"].SaveReplayHotkey);
    }
}
