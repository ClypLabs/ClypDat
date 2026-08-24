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
    public void WgcGame_StaysEnabled(string? value)
    {
        Assert.True(HybridCaptureBackendPolicy.UseWgcForGame(value));
    }

    [Fact]
    public void Dxgi_IsReservedForDesktopCapture()
    {
        Assert.True(HybridCaptureBackendPolicy.UseDxgiForDesktop(isMonitorMode: true));
        Assert.False(HybridCaptureBackendPolicy.UseDxgiForDesktop(isMonitorMode: false));
    }
}
