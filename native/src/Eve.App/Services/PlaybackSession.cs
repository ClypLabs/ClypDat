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
    private readonly Dictionary<int, string> _audioPaths = new();
    private readonly Dictionary<int, double> _audioVolumes = new();
    private WasapiOut? _audioOutput;
    private MixingSampleProvider? _audioMixer;
    private Media? _videoMedia;
    private bool _disposed;
    private bool _ended;
    private bool _isSeeking;
    private bool _shouldPlay;
    private long _seekVersion;
    private TimeSpan _lastRequestedPosition = TimeSpan.Zero;
    private readonly SemaphoreSlim _seekLock = new(1, 1);
    private readonly object _transportLock = new();

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
    public TimeSpan Position
    {
        get
        {
            var time = VideoPlayer.Time;
            return time > 0
                ? TimeSpan.FromMilliseconds(time)
                : _lastRequestedPosition;
        }
    }
    public bool IsPlaying => VideoPlayer.IsPlaying;
    public bool IsEnded => _ended || VideoPlayer.State == VLCState.Ended;
    public bool IsSeeking => _isSeeking;

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
        _lastRequestedPosition = TimeSpan.Zero;
        _videoMedia = new Media(_libVlc, new Uri(path));
        _videoMedia.AddOption(":no-audio");
        _videoMedia.AddOption(":avcodec-hw=d3d11va");
        VideoPlayer.Media = _videoMedia;
        VideoPlayer.Mute = true;
        VideoPlayer.Volume = 0;
    }

    public async Task LoadAudioAsync(string path, IReadOnlyList<AudioPreviewTrack> audioTracks, CancellationToken cancellationToken)
    {
        DisposeAudioOutput();
        _audioPaths.Clear();
        if (audioTracks.Count == 0) return;

        foreach (var track in audioTracks)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var audioPath = await ExtractAudioTrackAsync(path, track.StreamIndex, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            if (!IsUsableAudioCache(audioPath))
            {
                AppLog.Info($"Editor audio cache invalid after extract: stream={track.StreamIndex}, path={audioPath}.");
                TryDelete(audioPath);
                audioPath = await ExtractAudioTrackAsync(path, track.StreamIndex, cancellationToken);
            }
            _audioPaths[track.StreamIndex] = audioPath;
            _audioVolumes.TryAdd(track.StreamIndex, track.VolumePercent);
        }

        AppLog.Info($"Editor audio loaded: streams={string.Join(",", _audioPaths.Keys.OrderBy(key => key))}, volumes={string.Join(",", _audioVolumes.OrderBy(pair => pair.Key).Select(pair => $"{pair.Key}:{pair.Value:0}%"))}.");
        RebuildAudioOutput();
    }

    private void RebuildAudioOutput()
    {
        DisposeAudioOutput();
        if (_audioPaths.Count == 0) return;

        var providers = new List<ISampleProvider>();
        foreach (var (streamIndex, audioPath) in _audioPaths)
        {
            var reader = new AudioFileReader(audioPath);
            var volume = new VolumeSampleProvider(reader)
            {
                Volume = VolumeCurve(_audioVolumes.GetValueOrDefault(streamIndex, 100))
            };
            _audioSources[streamIndex] = new AudioTrackSource(reader, volume);
            providers.Add(volume);
        }

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
        AppLog.Info($"Editor audio output ready: streams={string.Join(",", _audioSources.Keys.OrderBy(key => key))}.");
    }

    public void Play()
    {
        PlayFrom(Position);
    }

    public void PlayFrom(TimeSpan time)
    {
        var milliseconds = Math.Max(0, (long)time.TotalMilliseconds);
        var wasStoppedOrEnded = IsEnded || VideoPlayer.State == VLCState.Stopped;
        AppLog.Info($"Editor play from requested={time.TotalSeconds:0.###}s, vlc={VideoPlayer.Time / 1000d:0.###}s, state={VideoPlayer.State}, ended={IsEnded}.");
        if (wasStoppedOrEnded)
        {
            VideoPlayer.Stop();
            RebuildAudioOutput();
        }

        _ended = false;
        _shouldPlay = true;
        _lastRequestedPosition = TimeSpan.FromMilliseconds(milliseconds);
        ForceVideoSilent();

        // A simple resume-from-pause is already sitting at this position; forcing
        // VideoPlayer.Time here makes VLC redo a full keyframe seek/rebuffer for no
        // reason, which is what causes the video to freeze on unpause.
        var needsSeek = wasStoppedOrEnded || Math.Abs(VideoPlayer.Time - milliseconds) > 150;
        lock (_transportLock)
        {
            if (needsSeek)
            {
                // LibVLC silently ignores a .Time assignment made before the player
                // has actually started (state still NothingSpecial) - confirmed via
                // logs showing the value bounce right back to 0. Play() must happen
                // first so the seek actually takes.
                VideoPlayer.Play();
                VideoPlayer.Time = milliseconds;
            }
            EnsureAudioOutputCanSeek(time);
            if (needsSeek) SeekAudio(time);
            VideoPlayer.Play();
            VideoPlayer.SetPause(false);
            _audioOutput?.Play();
        }
        AppLog.Info($"Editor play from requested={time.TotalSeconds:0.###}s (seek={needsSeek}), vlc after={VideoPlayer.Time / 1000d:0.###}s, state={VideoPlayer.State}.");
    }

    public void Pause()
    {
        _shouldPlay = false;
        lock (_transportLock)
        {
            _audioOutput?.Stop();
            VideoPlayer.SetPause(true);
        }
        AppLog.Info($"Editor pause at {Position.TotalSeconds:0.###}s.");
    }

    public void Stop()
    {
        try
        {
            lock (_transportLock)
            {
                _audioOutput?.Stop();
                VideoPlayer.Stop();
            }
            _ended = false;
            _shouldPlay = false;
        }
        catch (Exception error)
        {
            AppLog.Error("Editor stop failed", error);
        }
    }

    public void Seek(TimeSpan time, bool resumePlayback = false)
    {
        SeekAsync(time, resumePlayback).GetAwaiter().GetResult();
    }

    public async Task<bool> SeekAsync(TimeSpan time, bool resumePlayback = false, CancellationToken cancellationToken = default)
    {
        var seekVersion = Interlocked.Increment(ref _seekVersion);
        await _seekLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        if (seekVersion != Interlocked.Read(ref _seekVersion))
        {
            // Superseded by a newer seek while queued behind the lock - bail out
            // before touching VLC at all, instead of issuing a now-stale seek that
            // would just interrupt the newer one's in-flight decode.
            _seekLock.Release();
            return false;
        }
        var milliseconds = Math.Max(0, (long)time.TotalMilliseconds);
        var requested = TimeSpan.FromMilliseconds(milliseconds);
        _ended = false;
        _isSeeking = true;
        _shouldPlay = resumePlayback;
        _lastRequestedPosition = requested;
        var resumed = false;
        try
        {
            lock (_transportLock)
            {
                _audioOutput?.Stop();
                ForceVideoSilent();
                if (!VideoPlayer.IsPlaying) VideoPlayer.Play();
                VideoPlayer.SetPause(false);
            }
            AppLog.Info($"Editor seek begin: requested={requested.TotalSeconds:0.###}s, vlc={VideoPlayer.Time / 1000d:0.###}s, state={VideoPlayer.State}, resume={resumePlayback}, version={seekVersion}.");
            if (seekVersion != Interlocked.Read(ref _seekVersion)) return false;
            var videoReady = await SeekAndWaitAsync(requested, cancellationToken).ConfigureAwait(false);
            if (seekVersion != Interlocked.Read(ref _seekVersion)) return false;
            var settledTime = Position;
            lock (_transportLock)
            {
                EnsureAudioOutputCanSeek(requested);
                SeekAudio(requested);
                if (resumePlayback && videoReady)
                {
                    VideoPlayer.SetPause(false);
                    SeekAudio(Position);
                    _audioOutput?.Play();
                    resumed = true;
                }
                else
                {
                    _shouldPlay = false;
                    _audioOutput?.Stop();
                    VideoPlayer.SetPause(true);
                }
            }

            AppLog.Info($"Editor seek end: requested={requested.TotalSeconds:0.###}s, settled={settledTime.TotalSeconds:0.###}s, vlc={VideoPlayer.Time / 1000d:0.###}s, state={VideoPlayer.State}, resume={resumePlayback}, resumed={resumed}, version={seekVersion}.");
            return !resumePlayback || resumed;
        }
        finally
        {
            _isSeeking = false;
            _seekLock.Release();
        }
    }

    public void EnsurePlayingIfNeeded(bool shouldPlay)
    {
        if (!shouldPlay) return;
        _shouldPlay = true;
        ForceVideoSilent();
        lock (_transportLock)
        {
            if (!VideoPlayer.IsPlaying) VideoPlayer.Play();
            VideoPlayer.SetPause(false);
            if (_audioOutput is not null && _audioOutput.PlaybackState != PlaybackState.Playing) _audioOutput.Play();
        }
    }

    public void SetTrackVolume(int streamIndex, double percent)
    {
        _audioVolumes[streamIndex] = percent;
        if (_audioSources.TryGetValue(streamIndex, out var source))
        {
            source.Volume.Volume = VolumeCurve(percent);
            AppLog.Info($"Editor volume changed: stream={streamIndex}, percent={percent:0}%, found=True.");
        }
        else
        {
            AppLog.Info($"Editor volume changed: stream={streamIndex}, percent={percent:0}%, found=False, loaded={string.Join(",", _audioSources.Keys.OrderBy(key => key))}.");
        }
    }

    public void SyncAudioStreams()
    {
        if (_isSeeking || !_shouldPlay || _audioOutput is null || !VideoPlayer.IsPlaying) return;
        lock (_transportLock)
        {
            if (_audioOutput is not null && _audioOutput.PlaybackState != PlaybackState.Playing)
            {
                _audioOutput.Play();
            }
        }
    }

    public void SyncAndPlayMixedAudio()
    {
        lock (_transportLock)
        {
            if (_audioOutput is null) return;
            SeekAudio(Position);
            if (_shouldPlay && VideoPlayer.IsPlaying)
            {
                _audioOutput.Play();
            }
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
        _seekLock.Dispose();
    }

    private async Task<bool> SeekAndWaitAsync(TimeSpan target, CancellationToken cancellationToken)
    {
        var targetMs = target.TotalMilliseconds;
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void OnTimeChanged(object? sender, MediaPlayerTimeChangedEventArgs args)
        {
            if (Math.Abs(args.Time - targetMs) < 650)
            {
                ready.TrySetResult();
            }
        }

        // Subscribe before issuing the seek, not after: LibVLC's TimeChanged can fire
        // (on its own thread) before this method gets around to attaching a handler,
        // which was silently swallowing the confirmation and forcing a false timeout
        // even though the seek had actually landed correctly.
        VideoPlayer.TimeChanged += OnTimeChanged;
        try
        {
            lock (_transportLock)
            {
                VideoPlayer.Time = (long)targetMs;
            }

            try
            {
                await ready.Task.WaitAsync(TimeSpan.FromMilliseconds(900), cancellationToken).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                // The confirmation event may still have been missed even with the
                // race closed (e.g. a very long keyframe seek). Fall back to the
                // actual current position instead of unconditionally treating this
                // as failure - failure here meant "resume" silently turned into
                // "pause" even when the seek genuinely succeeded.
                if (Math.Abs(VideoPlayer.Time - targetMs) >= 650)
                {
                    AppLog.Info($"Editor video seek settle timeout: target={target.TotalSeconds:0.###}s, actual={Position.TotalSeconds:0.###}s, playing={VideoPlayer.IsPlaying}.");
                    return false;
                }
                AppLog.Info($"Editor video seek settle timeout but position already matches: target={target.TotalSeconds:0.###}s, actual={Position.TotalSeconds:0.###}s.");
            }

            _lastRequestedPosition = TimeSpan.FromMilliseconds(Math.Max(0, VideoPlayer.Time));
            return true;
        }
        finally
        {
            VideoPlayer.TimeChanged -= OnTimeChanged;
        }
    }

    private void ForceVideoSilent()
    {
        VideoPlayer.Mute = true;
        VideoPlayer.Volume = 0;
    }

    private void SeekAudio(TimeSpan time)
    {
        _lastRequestedPosition = time < TimeSpan.Zero ? TimeSpan.Zero : time;
        foreach (var source in _audioSources.Values)
        {
            source.Reader.CurrentTime = time < TimeSpan.Zero ? TimeSpan.Zero : time;
        }
    }

    private void EnsureAudioOutputCanSeek(TimeSpan target)
    {
        if (_audioPaths.Count == 0) return;
        if (_audioOutput is null)
        {
            RebuildAudioOutput();
            return;
        }

        var targetBeforeEnd = _audioSources.Values.Any(source => target < source.Reader.TotalTime - TimeSpan.FromMilliseconds(100));
        if (!targetBeforeEnd) return;

        var anyReaderAtEnd = _audioSources.Values.Any(source => source.Reader.Position >= source.Reader.Length);
        if (!anyReaderAtEnd) return;

        AppLog.Info("Editor audio output reached EOF; rebuilding before replay.");
        RebuildAudioOutput();
    }

    private void DisposeMedia()
    {
        _videoMedia?.Dispose();
        _videoMedia = null;
    }

    private void DisposeAudio()
    {
        DisposeAudioOutput();
        _audioPaths.Clear();
        _audioVolumes.Clear();
    }

    private void DisposeAudioOutput()
    {
        lock (_transportLock)
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
        if (IsUsableAudioCache(outputPath)) return outputPath;
        TryDelete(outputPath);

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

    private static bool IsUsableAudioCache(string path)
    {
        try
        {
            if (!File.Exists(path) || new FileInfo(path).Length < 4096) return false;
            using var reader = new WaveFileReader(path);
            return reader.TotalTime > TimeSpan.FromMilliseconds(250);
        }
        catch
        {
            return false;
        }
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
