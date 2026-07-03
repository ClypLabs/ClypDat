using LibVLCSharp.Shared;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;

namespace Eve.App.Services;

public sealed class PlaybackSession : IDisposable
{
    private readonly LibVLC _libVlc;
    private readonly Dictionary<int, AudioTrackPlayer> _audioPlayers = new();
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
        DisposeAudioPlayers();
        _ended = false;
        _videoMedia = new Media(_libVlc, new Uri(path));
        VideoPlayer.Media = _videoMedia;
        VideoPlayer.Mute = true;
    }

    public async Task LoadAudioAsync(string path, IReadOnlyList<AudioPreviewTrack> audioTracks, CancellationToken cancellationToken)
    {
        DisposeAudioPlayers();
        foreach (var track in audioTracks)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var audioPath = await ExtractAudioTrackAsync(path, track.StreamIndex, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            var media = new Media(_libVlc, new Uri(audioPath));
            var player = new MediaPlayer(media)
            {
                EnableKeyInput = false,
                EnableMouseInput = false,
                Mute = false,
                Volume = VolumeCurve(track.VolumePercent)
            };
            _audioPlayers[track.StreamIndex] = new AudioTrackPlayer(media, player);
        }
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
            foreach (var audio in _audioPlayers.Values)
            {
                audio.Player.Stop();
            }
        }

        _ended = false;
        VideoPlayer.Play();
        VideoPlayer.Time = milliseconds;
        foreach (var audio in _audioPlayers.Values)
        {
            audio.Player.Play();
            audio.Player.Time = milliseconds;
        }
    }

    public void Pause()
    {
        VideoPlayer.Pause();
        foreach (var audio in _audioPlayers.Values)
        {
            audio.Player.Pause();
        }
    }

    public void Stop()
    {
        VideoPlayer.Stop();
        foreach (var audio in _audioPlayers.Values)
        {
            audio.Player.Stop();
        }
        _ended = false;
    }

    public void Seek(TimeSpan time)
    {
        var milliseconds = Math.Max(0, (long)time.TotalMilliseconds);
        _ended = false;
        VideoPlayer.Time = milliseconds;
        foreach (var audio in _audioPlayers.Values)
        {
            audio.Player.Time = milliseconds;
        }
    }

    public void SetTrackVolume(int streamIndex, double percent)
    {
        if (_audioPlayers.TryGetValue(streamIndex, out var audio))
        {
            audio.Player.Volume = VolumeCurve(percent);
        }
    }

    public void SyncAudioStreams()
    {
        if (!VideoPlayer.IsPlaying) return;
        var videoTime = VideoPlayer.Time;
        foreach (var audio in _audioPlayers.Values)
        {
            if (!audio.Player.IsPlaying)
            {
                audio.Player.Play();
            }

            if (Math.Abs(audio.Player.Time - videoTime) > 150)
            {
                audio.Player.Time = videoTime;
            }
        }
    }

    public void SyncAndPlayMixedAudio()
    {
        foreach (var audio in _audioPlayers.Values)
        {
            audio.Player.Play();
            audio.Player.Time = VideoPlayer.Time;
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Stop();
        VideoPlayer.Dispose();
        DisposeAudioPlayers();
        DisposeMedia();
        _libVlc.Dispose();
    }

    private void DisposeMedia()
    {
        _videoMedia?.Dispose();
        _videoMedia = null;
    }

    private void DisposeAudioPlayers()
    {
        foreach (var audio in _audioPlayers.Values)
        {
            audio.Player.Dispose();
            audio.Media.Dispose();
        }

        _audioPlayers.Clear();
    }

    private static int VolumeCurve(double percent)
    {
        return (int)Math.Clamp(Math.Round(percent), 0, 150);
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

    private sealed record AudioTrackPlayer(Media Media, MediaPlayer Player);
}

public sealed record AudioPreviewTrack(int StreamIndex, double VolumePercent);
