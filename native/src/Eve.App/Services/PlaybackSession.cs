using LibVLCSharp.Shared;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;

namespace Eve.App.Services;

public sealed class PlaybackSession : IDisposable
{
    private readonly LibVLC _libVlc;
    private readonly Dictionary<int, AudioTrackSource> _audioSources = new();
    private WasapiOut? _audioOutput;
    private MixingSampleProvider? _audioMixer;
    private Media? _videoMedia;
    private bool _disposed;
    private bool _ended;

    public PlaybackSession()
    {
        global::LibVLCSharp.Shared.Core.Initialize();
        _libVlc = new LibVLC("--quiet");
        VideoPlayer = new MediaPlayer(_libVlc);
        VideoPlayer.EnableKeyInput = false;
        VideoPlayer.EnableMouseInput = false;
        VideoPlayer.EndReached += (_, _) => _ended = true;
    }

    public MediaPlayer VideoPlayer { get; }
    public TimeSpan Duration => TimeSpan.FromMilliseconds(Math.Max(0, VideoPlayer.Length));
    public TimeSpan Position => TimeSpan.FromMilliseconds(Math.Max(0, VideoPlayer.Time));
    public bool IsPlaying => VideoPlayer.IsPlaying;
    public bool IsEnded => _ended || VideoPlayer.State == VLCState.Ended;

    public static void WarmUp()
    {
        global::LibVLCSharp.Shared.Core.Initialize();
        using var libVlc = new LibVLC("--quiet");
    }

    public void LoadVideo(string path)
    {
        Stop();
        DisposeMedia();
        DisposeAudio();
        _ended = false;
        _videoMedia = new Media(_libVlc, new Uri(path));
        VideoPlayer.Media = _videoMedia;
        VideoPlayer.Mute = true;
    }

    public async Task LoadAudioAsync(string path, IReadOnlyList<AudioPreviewTrack> audioTracks, CancellationToken cancellationToken)
    {
        DisposeAudio();
        if (audioTracks.Count == 0) return;

        var providers = new List<ISampleProvider>();
        foreach (var track in audioTracks)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var audioPath = await ExtractAudioTrackAsync(path, track.StreamIndex, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();

            var reader = new AudioFileReader(audioPath);
            var volume = new VolumeSampleProvider(reader)
            {
                Volume = VolumeCurve(track.VolumePercent)
            };
            _audioSources[track.StreamIndex] = new AudioTrackSource(reader, volume);
            providers.Add(volume);
        }

        if (providers.Count == 0) return;
        _audioMixer = new MixingSampleProvider(WaveFormat.CreateIeeeFloatWaveFormat(48000, 2))
        {
            ReadFully = true
        };

        foreach (var provider in providers)
        {
            _audioMixer.AddMixerInput(provider);
        }

        var normalized = new GainSampleProvider(_audioMixer, 1f / Math.Max(1, providers.Count));
        var limited = new SoftLimiterSampleProvider(normalized);
        _audioOutput = new WasapiOut(AudioClientShareMode.Shared, false, 120);
        _audioOutput.Init(limited);
    }

    public void Play()
    {
        PlayFrom(Position);
    }

    public void PlayFrom(TimeSpan time)
    {
        var milliseconds = Math.Max(0, (long)time.TotalMilliseconds);
        if (IsEnded || VideoPlayer.State == VLCState.Stopped)
        {
            VideoPlayer.Stop();
            _audioOutput?.Stop();
        }

        _ended = false;
        SeekAudio(time);
        VideoPlayer.Play();
        VideoPlayer.Time = milliseconds;
        _audioOutput?.Play();
    }

    public void Pause()
    {
        VideoPlayer.Pause();
        _audioOutput?.Pause();
    }

    public void Stop()
    {
        VideoPlayer.Stop();
        _audioOutput?.Stop();
        _ended = false;
    }

    public void Seek(TimeSpan time)
    {
        var milliseconds = Math.Max(0, (long)time.TotalMilliseconds);
        _ended = false;
        VideoPlayer.Time = milliseconds;
        SeekAudio(time);
    }

    public void SetTrackVolume(int streamIndex, double percent)
    {
        if (_audioSources.TryGetValue(streamIndex, out var source))
        {
            source.Volume.Volume = VolumeCurve(percent);
        }
    }

    public void SyncAudioStreams()
    {
        if (_audioOutput is null || !VideoPlayer.IsPlaying) return;
        if (_audioOutput.PlaybackState != PlaybackState.Playing)
        {
            _audioOutput.Play();
        }
    }

