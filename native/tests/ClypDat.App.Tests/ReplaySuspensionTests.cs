using System.IO.Pipes;
using System.Reflection;
using System.Text.Json;
using ClypDat.App.Services;
using ClypDat.Capture.Abstractions;
using Xunit;

namespace ClypDat.App.Tests;

// A display-off or locked-session suspension keeps replay armed. The worker
// holds capture stopped and restarts it once on wake; the app never sends
// start while suspended and never presents the suspension as Replay Off.
public sealed class ReplaySuspensionTests
{
    [Fact]
    public async Task DisplayOffStaysArmedWithoutStartsAndWakeStartsOnce()
    {
        using var harness = new Harness();
        await harness.StartAsync();
        Assert.Equal((1, 1, 0), (harness.StartCommands, harness.Starts, harness.Stops));

        await harness.DisplayAsync(false);
        Assert.Equal(1, harness.Stops);
        Assert.True(harness.Proxy.IsRecording);
        Assert.True(harness.Proxy.IsCaptureSuspended);
        Assert.True(harness.Lifecycle.Suspended);
        var presentation = RecordingPresentation.Resolve(harness.Proxy.GetHealthSnapshot(), harness.Proxy.IsRecording, enabled: true,
            suspended: harness.Proxy.IsCaptureSuspended);
        Assert.Equal("Replay On — Suspended", presentation.Label);
        Assert.Equal("Display unavailable. Capture resumes automatically when it returns.", presentation.Detail);

        // Twenty 3-second detection ticks and reconciles while suspended.
        for (var tick = 0; tick < 20; tick++)
        {
            await harness.TickAsync();
            await harness.Lifecycle.ReconcileAsync(default);
        }
        Assert.Equal((1, 1, 1), (harness.StartCommands, harness.Starts, harness.Stops));
        Assert.True(harness.Proxy.IsRecording);
        Assert.True(harness.Proxy.IsCaptureSuspended);

        await harness.DisplayAsync(true);
        for (var tick = 0; tick < 5; tick++) await harness.TickAsync();
        Assert.Equal((1, 2, 1), (harness.StartCommands, harness.Starts, harness.Stops));
        Assert.True(harness.Proxy.IsRecording);
        Assert.False(harness.Proxy.IsCaptureSuspended);
        // Armed, suspended, resumed: exactly three state changes.
        Assert.Equal(3, harness.StateChanges);
    }

    [Fact]
    public async Task RapidDisplayOffOnOffEndsSuspendedThenResumesOnce()
    {
        using var harness = new Harness();
        await harness.StartAsync();
        await Task.WhenAll(harness.DisplayAsync(false), harness.DisplayAsync(true), harness.DisplayAsync(false)).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(harness.Capturing);
        Assert.True(harness.Proxy.IsRecording);
        Assert.True(harness.Proxy.IsCaptureSuspended);
        var starts = harness.Starts;
        for (var tick = 0; tick < 10; tick++) await harness.TickAsync();
        Assert.Equal(1, harness.StartCommands);
        Assert.Equal(starts, harness.Starts);

        await harness.DisplayAsync(true);
        Assert.True(harness.Capturing);
        Assert.Equal(starts + 1, harness.Starts);
        Assert.False(harness.Proxy.IsCaptureSuspended);
    }

    [Fact]
    public async Task SessionLockSuspendsAndUnlockResumesOnce()
    {
        using var harness = new Harness();
        await harness.StartAsync();
        await harness.SessionAsync(0x7); // WTS_SESSION_LOCK
        for (var tick = 0; tick < 10; tick++) await harness.TickAsync();
        Assert.True(harness.Proxy.IsRecording && harness.Proxy.IsCaptureSuspended);
        Assert.Equal((1, 1, 1), (harness.StartCommands, harness.Starts, harness.Stops));
        await harness.SessionAsync(0x8); // WTS_SESSION_UNLOCK
        Assert.Equal((1, 2, 1), (harness.StartCommands, harness.Starts, harness.Stops));
        Assert.True(harness.Proxy.IsRecording && !harness.Proxy.IsCaptureSuspended);
    }

