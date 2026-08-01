using ClypDat.App.Services;
using Xunit;

namespace ClypDat.GameDetection.Tests;

public sealed class ReplayEncoderPresetPolicyTests
{
    [Theory]
    [InlineData(60)]
    [InlineData(120)]
    [InlineData(240)]
    public void Resolve_UsesP1ForHighFrameRateNativeCapture(int frameRate)
    {
        Assert.Equal("P1", ReplayEncoderPresetPolicy.Resolve("P5", frameRate, "Native"));
        Assert.Equal("P1", ReplayEncoderPresetPolicy.Resolve("P3", frameRate, "Auto"));
    }

    [Fact]
    public void Resolve_PreservesManualPresetAtThirtyFps()
    {
        Assert.Equal("P5", ReplayEncoderPresetPolicy.Resolve("P5", 30, "Native"));
    }

    [Fact]
    public void Resolve_DoesNotOverrideLegacyBackend()
    {
        Assert.Equal("P5", ReplayEncoderPresetPolicy.Resolve("P5", 60, "Legacy"));
    }
}
