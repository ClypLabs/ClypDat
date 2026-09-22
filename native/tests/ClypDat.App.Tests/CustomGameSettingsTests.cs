using ClypDat.App.Services;
using ClypDat.Core.Settings;
using Xunit;

namespace ClypDat.App.Tests;

public sealed class CustomGameSettingsTests
{
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
    public void V12Migration_MovesUntouchedSaveHotkeyToInsert()
    {
        var settings = new AppSettings { SettingsSchemaVersion = 12, SaveReplayHotkey = "Ctrl+Shift+F9" };
        settings.CustomGameSettings["inherited.exe"] = new CustomGameProfile { SaveReplayHotkey = "Ctrl+Shift+F9" };
        settings.CustomGameSettings["custom.exe"] = new CustomGameProfile { SaveReplayHotkey = "Alt+F9" };

        Assert.True(AppSettingsMigrations.Apply(settings));
        Assert.Equal("Insert", settings.SaveReplayHotkey);
        Assert.Equal("Insert", settings.CustomGameSettings["inherited.exe"].SaveReplayHotkey);
        Assert.Equal("Alt+F9", settings.CustomGameSettings["custom.exe"].SaveReplayHotkey);
    }

    [Fact]
    public void V12Migration_KeepsCustomizedSaveHotkey()
    {
        var settings = new AppSettings { SettingsSchemaVersion = 12, SaveReplayHotkey = "Alt+F10" };
        settings.CustomGameSettings["game.exe"] = new CustomGameProfile { SaveReplayHotkey = "Ctrl+Shift+F9" };

        Assert.True(AppSettingsMigrations.Apply(settings));
        Assert.Equal("Alt+F10", settings.SaveReplayHotkey);
        Assert.Equal("Ctrl+Shift+F9", settings.CustomGameSettings["game.exe"].SaveReplayHotkey);
    }

    [Fact]
    public void V14Migration_TurnsSpotifyOffOnceAndNewSettingsStartOff()
    {
        Assert.False(new AppSettings().SpotifyEnabled);
        var upgraded = new AppSettings { SettingsSchemaVersion = 13, SpotifyEnabled = true };
        Assert.True(AppSettingsMigrations.Apply(upgraded));
        Assert.False(upgraded.SpotifyEnabled);
        // Turned back on afterwards, it stays on.
        upgraded.SpotifyEnabled = true;
        Assert.False(AppSettingsMigrations.Apply(upgraded));
        Assert.True(upgraded.SpotifyEnabled);
    }
}
