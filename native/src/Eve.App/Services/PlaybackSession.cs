using LibVLCSharp.Shared;
using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace Eve.App.Services;

public sealed class PlaybackSession : IDisposable
{
    private readonly LibVLC _libVlc;
    private MediaPlayer? _mixedAudioPlayer;
    private Media? _videoMedia;
    private Media? _mixedAudioMedia;
    private string? _mixedAudioPath;
    private bool _mixedAudioIsCached;
    private bool _disposed;

    public PlaybackSession()
    {
        global::LibVLCSharp.Shared.Core.Initialize();
        _libVlc = new LibVLC("--quiet");
        VideoPlayer = new MediaPlayer(_libVlc);
        VideoPlayer.EnableKeyInput = false;
        VideoPlayer.EnableMouseInput = false;
    }

    public MediaPlayer VideoPlayer { get; }
    public TimeSpan Duration => TimeSpan.FromMilliseconds(Math.Max(0, VideoPlayer.Length));
    public TimeSpan Position => TimeSpan.FromMilliseconds(Math.Max(0, VideoPlayer.Time));
    public bool IsPlaying => VideoPlayer.IsPlaying;

    public static void WarmUp()
    {
        global::LibVLCSharp.Shared.Core.Initialize();
        using var libVlc = new LibVLC("--quiet");
    }

    public async Task LoadAsync(string path, IReadOnlyList<AudioPreviewTrack> audioTracks, CancellationToken cancellationToken)
    {
        LoadVideo(path);
        await LoadAudioAsync(path, audioTracks, cancellationToken);
    }

    public void LoadVideo(string path)
    {
        Stop();
        DisposeMedia();
        DisposeMixedAudio();
        _videoMedia = new Media(_libVlc, new Uri(path));
        VideoPlayer.Media = _videoMedia;
        VideoPlayer.Mute = true;
    }

    public async Task LoadAudioAsync(string path, IReadOnlyList<AudioPreviewTrack> audioTracks, CancellationToken cancellationToken)
    {
        DisposeMixedAudio();
        if (audioTracks.Count == 0) return;

        _mixedAudioPath = await CreateMixedAudioPreviewAsync(path, audioTracks, cancellationToken);
        _mixedAudioIsCached = true;
        _mixedAudioMedia = new Media(_libVlc, new Uri(_mixedAudioPath));
        _mixedAudioPlayer = new MediaPlayer(_mixedAudioMedia)
        {
            EnableKeyInput = false,
            EnableMouseInput = false,
            Mute = false,
            Volume = 100
        };
    }

    public void Play()
    {
        VideoPlayer.Play();
        PlayMixedAudioIfReady();
    }

    public void PlayFrom(TimeSpan time)
    {
        var milliseconds = Math.Max(0, (long)time.TotalMilliseconds);
        if (VideoPlayer.State is VLCState.Ended or VLCState.Stopped)
        {
            VideoPlayer.Stop();
            _mixedAudioPlayer?.Stop();
        }

        VideoPlayer.Play();
        VideoPlayer.Time = milliseconds;
        PlayMixedAudioIfReady();
        Seek(time);
    }

    public void Pause()
    {
        VideoPlayer.Pause();
        _mixedAudioPlayer?.Pause();
    }

    public void Stop()
    {
        VideoPlayer.Stop();
        _mixedAudioPlayer?.Stop();
    }

    public void Seek(TimeSpan time)
    {
        var milliseconds = Math.Max(0, (long)time.TotalMilliseconds);
        VideoPlayer.Time = milliseconds;
        if (_mixedAudioPlayer is not null) _mixedAudioPlayer.Time = milliseconds;
    }

    public void SetTrackVolume(int streamIndex, double percent)
    {
        // Preview audio is pre-mixed with per-track volumes to keep playback stable.
    }

