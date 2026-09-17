using System.Runtime.InteropServices;
using ClypDat.App.Services;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using Xunit;

namespace ClypDat.App.Tests;

public sealed class ProcessLoopbackLifetimeTests
{
    [Fact]
    public void TimedOutStopRetainsClientAndPreventsOverlappingRestart()
    {
        var client = new StalledClient();
        using var capture = new ProcessLoopbackWaveIn(client, TimeSpan.FromMilliseconds(20));
        capture.StartRecording();
        Assert.True(client.Entered.Wait(TimeSpan.FromSeconds(5)));
        try
        {
            capture.StopRecording();
            capture.StartRecording();
            Assert.Equal(1, client.Starts);
            capture.Dispose();
            capture.Dispose();
            Assert.Equal(0, client.Releases);
            Assert.Throws<ObjectDisposedException>(capture.StartRecording);
        }
        finally { client.Continue.Set(); }
        Assert.True(client.Released.Wait(TimeSpan.FromSeconds(5)));
        capture.Dispose();
        Assert.Equal(1, client.Releases);
        Assert.Equal(1, client.Stops);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DisposalFromCallbackDoesNotReleaseInsideCaptureCall(bool dataCallback)
    {
        var client = new StalledClient { DeliverPackets = dataCallback };
        using var capture = new ProcessLoopbackWaveIn(client, TimeSpan.FromSeconds(2));
        var callback = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        void DisposeInCallback()
        {
            capture.Dispose();
            callback.TrySetResult(client.Releases);
        }
        if (dataCallback) capture.DataAvailable += (_, _) => DisposeInCallback();
        else capture.RecordingStopped += (_, _) => DisposeInCallback();
        capture.StartRecording();
        Assert.True(client.Entered.Wait(TimeSpan.FromSeconds(5)));
        client.Continue.Set();
        if (!dataCallback) capture.StopRecording();
        Assert.Equal(0, await callback.Task.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.True(client.Released.Wait(TimeSpan.FromSeconds(5)));
        Assert.Equal(1, client.Releases);
        Assert.Equal(1, client.Stops);
    }

    [Fact]
    public void ThrowingFinalCallbackStillStopsAndReleasesClient()
    {
        var client = new StalledClient();
        using var capture = new ProcessLoopbackWaveIn(client, TimeSpan.FromMilliseconds(20));
        capture.RecordingStopped += (_, _) => throw new InvalidOperationException("subscriber failed");
        capture.StartRecording();
        Assert.True(client.Entered.Wait(TimeSpan.FromSeconds(5)));
        capture.Dispose();
        client.Continue.Set();
        Assert.True(client.Released.Wait(TimeSpan.FromSeconds(5)));
        Assert.Equal(1, client.Stops);
        Assert.Equal(1, client.Releases);
    }

    [Fact]
    public void StalledNativeStopAlsoRetainsResources()
    {
        var client = new StalledClient { StallOnStop = true };
        using var capture = new ProcessLoopbackWaveIn(client, TimeSpan.FromMilliseconds(20));
        capture.StartRecording();
        Assert.True(client.Entered.Wait(TimeSpan.FromSeconds(5)));
        client.Continue.Set();
        capture.Dispose();
        Assert.True(client.StopEntered.Wait(TimeSpan.FromSeconds(5)));
        try { Assert.Equal(0, client.Releases); }
        finally { client.StopContinue.Set(); }
        Assert.True(client.Released.Wait(TimeSpan.FromSeconds(5)));
        Assert.Equal(1, client.Releases);
    }

    [Fact]
    public void CompletedStopAllowsSequentialRestart()
    {
        var client = new StalledClient();
        using var capture = new ProcessLoopbackWaveIn(client, TimeSpan.FromSeconds(2));
        capture.StartRecording();
        Assert.True(client.Entered.Wait(TimeSpan.FromSeconds(5)));
        client.Continue.Set();
        capture.StopRecording();
        client.Entered.Reset();
        capture.StartRecording();
        Assert.True(client.Entered.Wait(TimeSpan.FromSeconds(5)));
        capture.StopRecording();
        Assert.Equal(2, client.Starts);
        Assert.Equal(2, client.Stops);
        Assert.Equal(0, client.Releases);
        capture.Dispose();
        Assert.Equal(1, client.Releases);
    }

    [Fact]
    public async Task StalledReleaseDoesNotHoldLifetimeLock()
    {
        var client = new StalledClient { StallOnRelease = true };
        using var capture = new ProcessLoopbackWaveIn(client, TimeSpan.FromMilliseconds(20));
        capture.StartRecording();
        Assert.True(client.Entered.Wait(TimeSpan.FromSeconds(5)));
        capture.Dispose();
        client.Continue.Set();
        Assert.True(client.ReleaseEntered.Wait(TimeSpan.FromSeconds(5)));
        try
        {
            await Task.Run(capture.StopRecording).WaitAsync(TimeSpan.FromSeconds(2));
            await Task.Run(capture.Dispose).WaitAsync(TimeSpan.FromSeconds(2));
        }
        finally { client.ReleaseContinue.Set(); }
        Assert.True(client.Released.Wait(TimeSpan.FromSeconds(5)));
        Assert.Equal(1, client.Releases);
    }

    [Fact]
    public void DisposeBeforeStartReleasesExactlyOnce()
    {
        var client = new StalledClient();
        var capture = new ProcessLoopbackWaveIn(client, TimeSpan.Zero);
        capture.Dispose();
        capture.Dispose();
        Assert.Equal(1, client.Releases);
        Assert.Throws<ObjectDisposedException>(capture.StartRecording);
    }

    // WASAPI process loopback hands back zero-filled device periods without
    // setting AUDCLNT_BUFFERFLAGS_SILENT. Those used to skip the declick and
    // land in the WAV as a step at each edge, which is what made saved game
    // audio crackle.
    [Fact]
    public void UnflaggedSilentPacketIsDeclickedLikeAFlaggedOne()
    {
        const float level = 0.5f;
        using var client = new ScriptedClient(
            ScriptedClient.Packet.Tone(level),
            ScriptedClient.Packet.Zeros(),
            ScriptedClient.Packet.Tone(level));
        using var capture = new ProcessLoopbackWaveIn(client, TimeSpan.FromSeconds(2));
        var emitted = new List<float[]>();
        capture.DataAvailable += (_, e) => emitted.Add(ToSamples(e.Buffer, e.BytesRecorded));
        capture.StartRecording();
        Assert.True(client.Drained.Wait(TimeSpan.FromSeconds(5)));
        capture.StopRecording();

        Assert.Equal(3, emitted.Count);
        var before = emitted[0];
        var hole = emitted[1];
        var after = emitted[2];
        Assert.All(hole, sample => Assert.Equal(0f, sample));
        // Untouched in the middle, ramped at the edge that meets the hole.
        Assert.Equal(level, before[before.Length / 2]);
        Assert.Equal(level, after[after.Length / 2]);
        Assert.True(before[^1] < level, $"packet before the hole was not faded out (ends at {before[^1]})");
        Assert.True(after[0] < level, $"packet after the hole was not faded in (starts at {after[0]})");
    }

    private static float[] ToSamples(byte[] buffer, int bytes)
    {
        var samples = new float[bytes / sizeof(float)];
        Buffer.BlockCopy(buffer, 0, samples, 0, samples.Length * sizeof(float));
        return samples;
    }

    // Replays a fixed packet list through real unmanaged buffers, then reports
    // nothing further so the capture loop parks on WaitForPacket until stopped.
    private sealed class ScriptedClient : IProcessLoopbackClient, IDisposable
    {
        internal sealed record Packet(float[] Samples, AudioClientBufferFlags Flags)
        {
            private const int Frames = 480;
            public static Packet Tone(float level) => new(CreateSamples(level), AudioClientBufferFlags.None);
            // Deliberately unflagged: that is the case this exercises.
            public static Packet Zeros() => new(CreateSamples(0f), AudioClientBufferFlags.None);
            private static float[] CreateSamples(float level) => Enumerable.Repeat(level, Frames * 2).ToArray();
        }

        private readonly Queue<Packet> _packets;
        private readonly List<IntPtr> _allocations = new();
        public readonly ManualResetEventSlim Drained = new();
        public WaveFormat WaveFormat => WaveFormat.CreateIeeeFloatWaveFormat(48000, 2);

        public ScriptedClient(params Packet[] packets) => _packets = new Queue<Packet>(packets);

        public int Start() => 0;
        public int Stop() => 0;
        public int GetNextPacketSize(out int frames)
        {
            frames = _packets.Count > 0 ? _packets.Peek().Samples.Length / 2 : 0;
            if (frames == 0) Drained.Set();
            return 0;
        }

        public int GetBuffer(out IntPtr data, out int frames, out AudioClientBufferFlags flags, out long position, out long qpc)
        {
            var packet = _packets.Dequeue();
            frames = packet.Samples.Length / 2;
            flags = packet.Flags;
            position = 0;
            qpc = 0;
            data = Marshal.AllocHGlobal(packet.Samples.Length * sizeof(float));
            _allocations.Add(data);
            Marshal.Copy(packet.Samples, 0, data, packet.Samples.Length);
            return 0;
        }

        public int ReleaseBuffer(int frames) => 0;
        public bool WaitForPacket(int timeoutMs)
        {
            if (_packets.Count > 0) return true;
            Thread.Sleep(Math.Min(timeoutMs, 10));
            return false;
        }

        public void Dispose()
        {
            foreach (var allocation in _allocations) Marshal.FreeHGlobal(allocation);
            _allocations.Clear();
        }
    }

    private sealed class StalledClient : IProcessLoopbackClient
    {
        public WaveFormat WaveFormat => WaveFormat.CreateIeeeFloatWaveFormat(48000, 2);
        public readonly ManualResetEventSlim Entered = new();
        public readonly ManualResetEventSlim Continue = new();
        public readonly ManualResetEventSlim Released = new();
        public int Starts, Stops, Releases;
        public bool DeliverPackets, StallOnStop, StallOnRelease;
        public readonly ManualResetEventSlim ReleaseEntered = new();
        public readonly ManualResetEventSlim ReleaseContinue = new();
        public readonly ManualResetEventSlim StopEntered = new();
        public readonly ManualResetEventSlim StopContinue = new();
        public int Start() { Interlocked.Increment(ref Starts); return 0; }
        public int Stop()
        {
            Interlocked.Increment(ref Stops);
            StopEntered.Set();
            if (StallOnStop && !StopContinue.Wait(TimeSpan.FromSeconds(10))) throw new TimeoutException("Test did not unblock Stop.");
            return 0;
        }
        public int GetNextPacketSize(out int frames)
        {
            Entered.Set();
            if (!Continue.Wait(TimeSpan.FromSeconds(10))) throw new TimeoutException("Test did not unblock audio call.");
            frames = DeliverPackets ? 1 : 0;
            return 0;
        }
        public int GetBuffer(out IntPtr data, out int frames, out AudioClientBufferFlags flags, out long position, out long qpc)
        {
            data = IntPtr.Zero; frames = 1; flags = AudioClientBufferFlags.Silent; position = qpc = 0;
            return 0;
        }
        public int ReleaseBuffer(int frames) => 0;
        // The real client blocks here; this fake blocks in GetNextPacketSize so
        // the existing lifetime tests keep their single synchronisation point.
        public bool WaitForPacket(int timeoutMs) => true;
        public void Dispose()
        {
            Interlocked.Increment(ref Releases);
            ReleaseEntered.Set();
            if (StallOnRelease && !ReleaseContinue.Wait(TimeSpan.FromSeconds(10))) throw new TimeoutException("Test did not unblock release.");
            Released.Set();
        }
    }
}
