using ClypDat.App.Services;
using Xunit;

namespace ClypDat.GameDetection.Tests;

public sealed class HybridCaptureBackendPolicyTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("0")]
    [InlineData("true")]
    public void WgcGame_DefaultsOnUnlessDxgiForced(string? value) =>
        Assert.True(HybridCaptureBackendPolicy.UseWgcForGame(value));

    [Fact]
    public void WgcGame_ForceDxgiOverrideDisablesWgc() =>
        Assert.False(HybridCaptureBackendPolicy.UseWgcForGame("1"));
}
