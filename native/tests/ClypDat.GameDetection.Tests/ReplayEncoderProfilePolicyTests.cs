using ClypDat.App.Services;
using Xunit;

namespace ClypDat.GameDetection.Tests;

public sealed class ReplayEncoderProfilePolicyTests
{
    [Fact]
    public void Resolve_AlwaysUsesAutomaticProfile() =>
        Assert.Equal(ReplayEncoderProfilePolicy.Automatic, ReplayEncoderProfilePolicy.Resolve());

    [Theory]
    [InlineData(30, 16)]
    [InlineData(60, 16)]
    [InlineData(90, 20)]
    [InlineData(120, 23)]
    [InlineData(144, 23)]
    public void NvencSurfaces_KeepsPipelineAheadOfReplayQueue(int frameRate, int expected) =>
        Assert.Equal(expected, ReplayEncoderProfilePolicy.NvencSurfaces(frameRate));

    [Theory]
    [InlineData(30, 30)]
    [InlineData(60, 60)]
    [InlineData(90, 90)]
    [InlineData(120, 120)]
    [InlineData(1, 30)]
    [InlineData(1000, 120)]
    public void GopFrames_UsesOneSecondAtTheSupportedFrameRate(int frameRate, int expected) =>
        Assert.Equal(expected, ReplayEncoderProfilePolicy.GopFrames(frameRate));
}
