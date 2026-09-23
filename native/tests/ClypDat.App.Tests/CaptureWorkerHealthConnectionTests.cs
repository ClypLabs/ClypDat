using System.IO.Pipes;
using System.Reflection;
using ClypDat.App.Services;
using ClypDat.Capture.Abstractions;
using Xunit;

namespace ClypDat.App.Tests;

public sealed class CaptureWorkerHealthConnectionTests
{
    [Fact]
    public void QueuedHealth_FromDisconnectedPipe_CannotOverwriteReplacementHealth()
    {
        using var proxy = CreateProxy();
        using var oldPipe = new NamedPipeClientStream("unused-old-worker");
        using var newPipe = new NamedPipeClientStream("unused-new-worker");
        var healthy = ReplayCaptureHealth.Unknown("replacement") with { State = ReplayCaptureState.Healthy };
        Connect(proxy, newPipe);
        proxy.HandleWorkerHealth(newPipe, 0, healthy);
        var published = 0;
        proxy.HealthChanged += (_, _) => published++;

        // Both connections can share a process generation during IPC reconnect.
        proxy.HandleWorkerHealth(oldPipe, 0, FatalHealth());

        Assert.Same(healthy, proxy.GetHealthSnapshot());
        Assert.Equal(0, published);
    }

    [Fact]
    public void QueuedHealth_FromPreviousGeneration_IsIgnored()
    {
        using var proxy = CreateProxy();
        using var pipe = new NamedPipeClientStream("unused-worker");
        Connect(proxy, pipe);
        var initial = proxy.GetHealthSnapshot();

        proxy.HandleWorkerHealth(pipe, -1, FatalHealth());

        Assert.Same(initial, proxy.GetHealthSnapshot());
    }

    [Fact]
    public void QueuedHealth_AfterDispose_IsIgnored()
    {
        using var proxy = CreateProxy();
        using var pipe = new NamedPipeClientStream("unused-worker");
        Connect(proxy, pipe);
        proxy.Dispose();
        var stopped = proxy.GetHealthSnapshot();

        proxy.HandleWorkerHealth(pipe, 0, FatalHealth());

        Assert.Same(stopped, proxy.GetHealthSnapshot());
    }

    [Fact]
    public void CurrentConnection_PublishesHealthWithoutRecoveringWhenDisarmed()
    {
        using var proxy = CreateProxy();
        using var pipe = new NamedPipeClientStream("unused-worker");
        Connect(proxy, pipe);
        var health = FatalHealth();

        proxy.HandleWorkerHealth(pipe, 0, health);

        Assert.Same(health, proxy.GetHealthSnapshot());
        Assert.Equal(ReplayRecoveryStopReason.None, proxy.GetHealthSnapshot().RecoveryStopReason);
    }

    private static CaptureWorkerProxy CreateProxy() => new(() =>
        throw new InvalidOperationException("Health delivery must not start or attach a worker."));

    private static void Connect(CaptureWorkerProxy proxy, NamedPipeClientStream pipe) =>
        typeof(CaptureWorkerProxy).GetField("_pipe", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(proxy, pipe);

    private static ReplayCaptureHealth FatalHealth() => ReplayCaptureHealth.Unknown("old worker") with
    {
        State = ReplayCaptureState.Failed,
        PipelineRecoveryAction = ReplayPipelineRecoveryAction.RestartWorker,
        LastFailure = "Native failure queued before disconnect."
    };
}
