using ClypDat.App.Services;
using Xunit;

namespace ClypDat.App.Tests;

public sealed class EditorSeekRequestQueueTests
{
    [Fact]
    public void CompletedPreview_AllowsNextTargetWithoutFixedDelay()
    {
        var queue = new EditorSeekRequestQueue();
        var now = DateTimeOffset.UtcNow;
        var generation = queue.QueuePreview(TimeSpan.FromSeconds(1));
        queue.MarkPreviewWritten(generation, now);
        queue.QueuePreview(TimeSpan.FromSeconds(2));
        Assert.True(queue.TryTakePreview(now, out var target, out _, out _));
        Assert.Equal(TimeSpan.FromSeconds(2), target);
        Assert.Equal(TimeSpan.Zero, queue.BeginFinalSeek(now).QuietPeriod);
    }

    [Fact]
    public void PreviewGeneration_CanParkBeforeFinalSeek()
    {
        var queue = new EditorSeekRequestQueue();
        var generation = queue.QueuePreview(TimeSpan.FromSeconds(1));

        using var lease = queue.TryAcquirePreviewTransport(generation);

        Assert.NotNull(lease);
    }

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
    public void PostFinalPreviewGeneration_CanParkNormally()
    {
        var queue = new EditorSeekRequestQueue();
        var oldGeneration = queue.QueuePreview(TimeSpan.FromSeconds(1));
        var final = queue.BeginFinalSeek(DateTimeOffset.UtcNow);
        queue.CompleteFinalSeek(final.Generation);
        var newGeneration = queue.QueuePreview(TimeSpan.FromSeconds(2));

        Assert.Null(queue.TryAcquirePreviewTransport(oldGeneration));
        using var lease = queue.TryAcquirePreviewTransport(newGeneration);
        Assert.NotNull(lease);
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

    [Fact]
    public void FinalSeek_RejectsPreviewWithoutInvalidatingItsTransport()
    {
        var queue = new EditorSeekRequestQueue();
        queue.BeginFinalSeek(DateTimeOffset.UtcNow);

        Assert.False(queue.TryQueuePreview(TimeSpan.FromSeconds(2)));
    }
}
