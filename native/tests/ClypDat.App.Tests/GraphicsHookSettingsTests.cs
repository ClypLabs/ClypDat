using ClypDat.App.Services;
using ClypDat.Core.Settings;
using Xunit;

namespace ClypDat.App.Tests;

public sealed class GraphicsHookSettingsTests
{
    [Theory]
    [InlineData(null, "Display")]
    [InlineData("", "Display")]
    [InlineData("display", "Display")]
    [InlineData("Hook", "Hook")]
    [InlineData("invalid", "Display")]
    public void CaptureMethod_NormalizesToKnownWireValues(string? value, string expected)
    {
        Assert.Equal(expected, CustomGameSettingsResolver.NormalizeGameCaptureMethod(value));
    }

    [Fact]
    public void CaptureMethod_OnlyAppliesWhenGroupEnabled()
    {
        var settings = new AppSettings();
        settings.CustomGameSettings["game.exe"] = new CustomGameProfile
        {
            GameCaptureMethod = "Hook",
            Groups = new List<string> { CustomGameSettingsResolver.CaptureMethodGroup }
        };

        Assert.Equal("Hook", CustomGameSettingsResolver.Resolve(settings, "game.exe").GameCaptureMethod);

        settings.CustomGameSettings["game.exe"].Groups.Clear();
        Assert.Equal("Display", CustomGameSettingsResolver.Resolve(settings, "game.exe").GameCaptureMethod);
    }

    [Fact]
    public void ReplayConfigIdentity_ChangesWithGameCaptureMethod()
    {
        var display = new ReplayBufferConfig(60, 1080, 60, 0, 0, 1920, 1080, "", "", [], [], "", [], "Game", "game.exe", "", "",
            GameCaptureMethod: "Display");
        var hook = display with { GameCaptureMethod = "Hook" };

        Assert.NotEqual(ReplayBufferConfigIdentity.Serialize(display), ReplayBufferConfigIdentity.Serialize(hook));
    }

    [Fact]
    public void ReplayHotkey_OnlyAppliesWhenReplayGroupEnabled()
    {
        var settings = new AppSettings { SaveReplayHotkey = "Ctrl+Shift+F9" };
        settings.CustomGameSettings["game.exe"] = new CustomGameProfile
        {
            SaveReplayHotkey = "Alt+F9",
            Groups = new List<string> { CustomGameSettingsResolver.ReplayGroup }
        };

        Assert.Equal("Alt+F9", CustomGameSettingsResolver.Resolve(settings, "game.exe").SaveReplayHotkey);
        settings.CustomGameSettings["game.exe"].Groups.Clear();
        Assert.Equal("Ctrl+Shift+F9", CustomGameSettingsResolver.Resolve(settings, "game.exe").SaveReplayHotkey);
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
            Groups = new List<string> { CustomGameSettingsResolver.ReplayGroup }
        };

        Assert.Equal("Alt+F9", CustomGameSettingsResolver.Resolve(settings, "steam-1").SaveReplayHotkey);
    }

    [Fact]
    public void V5Migration_ResetsDormantProfileHotkeysToGlobalHotkey()
    {
        var settings = new AppSettings { SettingsSchemaVersion = 5, SaveReplayHotkey = "Ctrl+Alt+F9" };
        settings.CustomGameSettings["game.exe"] = new CustomGameProfile { SaveReplayHotkey = "Alt+F9" };

        Assert.True(AppSettingsMigrations.Apply(settings));
        Assert.Equal(6, settings.SettingsSchemaVersion);
        Assert.Equal("Ctrl+Alt+F9", settings.CustomGameSettings["game.exe"].SaveReplayHotkey);
    }
}
