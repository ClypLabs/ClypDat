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
        public void Dispose()
        {
            Interlocked.Increment(ref Releases);
            ReleaseEntered.Set();
            if (StallOnRelease && !ReleaseContinue.Wait(TimeSpan.FromSeconds(10))) throw new TimeoutException("Test did not unblock release.");
            Released.Set();
        }
    }
}
