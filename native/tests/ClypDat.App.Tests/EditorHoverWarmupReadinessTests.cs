using ClypDat.App.Services;
using Xunit;

namespace ClypDat.App.Tests;

public sealed class EditorHoverWarmupReadinessTests
{
    [Fact]
    public async Task ClaimWaitsForSeekAndPresentedFrame()
    {
        var readiness = new EditorHoverWarmupReadiness();
        var loaded = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var presented = false;
        var claim = readiness.CanAdoptAsync(loaded.Task, () => true, () => presented, CancellationToken.None);

        loaded.SetResult();
        await Task.Yield();
        Assert.False(claim.IsCompleted);
        presented = true;
        readiness.Complete(true);

        Assert.True(await claim);
    }

    [Fact]
    public async Task ReadyWarmupIsAdopted()
    {
        var readiness = new EditorHoverWarmupReadiness();
        readiness.Complete(true);
        Assert.True(await readiness.CanAdoptAsync(Task.CompletedTask, () => true, () => true, CancellationToken.None));
    }

    [Fact]
    public async Task ClaimWithoutPlayerAttachmentTimesOut()
    {
        var readiness = new EditorHoverWarmupReadiness();
        string? reason = null;
        Assert.False(await readiness.CanAdoptAsync(Task.CompletedTask, () => false, () => true,
            CancellationToken.None, TimeSpan.FromMilliseconds(20), value => reason = value));
        Assert.Equal("player-attach-timeout", reason);
    }

    [Fact]
    public async Task ClaimWaitsForPlayerAttachmentBeforeCheckingSeek()
    {
        var readiness = new EditorHoverWarmupReadiness();
        readiness.Complete(true);
        var attached = false;
        var claim = readiness.CanAdoptAsync(Task.CompletedTask, () => attached, () => true,
            CancellationToken.None, TimeSpan.FromSeconds(1));

        await Task.Delay(20);
        Assert.False(claim.IsCompleted);

        attached = true;
        readiness.MarkPlayerAttached();
        Assert.True(await claim);
    }

    [Theory]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public async Task FailedSeekOrMissingNativeFrameFallsBack(bool seekSucceeded, bool presented)
    {
        var readiness = new EditorHoverWarmupReadiness();
        readiness.Complete(seekSucceeded);
        string? reason = null;
        Assert.False(await readiness.CanAdoptAsync(Task.CompletedTask, () => true, () => presented,
            CancellationToken.None, onFailure: value => reason = value));
        Assert.Equal(seekSucceeded ? "native-frame-missing" : "seek-failed", reason);
    }

    [Fact]
    public async Task PendingSeekTimesOutAndFallsBack()
    {
        var readiness = new EditorHoverWarmupReadiness();
        string? reason = null;
        Assert.False(await readiness.CanAdoptAsync(Task.CompletedTask, () => true, () => true,
            CancellationToken.None, TimeSpan.FromMilliseconds(20), value => reason = value));
        Assert.Equal("seek-timeout", reason);
    }

    [Fact]
    public async Task CancelledOpenDoesNotAdopt()
    {
        var readiness = new EditorHoverWarmupReadiness();
        using var cancellation = new CancellationTokenSource();
        var claim = readiness.CanAdoptAsync(Task.CompletedTask, () => true, () => true, cancellation.Token);
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => claim);
    }
}
