using ClypDat.App.Services;
using ClypDat.App.ViewModels;
using Xunit;

namespace ClypDat.App.Tests;

public sealed class SpotifyPostSaveCoordinatorTests
{
    private static string NewPath() => Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".mp4");

    [Fact]
    public async Task AllSaveOriginsShareSerializationAndDuplicateCompletions()
    {
        var coordinator = new SpotifyPostSaveCoordinator();
        var paths = Enumerable.Range(0, 4).Select(_ => NewPath()).ToArray();
        var started = new TaskCompletionSource();
        var finish = new TaskCompletionSource();
        var active = 0;
        var calls = 0;
        async Task<SpotifyOverlayOutcome> Process(CancellationToken _)
        {
            Assert.Equal(1, ++active);
            calls++;
            started.TrySetResult();
            await finish.Task;
            active--;
            return SpotifyOverlayOutcome.Completed;
        }
        var first = coordinator.RunAsync(paths[0], "worker", false, () => Task.CompletedTask, Process);
        Assert.Same(first, coordinator.RunAsync(paths[0].ToUpperInvariant(), "worker", false, () => Task.CompletedTask, Process));
        var tasks = paths.Skip(1).Select((path, index) => coordinator.RunAsync(path,
            new[] { "editor-button", "auto-clip", "recovered" }[index], false, () => Task.CompletedTask, Process)).ToArray();
        await started.Task;
        Assert.All(paths, path => Assert.True(SpotifyProcessingPaths.IsProcessing(path)));
        Assert.Equal(1, calls);
        finish.SetResult();
        await Task.WhenAll(tasks.Append(first));
        Assert.Equal(4, calls);
        Assert.All(paths, path => Assert.False(SpotifyProcessingPaths.IsProcessing(path)));
        // Worker can reserve again before its duplicate reaches the UI thread.
        SpotifyProcessingPaths.Reserve(paths[0]);
        Assert.False(SpotifyProcessingPaths.IsProcessing(paths[0]));
        await coordinator.RunAsync(paths[0], "worker", false, () => Task.CompletedTask, Process);
        Assert.False(SpotifyProcessingPaths.IsProcessing(paths[0]));
        Assert.Equal(4, calls);
    }

    [Fact]
    public async Task ReservationBlocksNewReadersAndWaitsForOldReaders()
    {
        var path = NewPath();
        var reader = SpotifyProcessingPaths.TryRead(path)!;
        var coordinator = new SpotifyPostSaveCoordinator();
        var released = new TaskCompletionSource();
        var process = coordinator.RunAsync(path, null, false, () => { released.SetResult(); return Task.CompletedTask; },
            _ => Task.FromResult(SpotifyOverlayOutcome.Completed));
        Assert.Null(SpotifyProcessingPaths.TryRead(path));
        await released.Task;
        Assert.False(process.IsCompleted);
        reader.Dispose();
        Assert.Equal(SpotifyOverlayOutcome.Completed, await process);
        using var reopened = SpotifyProcessingPaths.TryRead(path);
        Assert.NotNull(reopened);
    }

    [Fact]
    public async Task FailureSurvivesCardRecreationAndExplicitRetryUnlocks()
    {
        var path = NewPath();
        var coordinator = new SpotifyPostSaveCoordinator();
        var media = new MediaFileInfo("clip", path, DateTimeOffset.Now, TimeSpan.FromSeconds(1), 1, "",
            [new(0, "video", "h264", "Video")], 320, 180, 25);
        ClipCardViewModel Card() => new(media, Path.GetTempPath());
        await coordinator.RunAsync(path, null, false, () => Task.CompletedTask, _ => Task.FromResult(SpotifyOverlayOutcome.Failed));
        Assert.True(Card().IsSpotifyOverlayFailed);
        Assert.True(Card().IsOpenable);
        var finishRetry = new TaskCompletionSource<SpotifyOverlayOutcome>();
        var retry = coordinator.RunAsync(path, null, true, () => Task.CompletedTask, _ => finishRetry.Task);
        var recreated = Card();
        Assert.True(recreated.IsSpotifyProcessing);
        Assert.False(recreated.IsFinalizing);
        Assert.False(recreated.IsOpenable);
        finishRetry.SetResult(SpotifyOverlayOutcome.Completed);
        await retry;
        Assert.False(recreated.IsSpotifyOverlayFailed);
        Assert.True(recreated.IsOpenable);
    }

    [Fact]
    public async Task CancelledQueuedSaveDoesNotRunAndReleasesReservation()
    {
        var path = NewPath();
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        var result = await new SpotifyPostSaveCoordinator().RunAsync(path, null, false,
            () => throw new Exception("Must not release readers"), _ => throw new Exception("Must not encode"), cancelled.Token);
        Assert.Equal(SpotifyOverlayOutcome.Cancelled, result);
        Assert.False(SpotifyProcessingPaths.IsProcessing(path));
    }
}
