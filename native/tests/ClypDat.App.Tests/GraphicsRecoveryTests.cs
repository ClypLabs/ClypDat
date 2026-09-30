using System.Text.Json;
using ClypDat.App.Services;
using ClypDat.Capture.Abstractions;
using Xunit;

namespace ClypDat.App.Tests;

public sealed class GraphicsRecoveryTests
{
    [Fact]
    public async Task SeveralMinuteOutageRetriesUntilAvailabilityReturns()
    {
        var delays = new List<TimeSpan>();
        var elapsed = TimeSpan.Zero;
        var attempts = 0;
        await GraphicsRecovery.RunAsync((index, _) => { attempts++; return Task.FromResult(elapsed >= TimeSpan.FromMinutes(5)); }, default,
            delay: (delay, _) => { delays.Add(delay); elapsed += delay; return Task.CompletedTask; });
        Assert.True(attempts > 30);
        Assert.Equal(new[] { 1d, 2, 4, 8, 10 }, delays.Take(5).Select(delay => delay.TotalSeconds));
        Assert.All(delays.Skip(4), delay => Assert.Equal(TimeSpan.FromSeconds(10), delay));
    }

    [Fact]
    public async Task CancellationWhileWaitingPreventsReplacement()
    {
        using var cancellation = new CancellationTokenSource();
        var attempts = 0;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => GraphicsRecovery.RunAsync((_, _) => { attempts++; return Task.FromResult(false); }, cancellation.Token,
            delay: (_, token) => { cancellation.Cancel(); return Task.FromCanceled(token); }));
        Assert.Equal(1, attempts);
    }

    [Fact]
    public async Task RepeatedOutagesStartWithImmediateAttemptAndIgnoreCrashBudgets()
    {
        for (var outage = 0; outage < 10; outage++) {
            var attempts = 0;
            await GraphicsRecovery.RunAsync((index, _) => { Assert.Equal(attempts++, index); return Task.FromResult(index == 8); }, default,
                delay: (_, _) => Task.CompletedTask);
            Assert.Equal(9, attempts);
        }
    }

    [Fact]
    public async Task StartupErrorsRetryAndUnsafeEditorTeardownStops()
    {
        var attempts = 0;
        await GraphicsRecovery.RunAsync((_, _) => {
            if (++attempts < 3) throw new IOException("Graphics runtime unavailable");
            return Task.FromResult(true);
        }, default, delay: (_, _) => Task.CompletedTask);
        Assert.Equal(3, attempts);
        await Assert.ThrowsAsync<EditorGraphicsRestartRequiredException>(() => GraphicsRecovery.RunAsync((_, _) => throw new EditorGraphicsRestartRequiredException(), default));
    }

    [Fact]
    public void NativeFailureMetadataRoundTripsAcrossStartupAndHealth()
    {
        using var json = JsonDocument.Parse("""{"failureKind":1,"failureHresult":-2005270523,"deviceRemovedReason":-2005270522,"adapter":"test GPU","adapterLuid":1234} """);
        var failure = NativeRecorderSession.ReadGraphicsFailure(json.RootElement);
        Assert.Equal(GraphicsFailureKind.DeviceLost, failure!.Kind);
        Assert.Equal(-2005270523, failure.HResult); Assert.Equal(-2005270522, failure.DeviceRemovedReason);
        Assert.Equal("test GPU", failure.AdapterDescription); Assert.Equal("1234", failure.AdapterLuid);
        var health = ReplayCaptureHealth.Unknown() with { GraphicsFailure = failure };
        var ack = new CaptureWorkerStartAck(false, false, "Removed", FailureHealth: health);
        Assert.Equal(failure, JsonSerializer.Deserialize<CaptureWorkerStartAck>(JsonSerializer.Serialize(ack))!.FailureHealth!.GraphicsFailure);
    }

    [Fact]
    public void DeviceRecoveryExplainsBufferReset()
    {
        var health = ReplayCaptureHealth.Unknown() with { State = ReplayCaptureState.Recovering,
            GraphicsFailure = new(GraphicsFailureKind.DeviceLost, -1, -1) };
        var presentation = RecordingPresentation.Resolve(health, true, true);
        Assert.Equal("Waiting for graphics device", presentation.Label);
        Assert.Contains("buffer resets", presentation.Detail);
        var resumed = RecordingPresentation.Resolve(health with { State = ReplayCaptureState.Healthy, StartupPhase = ReplayCaptureStartupPhase.Ready, GraphicsFailure = null, ReplayBufferReset = true }, true, true);
        Assert.Equal("Recording", resumed.Label);
        Assert.Contains("buffer reset after graphics recovery", resumed.Detail);
    }

    [Fact]
    public void InterruptedSavesResolveExactlyOnceAndCannotBeReissued()
    {
        var saves = new OutstandingReplaySaves(); var now = DateTime.UtcNow; var finished = Guid.NewGuid(); var interrupted = Guid.NewGuid();
        Assert.True(saves.Begin(new(finished, now))); Assert.True(saves.Begin(new(interrupted, now)));
        var completed = new ReplaySaveCompleted(finished, "saved.mp4", null, now, now, null, false);
        Assert.True(saves.Complete(completed)); Assert.False(saves.Complete(completed));
        var failed = Assert.Single(saves.Interrupt(now)); Assert.Equal(interrupted, failed.SaveId);
        Assert.False(saves.Complete(failed)); Assert.Empty(saves.Interrupt(now));
        Assert.False(saves.Begin(new(interrupted, now))); Assert.False(saves.Any);
    }
}
