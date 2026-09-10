using ClypDat.App.Services;
using Xunit;

namespace ClypDat.App.Tests;

public sealed class GamePortraitServiceTests
{
    [Fact]
    public void MissingStandaloneExecutableHasNoPortrait()
    {
        Assert.Null(GamePortraitService.TryLoadStandalone(Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".exe")));
    }
}
