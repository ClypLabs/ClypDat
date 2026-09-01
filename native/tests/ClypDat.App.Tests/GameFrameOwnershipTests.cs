using ClypDat.App.Services;
using Xunit;

namespace ClypDat.App.Tests;

public sealed class GameFrameOwnershipTests
{
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
}
