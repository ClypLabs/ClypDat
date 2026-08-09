using ClypDat.App.Services;
using Xunit;

namespace ClypDat.GameDetection.Tests;

public sealed class EncoderFrameLifetimeQueueTests
{
    [Fact]
    public void SubmittedFramesRemainAliveUntilMatchingPacketsDrain()
    {
        var released = new List<nint>();
        var queue = new EncoderFrameLifetimeQueue(released.Add);
        var firstTime = DateTime.UnixEpoch;
        var secondTime = firstTime.AddMilliseconds(8);

        queue.Enqueue((nint)1, firstTime);
        queue.Enqueue((nint)2, secondTime);

        Assert.Empty(released);
        Assert.True(queue.TryTake(out var first));
        Assert.Equal(firstTime, first.WallClockUtc);
        queue.Release(first);

        Assert.Equal(new[] { (nint)1 }, released);
        Assert.True(queue.TryTake(out var second));
        Assert.Equal(secondTime, second.WallClockUtc);
        queue.Release(second);
        Assert.Equal(new[] { (nint)1, (nint)2 }, released);
    }

    [Fact]
    public void ReleaseAllFreesDelayedFramesExactlyOnce()
    {
        var released = new List<nint>();
        var queue = new EncoderFrameLifetimeQueue(released.Add);

        queue.Enqueue((nint)1, DateTime.UnixEpoch);
        queue.Enqueue((nint)2, DateTime.UnixEpoch);
        queue.ReleaseAll();
        queue.ReleaseAll();

        Assert.Equal(new[] { (nint)1, (nint)2 }, released);
        Assert.Equal(2, queue.PeakCount);
        Assert.Equal(0, queue.Count);
    }
}