    [Fact]
    public async Task DisablingWhileSuspendedStaysOffOnWake()
    {
        using var harness = new Harness();
        await harness.StartAsync();
        await harness.DisplayAsync(false);
        await harness.UserStopAsync();
        Assert.False(harness.Proxy.IsRecording);
        Assert.False(harness.Proxy.IsCaptureSuspended);
        Assert.False(harness.Lifecycle.Suspended);
        // A suspension report still in flight cannot re-arm a disabled replay.
        harness.Proxy.HandleRecordingState(harness.Pipe, 0, recording: false, suspended: true);
        Assert.False(harness.Proxy.IsRecording);

        await harness.DisplayAsync(true);
        for (var tick = 0; tick < 5; tick++) await harness.TickAsync(shouldRecord: false);
        Assert.Equal((1, 1, 1), (harness.StartCommands, harness.Starts, harness.Stops));
        Assert.False(harness.Capturing);
        Assert.Equal("Off", RecordingPresentation.Resolve(harness.Proxy.GetHealthSnapshot(), false, enabled: false).Label);
    }

    [Fact]
    public async Task StartWhileDisplayIsOffArmsSuspendedAndStartsOnWake()
    {
        using var harness = new Harness();
        await harness.DisplayAsync(false);
        await harness.StartAsync();
        Assert.Equal((1, 0, 0), (harness.StartCommands, harness.Starts, harness.Stops));
        Assert.True(harness.Proxy.IsRecording && harness.Proxy.IsCaptureSuspended);
        for (var tick = 0; tick < 10; tick++) await harness.TickAsync();
        Assert.Equal(1, harness.StartCommands);
        await harness.DisplayAsync(true);
        Assert.Equal((1, 1, 0), (harness.StartCommands, harness.Starts, harness.Stops));
    }

    [Fact]
    public void ReconnectToSuspendedRequestedWorkerStaysArmed()
    {
        using var harness = new Harness();
        harness.Proxy.ApplyAttach(new CaptureWorkerAttachResponse(false, "identity", Health(), [], Suspended: true),
            TestReplayConfiguration.Create("library", "MKV"), preserveRecording: false);
        Assert.True(harness.Proxy.IsRecording);
        Assert.True(harness.Proxy.IsCaptureSuspended);
        Assert.False(ReplayAutoStart.ShouldStart(true, harness.Proxy, false));
        // The adopted intent keeps later suspension reports armed; wake resumes.
        harness.Proxy.HandleRecordingState(harness.Pipe, 0, recording: false, suspended: true);
        Assert.True(harness.Proxy.IsRecording);
        harness.Proxy.HandleRecordingState(harness.Pipe, 0, recording: true, suspended: false);
        Assert.True(harness.Proxy.IsRecording && !harness.Proxy.IsCaptureSuspended);
        Assert.Equal(0, harness.StartCommands);
    }

    [Fact]
    public void AttachToActiveWorkerIsArmedAndNotSuspended()
    {
        using var harness = new Harness();
        harness.Proxy.ApplyAttach(new CaptureWorkerAttachResponse(true, "identity", Health(), []),
            TestReplayConfiguration.Create("library", "MKV"), preserveRecording: false);
        Assert.True(harness.Proxy.IsRecording);
        Assert.False(harness.Proxy.IsCaptureSuspended);
        Assert.False(ReplayAutoStart.ShouldStart(true, harness.Proxy, false));
    }