    public void SyncAndPlayMixedAudio()
    {
        if (_audioOutput is null) return;
        SeekAudio(Position);
        if (VideoPlayer.IsPlaying)
        {
            _audioOutput.Play();
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Stop();
        VideoPlayer.Dispose();
        DisposeAudio();
        DisposeMedia();
        _libVlc.Dispose();
    }

    private void SeekAudio(TimeSpan time)
    {
        foreach (var source in _audioSources.Values)
        {
            source.Reader.CurrentTime = time < TimeSpan.Zero ? TimeSpan.Zero : time;
        }
    }

    private void DisposeMedia()
    {
        _videoMedia?.Dispose();
        _videoMedia = null;
    }

    private void DisposeAudio()
    {
        _audioOutput?.Stop();
        _audioOutput?.Dispose();
        _audioOutput = null;
        _audioMixer = null;

        foreach (var source in _audioSources.Values)
        {
            source.Reader.Dispose();
        }

        _audioSources.Clear();
    }

    private static float VolumeCurve(double percent)
    {
        return (float)Math.Clamp(percent / 100d, 0, 1.5);
    }

    private static async Task<string> ExtractAudioTrackAsync(
        string inputPath,
        int streamIndex,
        CancellationToken cancellationToken)
    {
        var tempDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "EVE",
            "preview-audio");
        Directory.CreateDirectory(tempDir);
        var outputPath = Path.Combine(tempDir, $"{AudioCacheKey(inputPath, streamIndex)}.wav");
        if (File.Exists(outputPath)) return outputPath;

        var pendingPath = Path.Combine(tempDir, $"{Guid.NewGuid():N}.wav");
        var startInfo = new ProcessStartInfo("ffmpeg")
        {
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        foreach (var argument in new[]
        {
            "-y",
            "-v", "error",
            "-i", inputPath,
            "-map", $"0:{streamIndex}",
            "-vn",
            "-sn",
            "-ac", "2",
            "-ar", "48000",
            "-c:a", "pcm_s16le",
            pendingPath
        })
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = Process.Start(startInfo) ?? throw new InvalidOperationException("Failed to start ffmpeg.");
        var outputTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var errorTask = process.StandardError.ReadToEndAsync(cancellationToken);
        try
        {
            await process.WaitForExitAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            TryKill(process);
            TryDelete(pendingPath);
            throw;
        }

        if (process.ExitCode != 0)
        {
            var error = await errorTask;
            TryDelete(pendingPath);
            throw new InvalidOperationException(string.IsNullOrWhiteSpace(error) ? "ffmpeg failed to extract preview audio." : error);
        }

        await outputTask;
        if (!File.Exists(outputPath))
        {
            File.Move(pendingPath, outputPath);
        }
        else
        {
            TryDelete(pendingPath);
        }

        return outputPath;
    }

    private static string AudioCacheKey(string inputPath, int streamIndex)
    {
        var info = new FileInfo(inputPath);
        var input = string.Join(
            "|",
            inputPath,
            streamIndex,
            info.Exists ? info.Length : 0,
            info.Exists ? info.LastWriteTimeUtc.Ticks : 0);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(input)))[..24].ToLowerInvariant();
    }

    private static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited) process.Kill(true);
        }
        catch
        {
            // Best-effort cancellation.
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch
        {
            // Best-effort cache cleanup.
        }
    }

    private sealed record AudioTrackSource(AudioFileReader Reader, VolumeSampleProvider Volume);

    private sealed class SoftLimiterSampleProvider : ISampleProvider
    {
        private readonly ISampleProvider _source;

        public SoftLimiterSampleProvider(ISampleProvider source)
        {
            _source = source;
            WaveFormat = source.WaveFormat;
        }

        public WaveFormat WaveFormat { get; }

        public int Read(float[] buffer, int offset, int count)
        {
            var read = _source.Read(buffer, offset, count);
            for (var index = offset; index < offset + read; index++)
            {
                var sample = buffer[index];
                var magnitude = MathF.Abs(sample);
                if (magnitude <= 0.95f)
                {
                    continue;
                }

                var limited = 0.95f + ((magnitude - 0.95f) / (1f + magnitude - 0.95f)) * 0.05f;
                buffer[index] = MathF.CopySign(limited, sample);
            }

            return read;
        }
    }

    private sealed class GainSampleProvider : ISampleProvider
    {
        private readonly ISampleProvider _source;
        private readonly float _gain;

        public GainSampleProvider(ISampleProvider source, float gain)
        {
            _source = source;
            _gain = gain;
            WaveFormat = source.WaveFormat;
        }

        public WaveFormat WaveFormat { get; }

        public int Read(float[] buffer, int offset, int count)
        {
            var read = _source.Read(buffer, offset, count);
            for (var index = offset; index < offset + read; index++)
            {
                buffer[index] *= _gain;
            }

            return read;
        }
    }
}

public sealed record AudioPreviewTrack(int StreamIndex, double VolumePercent);
