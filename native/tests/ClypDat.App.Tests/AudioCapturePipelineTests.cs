using ClypDat.App.Services;
using ClypDat.Core.Settings;
using NAudio.Wave;
using Xunit;

namespace ClypDat.App.Tests;

public sealed class AudioCapturePipelineTests
{
    [Fact]
    public void SnapshotTo_StalledCapture_DoesNotPadToWallTime()
    {
        using var fixture = new TimestampedSessionFixture();
        var start = DateTime.UtcNow;
        fixture.Raise(start, milliseconds: 100);

        var path = fixture.Snapshot(out var lastSampleUtc);

        using var reader = new WaveFileReader(path);
        Assert.InRange((reader.TotalTime - TimeSpan.FromMilliseconds(100)).Duration(), TimeSpan.Zero, TimeSpan.FromMilliseconds(1));
        Assert.InRange((lastSampleUtc - start.AddMilliseconds(100)).Duration(), TimeSpan.Zero, TimeSpan.FromMilliseconds(1));
    }

    [Fact]
    public void TimestampedPacket_AfterSnapshot_OverlappingPrefixIsTrimmed()
    {
        using var fixture = new TimestampedSessionFixture();
        var start = DateTime.UtcNow;
        fixture.Raise(start, milliseconds: 100);
        fixture.Snapshot(out _);

        fixture.Raise(start.AddMilliseconds(50), milliseconds: 100);
        var path = fixture.Snapshot(out var lastSampleUtc);

        using var reader = new WaveFileReader(path);
        Assert.InRange((reader.TotalTime - TimeSpan.FromMilliseconds(150)).Duration(), TimeSpan.Zero, TimeSpan.FromMilliseconds(1));
        Assert.InRange((lastSampleUtc - start.AddMilliseconds(150)).Duration(), TimeSpan.Zero, TimeSpan.FromMilliseconds(1));
    }

    [Fact]
    public void SnapshotPurpose_UsesInteractiveAndArchiveIoPriorities()
    {
        Assert.False(AudioCaptureSession.UsesBackgroundIo(AudioSnapshotPurpose.InteractiveReplay));
        Assert.True(AudioCaptureSession.UsesBackgroundIo(AudioSnapshotPurpose.BackgroundArchive));
    }
    [Fact]
    public void IsSilentWaveFile_DigitalSilence_IsSilent()
    {
        using var fixture = WaveFixture.Create(0f);

        Assert.True(AudioCapturePipeline.IsSilentWaveFile(fixture.Path));
    }

    [Fact]
    public void IsSilentWaveFile_NonZeroSpotifySample_IsActive()
    {
        using var fixture = WaveFixture.Create(0.25f);

        Assert.False(AudioCapturePipeline.IsSilentWaveFile(fixture.Path));
    }

    [Fact]
    public void IsSilentWaveFile_QuietMeaningfulSample_IsActive()
    {
        using var fixture = WaveFixture.Create(0.000002f);

        Assert.False(AudioCapturePipeline.IsSilentWaveFile(fixture.Path));
    }

    [Fact]
    public async Task BuildAlignedTracksAsync_SilentSpotify_OmitsSpotifyButKeepsOtherApps()
    {
        FfmpegPathResolver.EnsureBundledFfmpeg();
        Assert.True(FfmpegPathResolver.IsAvailable, "Bundled FFmpeg is unavailable to audio pipeline tests.");

        var folder = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"clypdat-audio-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(folder);
        var snapshots = new List<string>();
        try
        {
            using var pipeline = new AudioCapturePipeline(folder);
            var config = new ReplayBufferConfig(
                1, 720, 60, 0, 0, 1280, 720,
                "", "", Array.Empty<string>(), Array.Empty<string>(), "", Array.Empty<string>(),
                "", "", "", "", AdditionalAudioProcesses: new Dictionary<string, int>
                {
                    ["Discord"] = 100,
                    ["Spotify"] = 100
                });

            var tracks = await pipeline.BuildAlignedTracksAsync(
                [(DateTime.UtcNow - TimeSpan.FromSeconds(1), 0.1)], config, snapshots, CancellationToken.None);

            Assert.Contains(tracks, track => AudioProcessIdentity.Equals(track.Label, "Discord"));
            Assert.DoesNotContain(tracks, track => AudioProcessIdentity.Equals(track.Label, "Spotify"));
        }
        finally
        {
            foreach (var snapshot in snapshots) AudioCapturePipeline.TryDelete(snapshot);
            try { Directory.Delete(folder, recursive: true); } catch { }
        }
    }

    private sealed class WaveFixture : IDisposable
    {
        private WaveFixture(string path) => Path = path;

        public string Path { get; }

        public static WaveFixture Create(float sample)
        {
            var path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"clypdat-audio-signal-{Guid.NewGuid():N}.wav");
            using (var writer = new NAudio.Wave.WaveFileWriter(path, NAudio.Wave.WaveFormat.CreateIeeeFloatWaveFormat(48_000, 2)))
            {
                writer.WriteSamples([0f, sample], 0, 2);
            }

            return new WaveFixture(path);
        }

        public void Dispose() => AudioCapturePipeline.TryDelete(Path);
    }

    private sealed class TimestampedSessionFixture : IDisposable
    {
        private readonly FakeWaveIn _capture = new();
        private readonly AudioCaptureSession _session;
        private readonly List<string> _paths = new();

        public TimestampedSessionFixture() => _session = AudioCaptureSession.StartInMemory(_capture, "test");

        public void Raise(DateTime startUtc, int milliseconds)
        {
            var bytes = _capture.WaveFormat.AverageBytesPerSecond * milliseconds / 1000;
            _capture.Raise(new byte[bytes], startUtc);
        }

        public string Snapshot(out DateTime lastSampleUtc)
        {
            var path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"clypdat-audio-snapshot-{Guid.NewGuid():N}.wav");
            Assert.True(_session.SnapshotTo(path, null, out lastSampleUtc));
            _paths.Add(path);
            return path;
        }

        public void Dispose()
        {
            _session.Dispose();
            foreach (var path in _paths) AudioCapturePipeline.TryDelete(path);
        }
    }

    private sealed class FakeWaveIn : IWaveIn
    {
        public WaveFormat WaveFormat { get; set; } = WaveFormat.CreateIeeeFloatWaveFormat(48_000, 1);
        public event EventHandler<WaveInEventArgs>? DataAvailable;
        public event EventHandler<StoppedEventArgs>? RecordingStopped;
        public void StartRecording() { }
        public void StopRecording() => RecordingStopped?.Invoke(this, new StoppedEventArgs());
        public void Raise(byte[] bytes, DateTime startUtc) => DataAvailable?.Invoke(this, new TimestampedWaveInEventArgs(bytes, bytes.Length, startUtc));
        public void Dispose() { }
    }
}