    public void SyncAudioStreams()
    {
        if (_mixedAudioPlayer is null) return;
        var videoTime = VideoPlayer.Time;
        if (Math.Abs(_mixedAudioPlayer.Time - videoTime) > 150)
        {
            _mixedAudioPlayer.Time = videoTime;
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Stop();
        VideoPlayer.Dispose();
        DisposeMixedAudio();
        DisposeMedia();
        _libVlc.Dispose();
    }

    private void DisposeMedia()
    {
        _videoMedia?.Dispose();
        _videoMedia = null;
    }

    private void DisposeMixedAudio()
    {
        _mixedAudioPlayer?.Dispose();
        _mixedAudioPlayer = null;
        _mixedAudioMedia?.Dispose();
        _mixedAudioMedia = null;
        if (_mixedAudioPath is not null)
        {
            try
            {
                if (!_mixedAudioIsCached) File.Delete(_mixedAudioPath);
            }
            catch
            {
                // Best-effort cleanup only.
            }

            _mixedAudioPath = null;
            _mixedAudioIsCached = false;
        }
    }

    private static int VolumeCurve(double percent)
    {
        var clamped = Math.Clamp(percent, 0, 150);
        var volume = clamped <= 100
            ? Math.Sqrt(clamped / 100) * 100
            : clamped;
        return (int)Math.Clamp(Math.Round(volume), 0, 150);
    }

    private static async Task<string> CreateMixedAudioPreviewAsync(
        string inputPath,
        IReadOnlyList<AudioPreviewTrack> audioTracks,
        CancellationToken cancellationToken)
    {
        var tempDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "EVE",
            "preview-audio");
        Directory.CreateDirectory(tempDir);
        var outputPath = Path.Combine(tempDir, $"{AudioCacheKey(inputPath, audioTracks)}.wav");
        if (File.Exists(outputPath)) return outputPath;

        var pendingPath = Path.Combine(tempDir, $"{Guid.NewGuid():N}.wav");
        var labels = Enumerable.Range(0, audioTracks.Count).Select(index => $"[a{index}]").ToArray();
        var filters = audioTracks.Select((track, index) =>
            $"[0:a:{index}]volume={Math.Clamp(track.VolumePercent / 100, 0, 1.5).ToString("0.###", CultureInfo.InvariantCulture)}[a{index}]")
            .Concat(new[] { $"{string.Concat(labels)}amix=inputs={audioTracks.Count}:duration=longest:normalize=1,alimiter=limit=0.95[aout]" });

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
            "-i", inputPath,
            "-filter_complex", string.Join(";", filters),
            "-map", "[aout]",
            "-vn",
            "-ac", "2",
            "-ar", "48000",
            "-f", "wav",
            pendingPath
        })
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = Process.Start(startInfo) ?? throw new InvalidOperationException("Failed to start ffmpeg.");
        var outputTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var errorTask = process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken);
        if (process.ExitCode != 0)
        {
            var error = await errorTask;
            throw new InvalidOperationException(string.IsNullOrWhiteSpace(error) ? "ffmpeg failed to create preview audio." : error);
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

    private void PlayMixedAudioIfReady()
    {
        if (_mixedAudioPlayer is null) return;
        _mixedAudioPlayer.Play();
        _mixedAudioPlayer.Time = VideoPlayer.Time;
    }

    public void SyncAndPlayMixedAudio()
    {
        PlayMixedAudioIfReady();
    }

    private static string AudioCacheKey(string inputPath, IReadOnlyList<AudioPreviewTrack> audioTracks)
    {
        var info = new FileInfo(inputPath);
        var input = string.Join(
            "|",
            inputPath,
            info.Exists ? info.Length : 0,
            info.Exists ? info.LastWriteTimeUtc.Ticks : 0,
            string.Join(",", audioTracks.Select(track => $"{track.StreamIndex}:{track.VolumePercent:0.###}")));
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(input)))[..24].ToLowerInvariant();
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
}

public sealed record AudioPreviewTrack(int StreamIndex, double VolumePercent);
