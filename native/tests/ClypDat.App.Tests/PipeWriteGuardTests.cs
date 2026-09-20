using ClypDat.App.Services;
using Xunit;

namespace ClypDat.App.Tests;

public sealed class PipeWriteGuardTests
{
    private static readonly TimeSpan Budget = TimeSpan.FromMilliseconds(150);

    [Fact]
    public async Task WriteAsync_HealthyPeer_WritesAndReleasesTheGate()
    {
        var gate = new SemaphoreSlim(1, 1);
        var stalled = false;

        await PipeWriteGuard.WriteAsync(gate, Budget, _ => Task.CompletedTask, () => stalled = true, CancellationToken.None);

        Assert.False(stalled);
        Assert.Equal(1, gate.CurrentCount);
    }

    [Fact]
    public async Task WriteAsync_PeerStopsReading_ReportsItAndFreesTheGate()
    {
        // The wedge: a client that never drains the pipe. The write must not
        // outlive its budget, and the gate must come back so the next message
        // is not stuck behind a peer that is never coming back.
        var gate = new SemaphoreSlim(1, 1);
        var stalled = false;

        var failure = await Assert.ThrowsAsync<IOException>(() => PipeWriteGuard.WriteAsync(
            gate, Budget, token => Task.Delay(Timeout.Infinite, token), () => stalled = true, CancellationToken.None));

        Assert.True(stalled);
        Assert.Equal(1, gate.CurrentCount);
        Assert.Contains("stopped reading", failure.Message);
    }

    [Fact]
    public async Task WriteAsync_GateHeldByAStalledWrite_DoesNotQueueBehindIt()
    {
        var gate = new SemaphoreSlim(1, 1);
        await gate.WaitAsync();
        var stalled = false;

        var started = DateTime.UtcNow;
        await Assert.ThrowsAsync<IOException>(() => PipeWriteGuard.WriteAsync(
            gate, Budget, _ => Task.CompletedTask, () => stalled = true, CancellationToken.None));

        Assert.True(stalled);
        Assert.True(DateTime.UtcNow - started < TimeSpan.FromSeconds(2));
        // The holder still owns it: this call must not have released someone
        // else's gate on its way out.
        Assert.Equal(0, gate.CurrentCount);
    }

    [Fact]
    public async Task WriteAsync_Cancelled_PropagatesCancellationRatherThanBlamingThePeer()
    {
        var gate = new SemaphoreSlim(1, 1);
        using var cancellation = new CancellationTokenSource();
        var stalled = false;
        await cancellation.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => PipeWriteGuard.WriteAsync(
            gate, Budget, _ => Task.CompletedTask, () => stalled = true, cancellation.Token));

        Assert.False(stalled);
    }
}