    [Fact]
    public async Task SuspendedWorkerReportsSuspensionAndOlderPayloadsStayCompatible()
    {
        using var harness = new Harness();
        await harness.DisplayAsync(false);
        var ack = await CaptureWorkerHost.RequestCaptureAsync(harness.Lifecycle, () => harness.Capturing, () => null, default);
        Assert.True(ack.Accepted && !ack.Recording && ack.Suspended);
        Assert.True(JsonSerializer.Deserialize<CaptureWorkerStartAck>(JsonSerializer.Serialize(ack))!.Suspended);
        var attach = new CaptureWorkerAttachResponse(false, "identity", Health(), [], Suspended: true);
        Assert.True(JsonSerializer.Deserialize<CaptureWorkerAttachResponse>(JsonSerializer.Serialize(attach))!.Suspended);
        // A worker without the field reports an unsuspended state.
        Assert.False(JsonSerializer.Deserialize<CaptureWorkerStartAck>("""{"Accepted":true,"Recording":false}""")!.Suspended);
        // Disabled and unavailable is not a suspension.
        harness.Lifecycle.Request(false);
        Assert.False(harness.Lifecycle.Suspended);
    }

    [Fact]
    public async Task SavingWhileSuspendedIsRejectedWithTheReason()
    {
        using var harness = new Harness();
        await harness.StartAsync();
        await harness.DisplayAsync(false);
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => harness.Proxy.SaveReplayAsync("library"));
        Assert.Equal(CaptureWorkerProxy.SuspendedSaveMessage, error.Message);
    }

    private static ReplayCaptureHealth Health() => ReplayCaptureHealth.Unknown("worker");

    // The worker (real lifecycle coordinator, availability policy and start
    // handler) and the app (real proxy and the UI's auto-start predicate),
    // joined without a pipe: transitions reach the proxy as its read loop
    // delivers them. Counters prove exact start and stop commands.
    private sealed class Harness : IDisposable
    {
        public readonly CaptureWorkerProxy Proxy = new(() => throw new InvalidOperationException("The harness never attaches over a pipe."));
        public readonly NamedPipeClientStream Pipe = new("unused-suspension-worker");
        public readonly CaptureAvailabilityPolicy Availability = new();
        public readonly CaptureLifecycleCoordinator Lifecycle;
        public bool Capturing;
        public int Starts, Stops, StartCommands, StateChanges;

        public Harness()
        {
            Lifecycle = new(new SemaphoreSlim(1, 1), new SemaphoreSlim(1, 1), () => Capturing,
                _ => { Starts++; Capturing = true; return Task.CompletedTask; },
                _ => { Stops++; Capturing = false; return Task.CompletedTask; },
                (recording, suspended) => Proxy.HandleRecordingState(Pipe, 0, recording, suspended));
            typeof(CaptureWorkerProxy).GetField("_pipe", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(Proxy, Pipe);
            Proxy.RecordingStateChanged += (_, _) => StateChanges++;
            Lifecycle.SetAvailability(true);
        }

        // A game-detection tick: MainWindow starts replay only when this says so.
        public async Task TickAsync(bool shouldRecord = true)
        {
            if (ReplayAutoStart.ShouldStart(shouldRecord, Proxy, false)) await StartAsync();
        }

        // CaptureWorkerProxy.StartAsync once attached: arm, and send "start"
        // unless the worker is already armed.
        public async Task StartAsync()
        {
            typeof(CaptureWorkerProxy).GetField("_desiredRecording", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(Proxy, true);
            if (Proxy.IsRecording) return;
            StartCommands++;
            Proxy.ApplyStartAck(await CaptureWorkerHost.RequestCaptureAsync(Lifecycle, () => Capturing, () => null, default));
        }

        public Task DisplayAsync(bool on)
        {
            Lifecycle.SetAvailability(Availability.SetDisplayState(on ? 1u : 0u));
            return Lifecycle.ReconcileAsync(default);
        }

        public Task SessionAsync(int sessionEvent)
        {
            Lifecycle.SetAvailability(Availability.HandleSessionEvent(sessionEvent));
            return Lifecycle.ReconcileAsync(default);
        }

        // A user stop: the proxy disarms, then the worker handles "stop".
        public async Task UserStopAsync()
        {
            await Proxy.StopAsync();
            Lifecycle.Request(false);
            await Lifecycle.ReconcileAsync(default);
        }

        public void Dispose() { Proxy.Dispose(); Pipe.Dispose(); }
    }
}
