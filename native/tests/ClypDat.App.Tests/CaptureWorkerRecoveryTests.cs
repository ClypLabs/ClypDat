using System.IO.Pipes;
using System.Reflection;
using System.Text.Json;
using ClypDat.App.Services;
using ClypDat.Capture.Abstractions;
using Xunit;

namespace ClypDat.App.Tests;

public sealed class CaptureWorkerRecoveryTests
{
    [Fact]
    public async Task RestoreUsesLatestTargetAudioHotkeysAndPauseAfterStartup()
    {
        using var wire = await Wire.CreateAsync();
        var settings = TestReplayConfiguration.Create(@"D:\clips", "MKV") with { GameDisplayName = "Latest target", BitrateMbps = 30 };
        using var proxy = new CaptureWorkerProxy(() => settings);
        Set(proxy, "_pipe", wire.Client); Set(proxy, "_desiredRecording", true);
        Set(proxy, "_recovery", new TaskCompletionSource().Task);
        var read = proxy.ReadLoopAsync(wire.Client, 0);
        await proxy.UpdateHotkeyAsync("Ctrl+Shift+F9"); await proxy.UpdateFullSessionHotkeyAsync("Ctrl+Shift+F10");
        await proxy.UpdateClipGameNameAsync("Latest game");
        await proxy.UpdateAutoClipPolicyAsync("game", true, ["event"]);
        proxy.SetCapturePaused(true); proxy.RequestFrameRate(30);
        var commands = new List<CaptureWorkerEnvelope>();
        var worker = Task.Run(async () => {
            while (await CaptureWorkerPipe.ReadAsync(wire.Server, default) is { } message) {
                commands.Add(message);
                object response = message.Type switch {
                    "attach" => new CaptureWorkerAttachResponse(false, ReplayBufferConfigIdentity.Serialize(settings), ReplayCaptureHealth.Unknown(), []),
                    "start" => new CaptureWorkerStartAck(true, true),
                    _ => new CaptureWorkerAck(true)
                };
                await CaptureWorkerPipe.WriteAsync(wire.Server, "response", message.RequestId, response, default);
            }
        });
        await proxy.RestoreAsync(default).WaitAsync(TimeSpan.FromSeconds(10));
        Assert.True(proxy.IsRecording);
        var attached = commands.Single(message => message.Type == "attach").Payload.GetProperty("Configuration").Deserialize<ReplayBufferConfig>()!;
        Assert.Equal(ReplayBufferConfigIdentity.Serialize(settings), ReplayBufferConfigIdentity.Serialize(attached));
        Assert.Equal("Ctrl+Shift+F9", commands.Single(message => message.Type == "hotkey").Payload.GetProperty("hotkey").GetString());
        Assert.Equal("Ctrl+Shift+F10", commands.Single(message => message.Type == "full-session-hotkey").Payload.GetProperty("hotkey").GetString());
        Assert.True(commands.Single(message => message.Type == "pause").Payload.GetProperty("paused").GetBoolean());
        Assert.True(commands.FindIndex(message => message.Type == "start") < commands.FindIndex(message => message.Type == "pause"));
        Assert.True(commands.FindIndex(message => message.Type == "start") < commands.FindIndex(message => message.Type == "frame-rate"));
        Assert.Equal("Latest game", commands.Single(message => message.Type == "clip-game-name").Payload.GetProperty("gameDisplayName").GetString());
        proxy.Dispose(); await read; await worker;
    }

    [Fact]
    public async Task HungHealthRequestTimesOutAndReleasesPendingRequest()
    {
        using var wire = await Wire.CreateAsync();
        using var proxy = new CaptureWorkerProxy(() => throw new InvalidOperationException());
        Set(proxy, "_pipe", wire.Client);
        var request = proxy.SendAsync<ReplayCaptureHealth>("health", new { }, default);
        Assert.Equal("health", (await CaptureWorkerPipe.ReadAsync(wire.Server, default))!.Type);
        var error = await Assert.ThrowsAsync<IOException>(() => request.WaitAsync(TimeSpan.FromSeconds(8)));
        Assert.Equal("Capture worker health timed out.", error.Message);
        var pending = (System.Collections.IDictionary)typeof(CaptureWorkerProxy).GetField("_pending", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(proxy)!;
        Assert.Empty(pending.Keys.Cast<object>());
    }

    [Fact]
    public void RecoveryStartsFullSessionInUniqueFile()
    {
        var directory = Path.Combine(Path.GetTempPath(), "ClypDat-gpu-recovery-session-" + Guid.NewGuid().ToString("N"));
        try {
            var settings = TestReplayConfiguration.Create(directory, "MKV");
            var interrupted = NativeRecordingAdapter.FullSessionPath(settings); File.WriteAllBytes(interrupted, [1]);
            var resumed = NativeRecordingAdapter.FullSessionPath(settings);
            Assert.NotEqual(interrupted, resumed); Assert.False(File.Exists(resumed)); Assert.Equal(new byte[] { 1 }, File.ReadAllBytes(interrupted));
        } finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }

    [Fact]
    public async Task DeferredStartReplyCannotRearmAfterStop()
    {
        using var transitions = new SemaphoreSlim(1); using var saves = new SemaphoreSlim(1);
        var started = 0;
        var lifecycle = new CaptureLifecycleCoordinator(transitions, saves, () => false,
            _ => { started++; return Task.CompletedTask; }, _ => Task.CompletedTask, (_, _) => { });
        lifecycle.SetAvailability(true); lifecycle.Request(true); lifecycle.Request(false);
        var ack = await CaptureWorkerHost.RequestCaptureAsync(lifecycle, () => false, () => null, default, requestCapture: false);
        Assert.Equal(0, started); Assert.False(lifecycle.Requested); Assert.False(ack.Recording);
    }

    private static void Set(CaptureWorkerProxy proxy, string name, object value) => typeof(CaptureWorkerProxy).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(proxy, value);
    private sealed class Wire(NamedPipeServerStream server, NamedPipeClientStream client) : IDisposable
    {
        internal NamedPipeServerStream Server => server;
        internal NamedPipeClientStream Client => client;
        internal static async Task<Wire> CreateAsync()
        {
            var name = "ClypDat-gpu-recovery-test-" + Guid.NewGuid().ToString("N");
            var server = new NamedPipeServerStream(name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
            var ready = server.WaitForConnectionAsync();
            var client = new NamedPipeClientStream(".", name, PipeDirection.InOut, PipeOptions.Asynchronous);
            await client.ConnectAsync(); await ready; return new(server, client);
        }
        public void Dispose() { client.Dispose(); server.Dispose(); }
    }
}
