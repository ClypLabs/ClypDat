using System.Text.Json;
using ClypDat.Core.Settings;
using Xunit;

namespace ClypDat.App.Tests;

public sealed class AppSettingsTests
{
    [Fact]
    public void DiscordRichPresenceOnlyWhenGameActive_MissingJsonValue_DefaultsTrue()
    {
        var settings = JsonSerializer.Deserialize<AppSettings>("{}");

        Assert.NotNull(settings);
        Assert.True(settings.DiscordRichPresenceOnlyWhenGameActive);
    }

    [Fact]
    public void DiscordRichPresenceOnlyWhenGameActive_ExplicitFalse_IsPreserved()
    {
        var settings = JsonSerializer.Deserialize<AppSettings>("{\"DiscordRichPresenceOnlyWhenGameActive\":false}");

        Assert.NotNull(settings);
        Assert.False(settings.DiscordRichPresenceOnlyWhenGameActive);
    }

    [Fact]
    public void CustomTheme_ExportImport_RoundTripsAndSuffixesCollision()
    {
        var existing = new List<CustomThemeSettings> { new() { Name = "Night", BaseColor = "#010203", AccentColor = "#040506" } };
        var json = CustomThemeLibrary.Export(existing[0]);

        Assert.True(CustomThemeLibrary.TryImport(json, existing, out var imported, out var error), error);
        Assert.NotNull(imported);
        Assert.Equal("Night (2)", imported.Name);
        Assert.Equal("#010203", imported.BaseColor);
    }

    [Theory]
    [InlineData("#ABCDEF", true)]
    [InlineData("#abcdef", true)]
    [InlineData("#ABCDE", false)]
    [InlineData("#AABBCCDD", false)]
    public void CustomTheme_OnlyAcceptsOpaqueRgb(string color, bool valid) =>
        Assert.Equal(valid, CustomThemeLibrary.IsColor(color));

    [Fact]
    public void RecentThemeColors_DeduplicatesAndCapsAtEight()
    {
        var settings = new AppSettings();
        CustomThemeLibrary.AddRecent(settings, Enumerable.Range(0, 10).Select(number => $"#{number:X6}").ToArray());
        CustomThemeLibrary.AddRecent(settings, "#000009");
        Assert.Equal(8, settings.RecentThemeColors.Count);
        Assert.Equal("#000009", settings.RecentThemeColors[0]);
    }
}
