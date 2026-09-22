using System.Collections.Concurrent;
using ClypDat.App.Services;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using Xunit;

namespace ClypDat.App.Tests;

public sealed class AudioCaptureWriterTests
{
    [Fact]
    public async Task BlockedSnapshotCopyAllowsLaterCaptureWritesWithoutExtendingSnapshot()
    {
        var source = new ManualWaveIn();
        var sourcePath = TempPath();
        var copied = new ControlledStream { Block = true };
        try
        {
            using var session = AudioCaptureSession.StartOn(source,
                new FileStream(sourcePath, FileMode.Create, FileAccess.Write, FileShare.Read),
                "copy stall", snapshotOutput: _ => copied);
            var start = DateTime.UtcNow;
            source.Raise(Packet(1), start);
            var snapshot = Task.Run(() => session.SnapshotTo("injected snapshot stream", null, out _));
            try
            {
                Assert.True(copied.Entered.Wait(TimeSpan.FromSeconds(5)));
                source.Raise(Packet(2), start.AddMilliseconds(100));
                Assert.True(SpinWait.SpinUntil(() => session.BytesWritten == 400, TimeSpan.FromSeconds(5)));
                Assert.False(snapshot.IsCompleted);
            }
            finally { copied.Resume.Set(); }
            Assert.True(await snapshot);
            Assert.Equal(Packet(1), ReadSamples(copied));
        }
        finally { copied.Resume.Set(); File.Delete(sourcePath); }
    }

    [Fact]
    public async Task SnapshotBarrierIncludesEarlierPacketsAndExcludesLaterPackets()
    {
        var source = new ManualWaveIn();
        var output = new ControlledStream();
        using var session = AudioCaptureSession.StartOn(source, output, "snapshot ordering");
        var firstPath = TempPath();
        var secondPath = TempPath();
        var start = DateTime.UtcNow;
        try
        {
            source.Raise(Packet(1), start);
            Assert.True(SpinWait.SpinUntil(() => session.QueuedBytes == 0, TimeSpan.FromSeconds(5)));
            output.BlockFlush = true;
            DateTime boundary = default;
            var snapshot = Task.Run(() => session.SnapshotTo(firstPath, null, out boundary));
            Assert.True(output.FlushEntered.Wait(TimeSpan.FromSeconds(5)));
            source.Raise(Packet(2), start.AddMilliseconds(100));
            output.FlushResume.Set();
            Assert.True(await snapshot);
            Assert.Equal(start.AddMilliseconds(100), boundary);
            Assert.Equal(Packet(1), ReadSamples(firstPath));
            Assert.True(session.SnapshotTo(secondPath, null, out var secondBoundary));
            Assert.Equal(start.AddMilliseconds(200), secondBoundary);
            Assert.Equal(Packet(1).Concat(Packet(2)), ReadSamples(secondPath));
        }
        finally
        {
            output.FlushResume.Set();
            File.Delete(firstPath);
            File.Delete(secondPath);
        }
    }

    [Fact]
    public void QueuedPacketsOwnMutableBuffersAndPreserveGenuineGapsAndSilence()
    {
        var source = new ManualWaveIn();
        var output = new ControlledStream();
        using var session = AudioCaptureSession.StartOn(source, output, "timestamps");
        var start = DateTime.UtcNow;
        output.Block = true;
        try
        {
            var bytes = Packet(1);
            source.Raise(bytes, start);
            Assert.True(output.Entered.Wait(TimeSpan.FromSeconds(5)));
            Array.Fill(bytes, (byte)2);
            source.Raise(bytes, start.AddMilliseconds(100));
            Array.Fill(bytes, (byte)99);
            source.Raise(Packet(0), start.AddMilliseconds(200));
            source.Raise(Packet(3), start.AddMilliseconds(500));
        }
        finally { output.Resume.Set(); }
        session.Dispose();
        Assert.Equal(Packet(1).Concat(Packet(2)).Concat(new byte[600]).Concat(Packet(3)), ReadSamples(output));
        Assert.Equal(start, session.FirstSampleUtc);
        Assert.Equal(1200, session.BytesWritten);
    }

