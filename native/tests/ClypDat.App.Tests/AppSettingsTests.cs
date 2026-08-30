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
}
