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
    [InlineData("1")]
    public void WgcGame_IsRecoveryOnly(string? value)
    {
        Assert.False(HybridCaptureBackendPolicy.UseWgcForGame(value));
    }

    [Fact]
    public void Dxgi_IsPrimaryForEveryCaptureTarget()
    {
        Assert.True(HybridCaptureBackendPolicy.UseDxgiForDesktop(isMonitorMode: true));
        Assert.True(HybridCaptureBackendPolicy.UseDxgiForDesktop(isMonitorMode: false));
    }
}