    [Fact]
    public void TenSecondLimitIncludesInFlightWriteAndReportsOverflowWithoutBlocking()
    {
        var source = new ManualWaveIn();
        var output = new ControlledStream();
        using var session = AudioCaptureSession.StartOn(source, output, "overflow");
        output.Block = true;
        var start = DateTime.UtcNow;
        try
        {
            source.Raise(Packet(1), start);
            Assert.True(output.Entered.Wait(TimeSpan.FromSeconds(5)));
            source.Raise(new byte[19_800], start.AddMilliseconds(100));
            Assert.Equal(20_000, session.QueuedBytes);
            source.Raise(Packet(2), start.AddSeconds(10));
            Assert.True(session.Died);
            Assert.Equal(200, session.LostBytes);
            Assert.Equal(20_000, session.QueuedBytes);
        }
        finally { output.Resume.Set(); }
        session.Dispose();
        Assert.Equal(20_000, ReadSamples(output).Length);
    }

    [Fact]
    public async Task WriterExceptionMarksCaptureFailedAndReleasesOwnedResources()
    {
        var source = new ManualWaveIn();
        var output = new ControlledStream();
        using var session = AudioCaptureSession.StartOn(source, output, "write error");
        output.ThrowWrites = true;
        source.Raise(Packet(1), DateTime.UtcNow);
        Assert.True(SpinWait.SpinUntil(() => session.Died, TimeSpan.FromSeconds(5)));
        source.Raise(Packet(2));
        session.Dispose();
        await session.WriterCompletion.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(output.Disposed);
        Assert.Equal(1, source.DisposeCount);
        Assert.Equal(400, session.LostBytes);
    }

    private static string TempPath() => Path.Combine(Path.GetTempPath(), $"clypdat-writer-{Guid.NewGuid():N}.wav");
    private static byte[] Packet(byte value) => Enumerable.Repeat(value, 200).ToArray();
    private static byte[] ReadSamples(ControlledStream output) => ReadSamples(new MemoryStream(output.ToArray()));
    private static byte[] ReadSamples(string path) => ReadSamples(File.OpenRead(path));
    private static byte[] ReadSamples(Stream stream)
    {
        using var ownedStream = stream;
        using var reader = new WaveFileReader(stream);
        var data = new byte[reader.Length];
        reader.ReadExactly(data);
        return data;
    }

    private sealed class ManualWaveIn : IWaveIn
    {
        public bool DeferStopped;
        public int DisposeCount;
        public WaveFormat WaveFormat { get; set; } = new(1000, 16, 1);
        public event EventHandler<WaveInEventArgs>? DataAvailable;
        public event EventHandler<StoppedEventArgs>? RecordingStopped;
        public void StartRecording() { }
        public void StopRecording() { if (!DeferStopped) Finish(); }
        public void Finish() => RecordingStopped?.Invoke(this, new StoppedEventArgs());
        public void Raise(byte[] bytes, DateTime? timestamp = null) => DataAvailable?.Invoke(this,
            timestamp is { } utc ? new TimestampedWaveInEventArgs(bytes, bytes.Length, utc) : new WaveInEventArgs(bytes, bytes.Length));
        public void Dispose() => Interlocked.Increment(ref DisposeCount);
    }

    private sealed class SinglePacketClient : IProcessLoopbackClient
    {
        private int _released;
        public int Disposals;
        public readonly ManualResetEventSlim PacketReleased = new();
        public WaveFormat WaveFormat => new(1000, 16, 1);
        public int Start() => 0;
        public int Stop() => 0;
        public int GetNextPacketSize(out int frames) { frames = _released == 0 ? 100 : 0; return 0; }
        public int GetBuffer(out IntPtr data, out int frames, out AudioClientBufferFlags flags, out long position, out long qpc)
        {
            data = IntPtr.Zero;
            frames = 100;
            flags = AudioClientBufferFlags.Silent;
            position = 0;
            qpc = (long)(System.Diagnostics.Stopwatch.GetTimestamp() * (10_000_000d / System.Diagnostics.Stopwatch.Frequency));
            return 0;
        }
        public int ReleaseBuffer(int frames) { _released = 1; PacketReleased.Set(); return 0; }
        public bool WaitForPacket(int timeoutMs) { Thread.Sleep(Math.Min(timeoutMs, 10)); return _released == 0; }
        public void Dispose() => Interlocked.Increment(ref Disposals);
    }

