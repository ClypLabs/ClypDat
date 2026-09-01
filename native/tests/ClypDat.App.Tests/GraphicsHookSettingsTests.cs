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
}
