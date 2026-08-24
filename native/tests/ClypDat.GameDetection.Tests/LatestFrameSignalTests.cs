using ClypDat.App.Services;
using Xunit;

namespace ClypDat.GameDetection.Tests;

public sealed class LatestFrameSignalTests
{
    [Fact]
    public void ProducerPublishesWithoutWaitingForConsumer_AndCoalescesBurst()
    {
        using var signal = new LatestFrameSignal();

        for (var i = 0; i < 120; i++) signal.Publish();

        var snapshot = signal.Snapshot;
        Assert.Equal(120, snapshot.Published);
        Assert.Equal(119, snapshot.Overwritten);
        Assert.True(signal.WaitAndTake(TimeSpan.Zero, CancellationToken.None));
        Assert.Equal(120, signal.Snapshot.Taken);
        Assert.False(signal.WaitAndTake(TimeSpan.Zero, CancellationToken.None));
    }

    [Fact]
    public async Task WaitWakesOnPublishWithoutPolling()
    {
        using var signal = new LatestFrameSignal();
        var waiter = Task.Run(() => signal.WaitAndTake(TimeSpan.FromSeconds(2), CancellationToken.None));

        await Task.Delay(25);
        signal.Publish();

        Assert.True(await waiter);
    }

    [Fact]
    public void CancellationStopsWait()
    {
        using var signal = new LatestFrameSignal();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        Assert.Throws<OperationCanceledException>(() => signal.WaitAndTake(TimeSpan.FromSeconds(1), cancellation.Token));
    }
}
