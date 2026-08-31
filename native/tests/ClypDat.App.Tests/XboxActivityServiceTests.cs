using ClypDat.App.Services;
using Xunit;

namespace ClypDat.App.Tests;

public sealed class XboxActivityServiceTests
{
    [Theory]
    [InlineData("Home")]
    [InlineData("Xbox Home")]
    [InlineData("Xbox Dashboard")]
    [InlineData("Xbox Guide")]
    public void SystemTitle_IsIgnored(string title) => Assert.True(XboxActivityService.IsSystemTitle(title));

    [Fact]
    public void GameTitle_IsNotIgnored() => Assert.False(XboxActivityService.IsSystemTitle("Forza Horizon 5"));
}
