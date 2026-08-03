using LibVLCSharp.Shared;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;

namespace ClypDat.App.Services;

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
    private VolumeSampleProvider? _masterVolume;
    // Survives RebuildAudioOutput (called on every clip load, which tears
    // down and recreates _masterVolume itself) so a volume set once via
    // SetMasterVolume - e.g. restored from AppSettings.EditorMasterVolume at
    // startup - stays applied across every clip opened afterward in this
    // session, not just the one that was loaded when it was set.
    private double _masterVolumePercent = 100;
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
    // Completion of the PREVIOUS WasapiOut's endpoint release. Only
    // RebuildAudioOutput has to respect it (see DisposeAudioOutput).
    private Task _audioOutputRelease = Task.CompletedTask;

    public PlaybackSession()
    {
        global::LibVLCSharp.Shared.Core.Initialize();
        // Playback runs on software decode (see LoadVideo), which is enough for
        // a short clip on its own but has to share the machine with an active
        // replay buffer. When the decoder falls behind, libvlc's defaults
        // degrade picture quality to catch up - dropping late frames and
        // skipping the in-loop deblocking filter - and that shows up as blocky,
        // pixelated playback that standalone VLC never exhibits, because VLC
        // decodes the same clip on the GPU and never falls behind in the first
        // place. Prefer a late frame over a broken-looking one.
        _libVlc = new LibVLC("--quiet", "--no-drop-late-frames", "--no-skip-frames");
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

    // Runs its whole body on a background thread: Stop() is a genuinely
    // blocking libvlc call (real time tearing down decode/output threads for
    // whatever was previously loaded), and the network-path stat below can
    // hang far longer on a slow/dropped share. Both used to run inline on the
    // caller's thread - fine for a fire-and-forget background Stop(), but this
    // is called synchronously from the UI thread on every editor open after
    // the first, which froze the whole app for however long either of those
    // took.
    // How many decode threads the editor may take, and whether it is allowed to
    // fall behind rather than fight for the CPU.
    //
    // The editor decodes in software (see LoadVideoAsync) and used to ask for
    // ":avcodec-threads=0" - every core. On a machine with a replay buffer
    // armed that is the app competing with itself: capture, encode, audio and
    // the UI all want a core too, and real logs from an 8-thread laptop show
    // every sustained capture-overload burst starting within seconds of an
    // editor open. Leave the pipeline room.
    private static int ResolveDecodeThreads(bool replayArmed)
    {
        var cores = Environment.ProcessorCount;
        var share = replayArmed ? cores / 4 : cores / 2;
        return Math.Max(2, share);
    }

    public Task LoadVideoAsync(string path, bool replayArmed = false) => Task.Run(() =>
    {
        Stop();
        DisposeMedia();
        DisposeAudio();
        _ended = false;
        _lastRequestedPosition = TimeSpan.Zero;
        _videoMedia = new Media(_libVlc, new Uri(path));
        _videoMedia.AddOption(":no-audio");
        // Hardware decode (both hard-forced "d3d11va" and negotiated "any")
        // still showed blocky macroblock corruption on some NVENC-encoded
        // clips that play back clean in standalone VLC - the GPU decode path
        // was opening successfully (so "any" had nothing to fall back from)
        // and just decoding wrong, likely a stale-reference/driver quirk with
        // this content's GOP structure. Forcing software decode removes the
        // GPU decode path from the equation entirely; editor clips are short
        // enough that CPU decode cost isn't a real concern.
        _videoMedia.AddOption(":avcodec-hw=none");
        // Belt-and-braces alongside the instance-level --no-skip-frames above:
        // these are the decoder's own quality shortcuts, and skipping the
        // deblocking filter in particular is exactly what makes H.264 look
        // blocky. 0 = skip nothing.
        _videoMedia.AddOption(":avcodec-skiploopfilter=0");
        _videoMedia.AddOption(":avcodec-skip-frame=0");
        _videoMedia.AddOption(":avcodec-skip-idct=0");
        // Bounded rather than libvlc's "0" (every core) - see
        // ResolveDecodeThreads. Still generous enough that a short 1080p clip
        // decodes comfortably ahead of playback; just not at the price of
        // starving the capture pipeline of the machine it is recording with.
        var decodeThreads = ResolveDecodeThreads(replayArmed);
        _videoMedia.AddOption($":avcodec-threads={decodeThreads}");
        // With a buffer armed, prefer a dropped frame over stealing time from
        // capture: these ask libvlc to undo, for this clip only, the two
        // instance-level quality flags set in the constructor. Idle (no buffer)
        // playback adds nothing here and keeps the pristine path.
        //
        // Fail-safe either way: if libvlc declines to honour them at media
        // scope the instance-level --no-drop-late-frames/--no-skip-frames still
        // apply and behaviour is exactly what it was before this change.
        if (replayArmed)
        {
            _videoMedia.AddOption(":drop-late-frames");
            _videoMedia.AddOption(":skip-frames");
        }
        // LibVLC already streams windowed around the playhead (it never reads
        // the whole file), but its default read-ahead cache is sized for
        // local disks - on a network drive (UNC path or mapped SMB share) the
        // higher/spikier read latency blows through it and playback stutters.
        // A bigger demux cache absorbs those latency spikes at the cost of a
        // few MB of RAM; local files keep a modest bump over the default.
        // Local files get libvlc's own default (300ms) rather than the 1000ms
        // this used to set unconditionally: file-caching is how much the demuxer
        // buffers BEFORE it will render anything, so on a local disk - which has
        // none of the latency spikes this bump exists to absorb - it was simply
        // 700ms of guaranteed delay added to every single clip open, in exchange
        // for smoothing out a problem local storage doesn't have.
        var isNetwork = IsNetworkPath(path);
        _videoMedia.AddOption($":file-caching={(isNetwork ? 5000 : 300)}");
        VideoPlayer.Media = _videoMedia;
        VideoPlayer.Mute = true;
        VideoPlayer.Volume = 0;

        // Network-drive diagnostics: size + storage type up front, so slow
        // opens in the log can immediately be attributed (or not) to the file
        // living on a share.
        long sizeMb = 0;
        try { sizeMb = new FileInfo(path).Length / (1024 * 1024); } catch { }
        AppLog.Debug($"Editor video load: network={isNetwork}, sizeMB={sizeMb}, replayArmed={replayArmed}, decodeThreads={decodeThreads}, path={path}");
    });

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

        AppLog.Debug($"Editor audio loaded (chunked): streams={string.Join(",", _audioStreamIndexes.OrderBy(key => key))}, volumes={string.Join(",", _audioVolumes.OrderBy(pair => pair.Key).Select(pair => $"{pair.Key}:{pair.Value:0}%"))}.");
        RebuildAudioOutput();
        return Task.CompletedTask;
    }

    private void RebuildAudioOutput()
    {
        DisposeAudioOutput();
        if (_audioStreamIndexes.Count == 0 || string.IsNullOrEmpty(_audioInputPath)) return;

        var providers = new List<ISampleProvider>();
        foreach (var streamIndex in _audioStreamIndexes)
        {
            var reader = new ChunkedAudioReader(_audioInputPath, streamIndex, _audioDuration, AudioCacheKey(_audioInputPath, streamIndex));
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
        // Master volume sits BEFORE the limiter (not after) so boosting past
        // 100% still gets caught by SoftLimiterSampleProvider instead of
        // clipping the final output unprotected.
        _masterVolume = new VolumeSampleProvider(normalized) { Volume = VolumeCurve(_masterVolumePercent) };
        var limited = new SoftLimiterSampleProvider(_masterVolume);
        // The one place that genuinely needs the previous endpoint gone. By now
        // the release started at load time has almost always finished already,
        // so this is normally a no-op; the timeout is only so a WasapiOut whose
        // PlaybackStopped never fires can't wedge the rebuild forever.
        try { _audioOutputRelease.Wait(TimeSpan.FromMilliseconds(500)); } catch { }
        _audioOutput = new WasapiOut(AudioClientShareMode.Shared, false, 120);
        _audioOutput.Init(limited);
        AppLog.Debug($"Editor audio output ready: streams={string.Join(",", _audioSources.Keys.OrderBy(key => key))}.");
    }

    public void Play()
    {
        PlayFrom(Position);
    }

    public void PlayFrom(TimeSpan time)
    {
        // A timeline seek can still be waiting for LibVLC to settle when the
        // user presses Play. Its old completion must not pause/stop the newer
        // transport state after PlayFrom has already started it.
        Interlocked.Increment(ref _seekVersion);
        var playVersion = Interlocked.Increment(ref _playVersion);
        var milliseconds = Math.Max(0, (long)time.TotalMilliseconds);
        var wasStoppedOrEnded = IsEnded || VideoPlayer.State == VLCState.Stopped;
        AppLog.Debug($"Editor play from requested={time.TotalSeconds:0.###}s, vlc={VideoPlayer.Time / 1000d:0.###}s, state={VideoPlayer.State}, ended={IsEnded}.");
        if (wasStoppedOrEnded)
        {
            VideoPlayer.Stop();
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
            EnsureAudioOutputConnected();
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

        AppLog.Debug($"Editor play from requested={time.TotalSeconds:0.###}s (seek={needsSeek}), vlc after={VideoPlayer.Time / 1000d:0.###}s, state={VideoPlayer.State}.");
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
            // WasapiOut's 120ms buffer (see RebuildAudioOutput) means the audio
            // readers already got pulled ~120ms ahead of what was actually
            // heard by the time Stop() lands - left uncorrected, that overshoot
            // never gets reconciled on a plain resume-from-pause (only a real
            // seek reseeks audio) and compounds by another ~120ms on every
            // subsequent pause, drifting audio further out of sync with the
            // video each time. Re-anchoring to the true (video) pause position
            // here resets it every time instead of letting it accumulate.
            SeekAudio(Position);
        }
        AppLog.Debug($"Editor pause at {Position.TotalSeconds:0.###}s.");
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

    // Scrub/keyboard-repeat seeking: issue the position write and return, with
    // no lock, no confirmation wait and no audio work at all.
    //
    // These used to go through SeekAsync like any other seek, which made them
    // as slow as the slowest thing in that method. Two costs dominated. First,
    // serialization: every scrub tick queued behind _seekLock waiting on the
    // previous tick's settle confirmation, so the picture trailed the cursor by
    // the confirmation time rather than by the UI's own throttle. Second, and
    // worse, audio: SeekAsync repositions the ChunkedAudioReaders, and setting
    // CurrentTime on one closes its open chunk and prefetches three more - so a
    // drag across a three-track clip was firing off ffmpeg chunk extractions by
    // the dozen, competing with the video decode the user is actually watching,
    // to reposition audio that is stopped for the whole drag anyway.
    //
    // Audio is repositioned once, by the real SeekAsync the caller issues when
    // the drag/key-repeat ends. Video is deliberately left running here rather
    // than paused per tick: pausing and unpausing around every position write
    // makes libvlc rebuffer, and the next tick (or the settling seek) supersedes
    // it in a few tens of milliseconds regardless.
    public void SeekPreview(TimeSpan time)
    {
        var milliseconds = Math.Max(0, (long)time.TotalMilliseconds);
        // Same counter the real seeks use, so one of these can never land on
        // top of a newer settling seek.
        Interlocked.Increment(ref _seekVersion);
        _lastRequestedPosition = TimeSpan.FromMilliseconds(milliseconds);
        try
        {
            lock (_transportLock)
            {
                ForceVideoSilent();
                _audioOutput?.Stop();
                if (IsEnded || VideoPlayer.State == VLCState.Stopped)
                {
                    VideoPlayer.Stop();
                    _ended = false;
                }
                // libvlc ignores a Time assignment made before the player has
                // actually started - see PlayFrom for the same ordering.
                if (!VideoPlayer.IsPlaying) VideoPlayer.Play();
                VideoPlayer.SetPause(false);
                VideoPlayer.Time = milliseconds;
            }
        }
        catch (Exception error)
        {
            AppLog.Error("Editor preview seek failed", error);
        }
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
                // VLC accepts a Time assignment after EndReached without
                // restarting its decoder, but leaves the transport state at
                // Ended. Reset it before issuing the seek so settling lands
                // in a usable paused/playing state. Keep the existing audio
                // output and readers: they are repositioned after video
                // settles below, avoiding a slow WASAPI/mixer rebuild.
                var resetTransport = IsEnded || VideoPlayer.State == VLCState.Stopped;
                if (resetTransport)
                {
                    VideoPlayer.Stop();
                    _ended = false;
                }
                if (!VideoPlayer.IsPlaying) VideoPlayer.Play();
                VideoPlayer.SetPause(false);
            }
            AppLog.Debug($"Editor seek begin: requested={requested.TotalSeconds:0.###}s, vlc={VideoPlayer.Time / 1000d:0.###}s, state={VideoPlayer.State}, resume={resumePlayback}, version={seekVersion}.");
            if (seekVersion != Interlocked.Read(ref _seekVersion)) return false;
            var videoReady = await SeekAndWaitAsync(requested, cancellationToken).ConfigureAwait(false);
            if (seekVersion != Interlocked.Read(ref _seekVersion)) return false;
            var settledTime = Position;
            lock (_transportLock)
            {
                // Seek audio to where the video actually landed (settledTime), not
                // the raw requested time - SeekAndWaitAsync tolerates the video
                // settling up to 650ms away from the request, and audio pinning to
                // the unadjusted request instead of that actual position is what
                // caused audible desync after a paused timeline click.
                EnsureAudioOutputConnected();
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

            AppLog.Debug($"Editor seek end: requested={requested.TotalSeconds:0.###}s, settled={settledTime.TotalSeconds:0.###}s, vlc={VideoPlayer.Time / 1000d:0.###}s, state={VideoPlayer.State}, resume={resumePlayback}, resumed={resumed}, version={seekVersion}.");
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

    // Global output level (fullscreen playbar slider) - distinct from
    // SetTrackVolume, which mixes individual tracks against each other.
    public void SetMasterVolume(double percent)
    {
        _masterVolumePercent = percent;
        if (_masterVolume is not null) _masterVolume.Volume = VolumeCurve(percent);
    }

    public void SetTrackVolume(int streamIndex, double percent)
    {
        _audioVolumes[streamIndex] = percent;
        if (_audioSources.TryGetValue(streamIndex, out var source))
        {
            source.Volume.Volume = VolumeCurve(percent);
            AppLog.Debug($"Editor volume changed: stream={streamIndex}, percent={percent:0}%, found=True.");
        }
        else
        {
            AppLog.Debug($"Editor volume changed: stream={streamIndex}, percent={percent:0}%, found=False, loaded={string.Join(",", _audioSources.Keys.OrderBy(key => key))}.");
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
            AppLog.Debug($"Editor audio sync: position={position.TotalSeconds:0.###}s, shouldPlay={_shouldPlay}, videoPlaying={VideoPlayer.IsPlaying}, willPlay={willPlay}, outputState={_audioOutput.PlaybackState}, readers=[{readerState}].");
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
        // Only real (settling) seeks reach here now - drag-scrub and keyboard
        // repeat go through SeekPreview, which never waits at all. This one
        // decides whether playback correctly resumes, so it keeps the full
        // 900ms; shortening it previously caused a real desync bug.
        var waitTimeout = TimeSpan.FromMilliseconds(900);
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
                    AppLog.Debug($"Editor video seek settle timeout: target={target.TotalSeconds:0.###}s, actual={Position.TotalSeconds:0.###}s, playing={VideoPlayer.IsPlaying}.");
                    return false;
                }
                AppLog.Debug($"Editor video seek settle timeout but position already matches: target={target.TotalSeconds:0.###}s, actual={Position.TotalSeconds:0.###}s.");
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

    private void EnsureAudioOutputConnected()
    {
        if (_audioStreamIndexes.Count == 0) return;
        if (_audioOutput is null)
        {
            RebuildAudioOutput();
            return;
        }

        // Readers no longer drop out of the mixer when the clip ends
        // (ChunkedAudioReader pads past the end rather than short-reading, and
        // a short read is what makes MixingSampleProvider discard an input for
        // good). This stays as the backstop: a mixer missing any of its inputs
        // renders silence no matter where the readers are seeked to, and only
        // a rebuild reconnects them.
        int connected;
        try
        {
            connected = _audioMixer?.MixerInputs.Count() ?? 0;
        }
        catch (InvalidOperationException)
        {
            // MixerInputs hands back the live list, so the render thread can be
            // mutating it mid-enumeration. Leave it for the next call rather
            // than tearing down the output on a transient.
            return;
        }

        if (connected >= _audioSources.Count) return;

        AppLog.Info($"Editor audio mixer lost inputs (connected={connected}, expected={_audioSources.Count}); rebuilding before replay.");
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
            _masterVolume = null;

            foreach (var source in _audioSources.Values)
            {
                source.Reader.Dispose();
            }

            _audioSources.Clear();
        }

        if (previous is null) return;

        // The wait below used to run right here, inline, which put it squarely
        // on the editor-open critical path: LoadVideoAsync called this BEFORE
        // constructing the new Media, so every clip open after the first spent
        // up to 300ms tearing down the previous clip's audio endpoint before
        // libvlc was even told what to decode next. Nothing about opening a new
        // video needs the old audio endpoint to be gone - only building the
        // NEXT WasapiOut does, which is what RebuildAudioOutput waits on.
        _audioOutputRelease = Task.Run(() => ReleaseAudioOutput(previous));
    }

    // WasapiOut.Stop()/Dispose() don't block until its internal render thread
    // has actually released the shared-mode IAudioClient - closing one clip's
    // editor session and immediately opening another's (or re-opening the same
    // one) could construct+Init() a new WasapiOut before that release finished,
    // which could silently leave the WASAPI session wedged with no audio for
    // the rest of the app run. Wait for PlaybackStopped (or a short timeout if
    // it never started) before disposing, so the endpoint is actually free by
    // the time the next WasapiOut is created.
    private static void ReleaseAudioOutput(WasapiOut previous)
    {
        using var stopped = new ManualResetEventSlim(false);
        void OnStopped(object? sender, StoppedEventArgs args) => stopped.Set();
        previous.PlaybackStopped += OnStopped;
        try
        {
            previous.Stop();
            stopped.Wait(TimeSpan.FromMilliseconds(300));
        }
        catch
        {
            // A failed teardown must not take the next clip's audio with it.
        }
        finally
        {
            previous.PlaybackStopped -= OnStopped;
            try { previous.Dispose(); } catch { }
        }
    }

    private static float VolumeCurve(double percent)
    {
        return (float)Math.Clamp(percent / 100d, 0, 1.5);
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
