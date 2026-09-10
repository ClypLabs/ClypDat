using ClypDat.App.Services;
using ClypDat.Core.Settings;
using Xunit;

namespace ClypDat.App.Tests;

public sealed class GamePortraitServiceTests
{
    [Fact]
    public void MissingStandaloneExecutableHasNoPortrait()
    {
        Assert.Null(GamePortraitService.TryLoadStandalone(Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".exe")));
    }

    [Fact]
    public void ReadsPathFromLegacyStandaloneKey()
    {
        var entry = new GameCaptureOverride { ExecutableName = "standalone:D:\\Games\\osu!\\osu!.exe" };
        Assert.Equal("D:\\Games\\osu!\\osu!.exe", StandaloneGameIdentity.ExecutablePath(entry));
    }
}
