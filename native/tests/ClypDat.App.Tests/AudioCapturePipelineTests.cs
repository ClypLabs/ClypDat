using ClypDat.App.Services;
using ClypDat.Core.Settings;
using Xunit;

namespace ClypDat.App.Tests;

public sealed class AudioCapturePipelineTests
{
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
}