    [Fact]
    public void BlockedDisk_FiniteCaptureBufferKeepsDrainingAndPreservesSamples()
    {
        using var source = new FiniteWaveIn();
        var output = new ControlledStream();
        using var session = AudioCaptureSession.StartOn(source, output, "finite buffer");
        output.Block = true;
        try
        {
            source.Produce(1);
            Assert.True(output.Entered.Wait(TimeSpan.FromSeconds(5)));
            source.Produce(2);
            source.Produce(3);
            // A two-packet device buffer has to drain before the next burst.
            SpinWait.SpinUntil(() => source.Delivered >= 3, TimeSpan.FromMilliseconds(500));
            source.Produce(4);
            source.Produce(5);
        }
        finally { output.Resume.Set(); }
        source.StopRecording();
        session.Dispose();

        Assert.Equal(0, source.Lost);
        using var reader = new WaveFileReader(new MemoryStream(output.ToArray()));
        var samples = new byte[reader.Length];
        reader.ReadExactly(samples);
        Assert.Equal(Enumerable.Range(1, 5).SelectMany(value => Enumerable.Repeat((byte)value, 200)), samples);
    }

    private sealed class FiniteWaveIn : IWaveIn
    {
        private readonly BlockingCollection<byte[]> _packets = new(2);
        private Thread? _thread;
        public int Delivered, Lost;
        public WaveFormat WaveFormat { get; set; } = new(1000, 16, 1);
        public event EventHandler<WaveInEventArgs>? DataAvailable;
        public event EventHandler<StoppedEventArgs>? RecordingStopped;
        public void Produce(int value)
        {
            if (!_packets.TryAdd(Enumerable.Repeat((byte)value, 200).ToArray())) Lost++;
        }
        public void StartRecording()
        {
            _thread = new Thread(() =>
            {
                var start = DateTime.UtcNow;
                foreach (var bytes in _packets.GetConsumingEnumerable())
                {
                    DataAvailable?.Invoke(this, new TimestampedWaveInEventArgs(bytes, bytes.Length, start.AddMilliseconds(Delivered * 100)));
                    Interlocked.Increment(ref Delivered);
                }
                RecordingStopped?.Invoke(this, new StoppedEventArgs());
            }) { IsBackground = true };
            _thread.Start();
        }
        public void StopRecording()
        {
            _packets.CompleteAdding();
            Assert.True(_thread!.Join(TimeSpan.FromSeconds(5)));
        }
        public void Dispose() => StopRecording();
    }

    private sealed class ControlledStream : MemoryStream
    {
        public bool Block, BlockFlush, ThrowWrites, ThrowFlush, Disposed;
        public readonly ManualResetEventSlim Entered = new();
        public readonly ManualResetEventSlim Resume = new();
        public readonly ManualResetEventSlim FlushEntered = new();
        public readonly ManualResetEventSlim FlushResume = new();
        public override void Write(byte[] buffer, int offset, int count)
        {
            if (ThrowWrites) throw new IOException("Injected writer failure.");
            if (Block)
            {
                Entered.Set();
                if (!Resume.Wait(TimeSpan.FromSeconds(10))) throw new TimeoutException("Test did not resume output.");
            }
            base.Write(buffer, offset, count);
        }
        public override void Flush()
        {
            if (ThrowFlush) throw new IOException("Injected flush failure.");
            if (BlockFlush)
            {
                FlushEntered.Set();
                if (!FlushResume.Wait(TimeSpan.FromSeconds(10))) throw new TimeoutException("Test did not resume flush.");
            }
            base.Flush();
        }
        protected override void Dispose(bool disposing) { Disposed = true; base.Dispose(disposing); }
    }
}
