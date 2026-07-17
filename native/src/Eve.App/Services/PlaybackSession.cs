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
    private readonly List<int> _audioStreamIndexes = new();
    private string _audioInputPath = string.Empty;
    private TimeSpan _audioDuration = TimeSpan.Zero;
    private readonly Dictionary<int, double> _audioVolumes = new();
    private WasapiOut? _audioOutput;
    private MixingSampleProvider? _audioMixer;
    private Media? _videoMedia;
    private bool _disposed;
    private bool _ended;
    private bool _isSeeking;
    private bool _shouldPlay;
    private long _seekVersion;
    private long _playVersion;
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
        // LibVLC already streams windowed around the playhead (it never reads
        // the whole file), but its default read-ahead cache is sized for
        // local disks - on a network drive (UNC path or mapped SMB share) the
        // higher/spikier read latency blows through it and playback stutters.
        // A bigger demux cache absorbs those latency spikes at the cost of a
        // few MB of RAM; local files keep a modest bump over the default.
        var isNetwork = IsNetworkPath(path);
        _videoMedia.AddOption($":file-caching={(isNetwork ? 5000 : 1000)}");
        VideoPlayer.Media = _videoMedia;
        VideoPlayer.Mute = true;
        VideoPlayer.Volume = 0;

        // Network-drive diagnostics: size + storage type up front, so slow
        // opens in the log can immediately be attributed (or not) to the file
        // living on a share.
        long sizeMb = 0;
        try { sizeMb = new FileInfo(path).Length / (1024 * 1024); } catch { }
        AppLog.Info($"Editor video load: network={isNetwork}, sizeMB={sizeMb}, path={path}");
    }

    public static bool IsNetworkPath(string path)
    {
        try
        {
            if (path.StartsWith(@"\\", StringComparison.Ordinal)) return true;
            var root = Path.GetPathRoot(path);
            return !string.IsNullOrEmpty(root) && new DriveInfo(root).DriveType == DriveType.Network;
        }
        catch
        {
            return false;
        }
    }

    // Warms the on-demand audio chunk cache around an arbitrary timeline
    // point (trim handles, markers) so seeking there plays real audio
    // immediately instead of a silent beat while its chunk extracts - called
    // by the editor whenever the user positions something worth jumping to.
    public void PrefetchAudioAt(TimeSpan time)
    {
        foreach (var source in _audioSources.Values)
        {
            source.Reader.Prefetch(time);
        }
    }

    // No upfront extraction anymore - audio streams in 30s chunks on demand
    // (see ChunkedAudioReader), so this only records what to build readers
    // from and constructs the output; audio is ready near-instantly even for
    // an hour-long clip instead of waiting on a full-track WAV extract.
    public Task LoadAudioAsync(string path, IReadOnlyList<AudioPreviewTrack> audioTracks, TimeSpan duration, CancellationToken cancellationToken)
    {
        DisposeAudioOutput();
        _audioStreamIndexes.Clear();
        _audioInputPath = path;
        _audioDuration = duration;
        if (audioTracks.Count == 0) return Task.CompletedTask;

        foreach (var track in audioTracks)
        {
            _audioStreamIndexes.Add(track.StreamIndex);
            _audioVolumes.TryAdd(track.StreamIndex, track.VolumePercent);
        }

        AppLog.Info($"Editor audio loaded (chunked): streams={string.Join(",", _audioStreamIndexes.OrderBy(key => key))}, volumes={string.Join(",", _audioVolumes.OrderBy(pair => pair.Key).Select(pair => $"{pair.Key}:{pair.Value:0}%"))}.");
        RebuildAudioOutput();
        return Task.CompletedTask;
    }

    private void RebuildAudioOutput()
    {
        DisposeAudioOutput();
        if (_audioStreamIndexes.Count == 0 || string.IsNullOrEmpty(_audioInputPath)) return;

        var tempDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "EVE",
            "preview-audio");
        Directory.CreateDirectory(tempDir);
        PruneAudioCache(tempDir);

        var providers = new List<ISampleProvider>();
        foreach (var streamIndex in _audioStreamIndexes)
        {
            var reader = new ChunkedAudioReader(_audioInputPath, streamIndex, _audioDuration, tempDir, AudioCacheKey(_audioInputPath, streamIndex));
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
        var playVersion = Interlocked.Increment(ref _playVersion);
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
            // A plain resume-from-pause needs no seek, so audio can start
            // immediately below alongside video. A real seek is deferred to
            // StartAudioOnceVideoCatchesUpAsync instead of starting here -
            // otherwise audio starts the instant the seek is *issued*, well
            // before the video actually lands there, and sounds like it's
            // racing ahead of a video that's still visually snapping into
            // place.
            if (!needsSeek) _audioOutput?.Play();
        }

        if (needsSeek)
        {
            _ = StartAudioOnceVideoCatchesUpAsync(playVersion, milliseconds);
        }

        AppLog.Info($"Editor play from requested={time.TotalSeconds:0.###}s (seek={needsSeek}), vlc after={VideoPlayer.Time / 1000d:0.###}s, state={VideoPlayer.State}.");
    }

    // Holds audio back until the video has actually reached the seek target
    // (confirmed via TimeChanged - same signal SeekAndWaitAsync uses), bounded
    // so a seek that never confirms doesn't leave audio silent forever.
    // Guarded by playVersion (and _shouldPlay) so a Pause/Stop/newer PlayFrom
    // that happens before this fires can't have it wrongly start audio out
    // from under whatever state the session has since moved on to.
    private async Task StartAudioOnceVideoCatchesUpAsync(long playVersion, long targetMs)
    {
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void OnTimeChanged(object? sender, MediaPlayerTimeChangedEventArgs args)
        {
            if (Math.Abs(args.Time - targetMs) < 650) ready.TrySetResult();
        }

        VideoPlayer.TimeChanged += OnTimeChanged;
        try
        {
            await ready.Task.WaitAsync(TimeSpan.FromMilliseconds(900)).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
        }
        finally
        {
            VideoPlayer.TimeChanged -= OnTimeChanged;
        }

        if (Interlocked.Read(ref _playVersion) != playVersion || !_shouldPlay) return;
        lock (_transportLock)
        {
            _audioOutput?.Play();
        }
    }

    public void Pause()
    {
        Interlocked.Increment(ref _playVersion);
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
            Interlocked.Increment(ref _playVersion);
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

    public async Task<bool> SeekAsync(TimeSpan time, bool resumePlayback = false, CancellationToken cancellationToken = default, bool isPreview = false)
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
            var videoReady = await SeekAndWaitAsync(requested, cancellationToken, isPreview).ConfigureAwait(false);
            if (seekVersion != Interlocked.Read(ref _seekVersion)) return false;
            var settledTime = Position;
            lock (_transportLock)
            {
                // Seek audio to where the video actually landed (settledTime), not
                // the raw requested time - SeekAndWaitAsync tolerates the video
                // settling up to 650ms away from the request, and audio pinning to
                // the unadjusted request instead of that actual position is what
                // caused audible desync after a paused timeline click.
                EnsureAudioOutputCanSeek(settledTime);
                SeekAudio(settledTime);
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

    public void EnsurePausedIfNeeded()
    {
        // Mirrors the _shouldPlay-vs-VideoPlayer.IsPlaying race already fixed in
        // SyncAndPlayMixedAudio: a seek issued while paused/ended has to force
        // VideoPlayer.Play() first (LibVLC ignores seeks on a stopped/ended
        // player), then immediately calls SetPause(true) to put it back - but
        // that SetPause can silently not land if it races the seek's own async
        // state transition, leaving video rolling from the seek point while
        // audio (which stops synchronously) correctly stays silent. Called every
        // UI tick as a corrective check so a missed pause self-heals quickly
        // instead of requiring another user action.
        if (_shouldPlay) return;
        lock (_transportLock)
        {
            if (VideoPlayer.IsPlaying) VideoPlayer.SetPause(true);
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
            var position = Position;
            SeekAudio(position);
            // VideoPlayer.IsPlaying used to gate this too, but LibVLC's Play()/seek
            // is asynchronous - PlayFrom already issued Play() moments earlier, but
            // IsPlaying can still read false here if this runs before that state
            // transition lands, which permanently skipped starting the audio output
            // while video went on to play fine. _shouldPlay already is the source of
            // truth for play/pause intent (set by Play()/Pause()), so trust that
            // instead of re-checking a state that hasn't caught up yet.
            var willPlay = _shouldPlay;
            if (willPlay)
            {
                _audioOutput.Play();
            }

            var readerState = string.Join(",", _audioSources.Select(pair =>
                $"{pair.Key}:cur={pair.Value.Reader.CurrentTime.TotalSeconds:0.###}s/total={pair.Value.Reader.TotalTime.TotalSeconds:0.###}s"));
            AppLog.Info($"Editor audio sync: position={position.TotalSeconds:0.###}s, shouldPlay={_shouldPlay}, videoPlaying={VideoPlayer.IsPlaying}, willPlay={willPlay}, outputState={_audioOutput.PlaybackState}, readers=[{readerState}].");
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

    private async Task<bool> SeekAndWaitAsync(TimeSpan target, CancellationToken cancellationToken, bool isPreview = false)
    {
        // A drag-scrub preview seek doesn't need to wait for a precise
        // confirmation - it's purely visual (resumePlayback is always false
        // for these) and the caller doesn't act differently on true/false
        // either way. Waiting up to the full 900ms here was the actual
        // bottleneck during a fast drag: this call is serialized behind
        // _seekLock, so every scrub tick queued behind whichever one
        // currently held the lock, and the video visibly lagged the mouse
        // by however long confirmation took rather than by the UI's own
        // throttle. A short wait lets each accepted scrub seek get out of
        // the way quickly so the next (latest) mouse position can start
        // almost immediately. The 650ms match-tolerance below is untouched -
        // it only decides how close counts as "landed", not how long to
        // wait - and the real (non-preview) seek from release/restart/step
        // keeps the full 900ms, since that's the one that decides whether
        // playback correctly resumes and shortening it was what caused a
        // real desync bug previously.
        var waitTimeout = isPreview ? TimeSpan.FromMilliseconds(180) : TimeSpan.FromMilliseconds(900);
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
                await ready.Task.WaitAsync(waitTimeout, cancellationToken).ConfigureAwait(false);
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
        if (_audioStreamIndexes.Count == 0) return;
        if (_audioOutput is null)
        {
            RebuildAudioOutput();
            return;
        }

        var targetBeforeEnd = _audioSources.Values.Any(source => target < source.Reader.TotalTime - TimeSpan.FromMilliseconds(100));
        if (!targetBeforeEnd) return;

        var anyReaderAtEnd = _audioSources.Values.Any(source => source.Reader.AtEnd);
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
        _audioStreamIndexes.Clear();
        _audioInputPath = string.Empty;
        _audioDuration = TimeSpan.Zero;
        _audioVolumes.Clear();
    }

    private void DisposeAudioOutput()
    {
        WasapiOut? previous;
        lock (_transportLock)
        {
            previous = _audioOutput;
            _audioOutput = null;
            _audioMixer = null;

            foreach (var source in _audioSources.Values)
            {
                source.Reader.Dispose();
            }

            _audioSources.Clear();
        }

        if (previous is null) return;

        // WasapiOut.Stop()/Dispose() don't block until its internal render
        // thread has actually released the shared-mode IAudioClient - closing
        // one clip's editor session and immediately opening another's (or
        // re-opening the same one) could construct+Init() a new WasapiOut
        // before that release finished, which could silently leave the WASAPI
        // session wedged with no audio for the rest of the app run. Wait for
        // PlaybackStopped (or a short timeout if it never started) before
        // disposing, so the endpoint is actually free by the time the next
        // WasapiOut is created.
        using var stopped = new ManualResetEventSlim(false);
        void OnStopped(object? sender, StoppedEventArgs args) => stopped.Set();
        previous.PlaybackStopped += OnStopped;
        try
        {
            previous.Stop();
            stopped.Wait(TimeSpan.FromMilliseconds(300));
        }
        finally
        {
            previous.PlaybackStopped -= OnStopped;
            previous.Dispose();
        }
    }

    private static float VolumeCurve(double percent)
    {
        return (float)Math.Clamp(percent / 100d, 0, 1.5);
    }

    // Guards the once-per-run PruneAudioCache sweep.
    private static int _audioCachePruned;

    // The preview WAVs are uncompressed 48kHz stereo PCM (~11MB per minute per
    // audio track) and were cached with no cleanup at all - every clip ever
    // opened in the editor stayed on disk forever, measured at 9GB on one real
    // install. They're pure re-extractable cache, so anything not used in a
    // week is safe to drop. Recency is tracked by bumping LastWriteTime on
    // every cache hit (NTFS LastAccessTime updates are often disabled, so
    // that can't be trusted for this).
    private static void PruneAudioCache(string tempDir)
    {
        if (Interlocked.Exchange(ref _audioCachePruned, 1) != 0) return;
        try
        {
            var cutoff = DateTime.UtcNow.AddDays(-7);
            foreach (var file in Directory.EnumerateFiles(tempDir, "*.wav"))
            {
                if (File.GetLastWriteTimeUtc(file) < cutoff) TryDelete(file);
            }
        }
        catch
        {
            // Cache pruning must never block playback.
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

    private sealed record AudioTrackSource(ChunkedAudioReader Reader, VolumeSampleProvider Volume);

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
