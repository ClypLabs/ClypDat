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
    public void WgcGame_DefaultsOff(string? value) =>
        Assert.False(HybridCaptureBackendPolicy.UseWgcForGame(value));

    [Fact]
    public void WgcGame_RequiresExplicitDiagnosticOptIn() =>
        Assert.True(HybridCaptureBackendPolicy.UseWgcForGame("1"));
}
