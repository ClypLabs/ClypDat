using ClypDat.App.Services;
using Xunit;

namespace ClypDat.App.Tests;

public sealed class EditorSeekRequestQueueTests
{
    [Fact]
    public void BeginFinalSeek_PermanentlyInvalidatesOldPreviewGeneration()
    {
        var queue = new EditorSeekRequestQueue();
        var generation = queue.QueuePreview(TimeSpan.FromSeconds(1));

        var final = queue.BeginFinalSeek(DateTimeOffset.UtcNow);
        queue.CompleteFinalSeek(final.Generation);

        Assert.Null(queue.TryAcquirePreviewTransport(generation));
    }

    [Fact]
    public void FinalCommit_CannotBeFollowedByStalePreviewPause()
    {
        var queue = new EditorSeekRequestQueue();
        var previewGeneration = queue.QueuePreview(TimeSpan.FromSeconds(1));
        var final = queue.BeginFinalSeek(DateTimeOffset.UtcNow);

        var stalePark = queue.TryAcquirePreviewTransport(previewGeneration, parking: true);
        var handoff = queue.GetHandoffSummary();

        Assert.Null(stalePark);
        Assert.Equal("final-owner", handoff.Outcome);
        Assert.Equal(1, handoff.SuppressedStaleParks);
        queue.CompleteFinalSeek(final.Generation);
    }
}
