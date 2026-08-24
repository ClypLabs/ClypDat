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
