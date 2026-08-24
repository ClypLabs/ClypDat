using ClypDat.App.Services;
using Xunit;

namespace ClypDat.GameDetection.Tests;

public sealed class EditorSeekRequestQueueTests
{
    [Fact]
    public void PreviewRequests_CoalesceToLatestTarget()
    {
        var queue = new EditorSeekRequestQueue();
        queue.QueuePreview(TimeSpan.FromSeconds(1));
        queue.QueuePreview(TimeSpan.FromSeconds(2));

        Assert.True(queue.TryTakePreview(out var target, out _));
        Assert.Equal(TimeSpan.FromSeconds(2), target);
        Assert.False(queue.TryTakePreview(out _, out _));
    }

    [Fact]
    public void FinalSeek_DiscardsPendingPreview()
    {
        var queue = new EditorSeekRequestQueue();
        queue.QueuePreview(TimeSpan.FromSeconds(8));
        queue.BeginFinalSeek();

        Assert.False(queue.TryTakePreview(out _, out _));
        queue.CompleteFinalSeek();
        Assert.False(queue.TryTakePreview(out _, out _));
    }

    [Fact]
    public void PreviewWrites_AreLimitedToTenPerSecondAndCoalesceLatestTarget()
    {
        var queue = new EditorSeekRequestQueue();
        var now = DateTimeOffset.UnixEpoch;
        var first = queue.QueuePreview(TimeSpan.FromSeconds(1));
        Assert.True(queue.TryTakePreview(now, out var target, out var generation, out var delay));
        Assert.Equal(TimeSpan.FromSeconds(1), target);
        Assert.Equal(first, generation);
        Assert.Equal(TimeSpan.Zero, delay);
        queue.MarkPreviewWritten(generation, now);

        queue.QueuePreview(TimeSpan.FromSeconds(2));
        queue.QueuePreview(TimeSpan.FromSeconds(3));
        Assert.False(queue.TryTakePreview(now + TimeSpan.FromMilliseconds(99), out _, out _, out delay));
        Assert.Equal(TimeSpan.FromMilliseconds(1), delay);
        Assert.True(queue.TryTakePreview(now + TimeSpan.FromMilliseconds(100), out target, out generation, out delay));
        Assert.Equal(TimeSpan.FromSeconds(3), target);
    }

    [Fact]
    public void FinalSeek_PreemptsPreviewAndCapturesQuietPeriod()
    {
        var queue = new EditorSeekRequestQueue();
        var now = DateTimeOffset.UnixEpoch;
        queue.QueuePreview(TimeSpan.FromSeconds(1));
        Assert.True(queue.TryTakePreview(now, out _, out var generation, out _));
        queue.MarkPreviewWritten(generation, now);
        queue.QueuePreview(TimeSpan.FromSeconds(2));

        var final = queue.BeginFinalSeek(now + TimeSpan.FromMilliseconds(25));

        Assert.Equal(1, final.PreviewWriteCount);
        Assert.Equal(TimeSpan.FromMilliseconds(75), final.QuietPeriod);
        Assert.False(final.RequiresDecoderReset);
        Assert.False(queue.TryTakePreview(now + TimeSpan.FromMilliseconds(100), out _, out _, out _));
    }

    [Fact]
    public void FourPreviewWrites_RequireProactiveResetWithoutQuietWait()
    {
        var queue = new EditorSeekRequestQueue();
        var now = DateTimeOffset.UnixEpoch;
        for (var i = 0; i < EditorSeekRequestQueue.StressPreviewWriteCount; i++)
        {
            queue.QueuePreview(TimeSpan.FromSeconds(i));
            Assert.True(queue.TryTakePreview(now, out _, out var generation, out _));
            queue.MarkPreviewWritten(generation, now);
            now += EditorSeekRequestQueue.PreviewInterval;
        }

        var final = queue.BeginFinalSeek(now);
        Assert.True(final.RequiresDecoderReset);
        Assert.Equal(TimeSpan.Zero, final.QuietPeriod);
    }

    [Fact]
    public void SupersededFinalCompletion_DoesNotReopenPreviewProcessing()
    {
        var queue = new EditorSeekRequestQueue();
        var first = queue.BeginFinalSeek();
        var second = queue.BeginFinalSeek();

        queue.CompleteFinalSeek(first);
        queue.QueuePreview(TimeSpan.FromSeconds(4));
        Assert.False(queue.TryTakePreview(out _, out _));

        queue.CompleteFinalSeek(second);
        queue.QueuePreview(TimeSpan.FromSeconds(4));
        Assert.True(queue.TryTakePreview(out var target, out _));
        Assert.Equal(TimeSpan.FromSeconds(4), target);
    }

    [Theory]
    [InlineData(-1, 0)]
    [InlineData(0, 0)]
    [InlineData(1250, 1250)]
    public void Targets_NormalizeAtZero(int requestedMs, int expectedMs) =>
        Assert.Equal(TimeSpan.FromMilliseconds(expectedMs), EditorSeekRequestQueue.Normalize(TimeSpan.FromMilliseconds(requestedMs)));

    [Theory]
    [InlineData(true, true, true)]
    [InlineData(true, false, false)]
    [InlineData(false, true, false)]
    public void TransportRestoresOnlyAfterSuccessfulResume(bool wasPlaying, bool seekSucceeded, bool expected) =>
        Assert.Equal(expected, EditorSeekRequestQueue.ShouldResume(wasPlaying, seekSucceeded));
}
