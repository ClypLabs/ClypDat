using ClypDat.App.Services;
using Xunit;

namespace ClypDat.GameDetection.Tests;

public sealed class PlaybackLoadGateTests
{
    [Fact]
    public async Task CancelledOlderLoadCannotOverlapNewerLoad()
    {
        var gate = new PlaybackLoadGate();
        using var firstEntered = new ManualResetEventSlim();
        using var releaseFirst = new ManualResetEventSlim();
        using var secondCancellation = new CancellationTokenSource();
        var events = new List<string>();

        var first = Task.Run(async () =>
        {
            using var load = await gate.EnterAsync(CancellationToken.None);
            events.Add("first-enter");
            firstEntered.Set();
            releaseFirst.Wait();
            events.Add("first-exit");
        });

        Assert.True(firstEntered.Wait(TimeSpan.FromSeconds(1)));
        var second = gate.EnterAsync(secondCancellation.Token);
        Assert.False(second.IsCompleted);
        secondCancellation.Cancel();
        await Assert.ThrowsAsync<OperationCanceledException>(() => second);
        releaseFirst.Set();
        using var secondLoad = await gate.EnterAsync(CancellationToken.None);
        events.Add("second-enter");

        await first;
        Assert.Equal(new[] { "first-enter", "first-exit", "second-enter" }, events);
    }
}
