using ClypDat.App.Services;
using Xunit;

namespace ClypDat.App.Tests;

public sealed class GameFrameOwnershipTests
{
    [Theory]
    [InlineData(1366, 768, true)]
    [InlineData(1920, 1080, true)]
    [InlineData(2560, 1440, true)]
    [InlineData(3840, 2160, true)]
    [InlineData(3440, 1440, false)]
    public void DetectorAspectRatioAllowsSupportedLayoutsOnly(int width, int height, bool expected)
    {
        Assert.Equal(expected, NativeReplayBuffer.IsSupportedDetectorAspectRatio(width, height));
    }

    [Fact]
    public void CrossDeviceLease_MustCopyBeforeVideoProcessing()
    {
        Assert.False(NativeReplayBuffer.CanUseDirectVideoProcessorInput(
            directBltAvailable: true,
            requiresCopyBeforeProcessing: true));
        Assert.True(NativeReplayBuffer.CanUseDirectVideoProcessorInput(
            directBltAvailable: true,
            requiresCopyBeforeProcessing: false));
}

    [Fact]
    public void DxgiAcquireDeadline_CapsCallsToCaptureCadenceAndSnapsAfterAStall()
    {
        var scheduled = TimeSpan.FromSeconds(1);
        Assert.Equal(
            TimeSpan.FromSeconds(1) + TimeSpan.FromSeconds(1d / 60),
            NativeReplayBuffer.NextDxgiAcquireDeadline(scheduled, TimeSpan.FromSeconds(1.004), 60));
        Assert.Equal(
            TimeSpan.FromSeconds(1.050),
            NativeReplayBuffer.NextDxgiAcquireDeadline(scheduled, TimeSpan.FromSeconds(1.050), 60));
    }
}
