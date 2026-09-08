using ClypDat.App.Services;
using Xunit;

namespace ClypDat.App.Tests;

public sealed class GameFilterResolverTests
{
    [Theory]
    [InlineData("Session - HELLDIVERS™ 2", "HELLDIVERS™ 2")]
    [InlineData("HELLDIVERS™ 2 Full Session", "HELLDIVERS™ 2")]
    [InlineData("Session - HELLDIVERS™ 2 2026-09-09 20-15-00", "HELLDIVERS™ 2")]
    public void Resolve_FallbackSessionTitle_UsesSessionGame(string titleOrFileName, string expected)
    {
        var info = titleOrFileName.Contains("2026-09-09", StringComparison.Ordinal)
            ? null
            : new ClipInfo(null, null, titleOrFileName);
        var fileName = info is null ? titleOrFileName : "ordinary clip.mp4";

        Assert.Equal(expected, GameFilterResolver.Resolve(info, fileName));
    }

    [Fact]
    public void Resolve_ExplicitGame_PreservesOverrideAndTrademark()
    {
        var info = new ClipInfo("HELLDIVERS™ 2", null, "Session - Different Game");

        Assert.Equal("HELLDIVERS™ 2", GameFilterResolver.Resolve(info, "Session - Different Game.mp4"));
    }

    [Fact]
    public void Resolve_ExplicitSessionNamedGame_DoesNotParseIt()
    {
        var info = new ClipInfo("Session - HELLDIVERS™ 2", null, "ordinary clip");

        Assert.Equal("Session - HELLDIVERS™ 2", GameFilterResolver.Resolve(info, "ordinary clip"));
    }

    [Fact]
    public void Resolve_BlankExplicitGame_UsesFallbackTitle()
    {
        var info = new ClipInfo("  ", null, "Session - HELLDIVERS™ 2");

        Assert.Equal("HELLDIVERS™ 2", GameFilterResolver.Resolve(info, "ordinary clip"));
    }

    [Fact]
    public void Resolve_OrdinaryTitle_UsesExistingNormalization()
    {
        Assert.Equal("Fortnite", GameFilterResolver.Resolve(new ClipInfo(null, null, "FortniteClient-Win64-Shipping.exe"), "ignored.mp4"));
    }
}
