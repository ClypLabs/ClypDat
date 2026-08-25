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
    private volatile bool _disposed;
    // Generous on purpose: a preview decode holding _seekLock is bounded work, and
    // overshooting the wait is far cheaper than the leak the timeout path takes.
    private static readonly TimeSpan PreviewWorkerDrainTimeout = TimeSpan.FromSeconds(5);
    private bool _ended;
    private bool _isSeeking;
    private bool _shouldPlay;
    private long _seekVersion;
    private long _playVersion;
    private TimeSpan _lastRequestedPosition = TimeSpan.Zero;
    private readonly SemaphoreSlim _seekLock = new(1, 1);
    private readonly object _transportLock = new();
    private readonly PlaybackLoadGate _loadGate = new();
    private readonly object _previewLock = new();
    private readonly EditorSeekRequestQueue _previewRequests = new();
    private readonly EditorSeekCoordinator _seekCoordinator = new();
    private readonly EditorAvClockPolicy _audioClockPolicy = new();
    private Task? _previewWorker;
    private EventHandler<MediaPlayerTimeChangedEventArgs>? _audioDriftHandler;
    private long _audioAnchorDevicePosition;
    private TimeSpan _audioAnchorMediaTime;
    private long _audioAnchorTimestamp;
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

    // A session built ahead of the first editor open and parked here for it to
    // claim. WarmUp used to build a throwaway LibVLC and dispose it, which
    // pre-paid the DLL load and plugin scan but left the first clip click still
    // constructing the engine + MediaPlayer it actually plays through. Keeping
    // the real thing means the first open reuses a session exactly the way
    // every open after the first already does.
    // Tracked as the in-flight TASK, not the finished session. Parking only the
    // finished object left a window where a warm-up had started but not landed,
    // and a click inside it saw an empty slot and built a SECOND engine - two
    // cold LibVLC constructions running at once, each scanning the plugin
    // directory, contending for the same disk. Measured: warm-up begins at
    // launch+12s, a clip clicked at launch+13.1s, "engine ready at 8691ms".
    // Waiting on the one already running is always cheaper than starting
    // another, and the window is exactly when a user who opened the app to
    // watch something clicks.
    private static Task<PlaybackSession>? _warming;
    private static readonly object WarmingLock = new();

    public static void WarmUp()
    {
        lock (WarmingLock)
        {
            _warming ??= Task.Run(() => new PlaybackSession());
        }
    }

    // Claims the warm-up - finished or still running - and otherwise builds one.
    // Either way the caller owns exactly one session and the slot is left empty
    // so nothing else can hand the same instance out twice.
    //
    // Blocking on the task is deliberate: this is already called from a
    // background thread (see MainWindow's editor open, which constructs off the
    // UI thread precisely so a cold engine cannot freeze the window), and the
    // wait is bounded by a construction that is already underway.
    public static PlaybackSession TakeWarmedOrCreate()
    {
        Task<PlaybackSession>? pending;
        lock (WarmingLock)
        {
            pending = _warming;
            _warming = null;
        }

        if (pending is null) return new PlaybackSession();
        try
        {
            return pending.GetAwaiter().GetResult();
        }
        catch (Exception error)
        {
            // A warm-up that threw must not take the editor open down with it.
            AppLog.Error("Playback engine warm-up failed; building a fresh session.", error);
            return new PlaybackSession();
        }
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

    public Task LoadVideoAsync(string path, bool replayArmed = false) => LoadVideoAsync(path, string.Empty, replayArmed);

    internal Task LoadVideoAsync(string path, string videoCodec, bool replayArmed = false, CancellationToken cancellationToken = default) => Task.Run(async () =>
    {
        using var load = await _loadGate.EnterAsync(cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        // Split timings, because the three things this body does have wildly
        // different costs and only the total was ever visible: Stop() is
        // libvlc tearing down the PREVIOUS clip's decode/vout threads (real
        // time, and the whole reason this runs off the UI thread), the
        // teardown after it disposes the old Media, and the rest is cheap
        // option-setting. Which one dominates decides where an open-latency
        // fix has to go.
        var loadClock = Stopwatch.StartNew();
        Stop();
        cancellationToken.ThrowIfCancellationRequested();
        var stopMs = loadClock.ElapsedMilliseconds;
        DisposeMedia();
        DisposeAudio();
        var teardownMs = loadClock.ElapsedMilliseconds;
        _ended = false;
        _lastRequestedPosition = TimeSpan.Zero;
        _videoMedia = new Media(_libVlc, new Uri(path));
        _videoMedia.AddOption(":no-audio");
        if (IsH264(videoCodec))
        {
            var safeForHardwareDecode = H264HardwareDecodeProbe.HasOnlyIdrRandomAccessPoints(path);
            // Hardware H.264 seeking is safe only for clips whose advertised
            // random-access packets are genuine IDRs. Current replay output
            // is; legacy clips retain the software fallback.
            _videoMedia.AddOption(safeForHardwareDecode ? ":avcodec-hw=any" : ":avcodec-hw=none");
            _videoMedia.AddOption(":avcodec-skiploopfilter=0");
            _videoMedia.AddOption(":avcodec-skip-frame=0");
            _videoMedia.AddOption(":avcodec-skip-idct=0");
            AppLog.Info($"Editor H.264 decode: {(safeForHardwareDecode ? "hardware" : "software")} (IDR random-access probe).");
        }
        else
        {
            // AV1/HEVC use negotiated hardware decode when available, with
            // LibVLC software fallback when no compatible hardware path exists.
            _videoMedia.AddOption(":avcodec-hw=any");
        }
        // Bounded rather than libvlc's "0" (every core) - see
        // ResolveDecodeThreads. Still generous enough that a short 1080p clip
        // decodes comfortably ahead of playback; just not at the price of
        // starving the capture pipeline of the machine it is recording with.
        var decodeThreads = ResolveDecodeThreads(replayArmed);
        _videoMedia.AddOption($":avcodec-threads={decodeThreads}");
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
        cancellationToken.ThrowIfCancellationRequested();
        VideoPlayer.Media = _videoMedia;
        VideoPlayer.Mute = true;
        VideoPlayer.Volume = 0;

        // Network-drive diagnostics: size + storage type up front, so slow
        // opens in the log can immediately be attributed (or not) to the file
        // living on a share.
        long sizeMb = 0;
        try { sizeMb = new FileInfo(path).Length / (1024 * 1024); } catch { }
        AppLog.Debug($"Editor video load: codec={videoCodec}, network={isNetwork}, sizeMB={sizeMb}, replayArmed={replayArmed}, decodeThreads={decodeThreads}, stopMs={stopMs}, teardownMs={teardownMs}, totalMs={loadClock.ElapsedMilliseconds}, path={path}");
    });

    private static bool IsH264(string? codec) =>
        string.Equals(codec, "h264", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(codec, "avc", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(codec, "avc1", StringComparison.OrdinalIgnoreCase);

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
    public Task LoadAudioAsync(string path, IReadOnlyList<AudioPreviewTrack> audioTracks, TimeSpan duration, CancellationToken cancellationToken) => Task.Run(() =>
    {
        using var load = _loadGate.Enter(cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        DisposeAudioOutput();
        _audioStreamIndexes.Clear();
        _audioInputPath = path;
        _audioDuration = duration;
        if (audioTracks.Count == 0) return;

        foreach (var track in audioTracks)
        {
            _audioStreamIndexes.Add(track.StreamIndex);
            _audioVolumes.TryAdd(track.StreamIndex, track.VolumePercent);
        }

        AppLog.Debug($"Editor audio loaded (chunked): streams={string.Join(",", _audioStreamIndexes.OrderBy(key => key))}, volumes={string.Join(",", _audioVolumes.OrderBy(pair => pair.Key).Select(pair => $"{pair.Key}:{pair.Value:0}%"))}.");
        RebuildAudioOutput();
    }, cancellationToken);

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
        Interlocked.Increment(ref _playVersion);
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
        if (needsSeek)
        {
            // Stopped/ended playback and a distant PlayFrom target are final
            // seeks too. Route them through the same pause -> land -> roll
            // sequence as the timeline rather than issuing a parallel seek.
            _ = SeekAsync(TimeSpan.FromMilliseconds(milliseconds), resumePlayback: true);
            return;
        }

        lock (_transportLock)
        {
            EnsureAudioOutputConnected();
            var anchor = Position;
            SeekAudio(anchor);
            VideoPlayer.Play();
            VideoPlayer.SetPause(false);
            StartAudioAt(anchor, Interlocked.Read(ref _seekVersion));
        }

        AppLog.Debug($"Editor play from requested={time.TotalSeconds:0.###}s (seek={needsSeek}), vlc after={VideoPlayer.Time / 1000d:0.###}s, state={VideoPlayer.State}.");
    }

    public void Pause()
    {
        Interlocked.Increment(ref _playVersion);
        _shouldPlay = false;
        lock (_transportLock)
        {
            StopAudioClockMonitoring();
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
                StopAudioClockMonitoring();
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

    /// <summary>
    /// Stops transport and RELEASES THE FILE: disposes the Media that libvlc is
    /// demuxing and tears down the audio output along with its ChunkedAudioReaders.
    ///
    /// Deliberately not Dispose(): VideoPlayer and the LibVLC engine stay alive so the
    /// session can be reused for the next clip, which is what the editor relies on.
    ///
    /// This exists because Stop() alone does not release the file. libvlc opens it
    /// without FILE_SHARE_DELETE, and _videoMedia is only released by DisposeMedia, so
    /// deleting a clip while its session still held the Media failed with a sharing
    /// violation. It is the same teardown prefix LoadVideoAsync already runs before
    /// loading a new path, so a later open re-runs it against nulls harmlessly.
    /// </summary>
    public void UnloadMedia()
    {
        Stop();

        try
        {
            // Drop the player's own reference first, so disposing the Media below
            // actually takes its refcount to zero.
            VideoPlayer.Media = null;
        }
        catch (Exception error)
        {
            AppLog.Error("Editor media detach failed", error);
        }

        DisposeMedia();
        DisposeAudio();
    }

    // Scrub/keyboard-repeat seeking: queue a video-only preview and return,
    // with no confirmation wait and no audio work at all.
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
    // the drag/key-repeat ends. The preview worker keeps video paused between
    // writes; it only starts the pipeline around a position write when LibVLC
    // needs that transition to present a newly landed frame.
    public void SeekPreview(TimeSpan time)
    {
        var milliseconds = Math.Max(0, (long)time.TotalMilliseconds);
        Interlocked.Increment(ref _seekVersion);
        _lastRequestedPosition = TimeSpan.FromMilliseconds(milliseconds);
        try
        {
            lock (_transportLock)
            {
                ForceVideoSilent();
                _audioOutput?.Stop();
            }

            // One worker owns all preview writes. New drag positions replace a
            // pending target; an active GOP decode finishes before final seek
            // can acquire the same lock.
            lock (_previewLock)
            {
                if (_disposed) return;
                _previewRequests.QueuePreview(TimeSpan.FromMilliseconds(milliseconds));
                if (_previewWorker is null) _previewWorker = Task.Run(PreviewSeekWorkerAsync);
            }
        }
        catch (Exception error)
        {
            AppLog.Error("Editor preview seek failed", error);
        }
    }

    private async Task PreviewSeekWorkerAsync()
    {
        try
        {
            while (true)
            {
                // Dispose() releases the media player and the LibVLC instance. Every
                // VideoPlayer touch below is a call into native libvlc, so bail out
                // the moment disposal starts rather than racing it.
                if (_disposed)
                {
                    lock (_previewLock) _previewWorker = null;
                    return;
                }

                if (!_previewRequests.TryTakePreview(DateTimeOffset.UtcNow, out var target, out var generation, out var delay))
                {
                    if (delay > TimeSpan.Zero)
                    {
                        await Task.Delay(delay).ConfigureAwait(false);
                        continue;
                    }

                    // Keep a preview decode alive only while pointer updates
                    // are arriving. This gives LibVLC time to present the
                    // landed frame, then parks video when the mouse stops.
                    if (await WaitForPreviewActivityAsync().ConfigureAwait(false)) continue;

                    lock (_transportLock)
                    {
                        if (_disposed)
                        {
                            lock (_previewLock) _previewWorker = null;
                            return;
                        }

                        VideoPlayer.SetPause(true);
                    }

                    lock (_previewLock)
                    {
                        // Queueing can race the worker's empty check. Keep
                        // this worker alive when a target arrived before it
                        // acquired the lifecycle lock; otherwise that target
                        // would sit forever with no writer.
                        if (_previewRequests.HasPendingPreview()) continue;
                        _previewWorker = null;
                        return;
                    }
                }

                await _seekLock.WaitAsync().ConfigureAwait(false);
                try
                {
                    if (!_previewRequests.IsCurrent(generation)) continue;
                    if (_disposed) return;
                    lock (_transportLock)
                    {
                        if (_disposed) return;
                        // Preview writes intentionally do not settle or touch
                        // audio. Video is parked by the idle branch above;
                        // the next final seek owns pause/land/roll.
                        ForceVideoSilent();
                        if (IsEnded || VideoPlayer.State == VLCState.Stopped)
                        {
                            VideoPlayer.Stop();
                            _ended = false;
                            VideoPlayer.Play();
                        }
                        else if (!VideoPlayer.IsPlaying)
                        {
                            // Timeline drags keep transport paused. Start the
                            // pipeline only long enough for LibVLC to accept
                            // the preview seek; pause immediately after the
                            // write so the clip cannot run while the pointer
                            // is stationary.
                            VideoPlayer.Play();
                        }
                        VideoPlayer.Time = (long)target.TotalMilliseconds;
                        _previewRequests.MarkPreviewWritten(generation, DateTimeOffset.UtcNow);
                    }
                }
                finally
                {
                    _seekLock.Release();
                }
            }
        }
        catch (Exception error)
        {
            lock (_previewLock) _previewWorker = null;
            AppLog.Error("Editor preview seek worker failed", error);
        }
    }

    public async Task<bool> SeekAsync(TimeSpan time, bool resumePlayback = false, CancellationToken cancellationToken = default)
    {
        var finalRequest = _previewRequests.BeginFinalSeek(DateTimeOffset.UtcNow);
        var finalRequestGeneration = finalRequest.Generation;
        var seekVersion = Interlocked.Increment(ref _seekVersion);
        try
        {
            await _seekLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            _previewRequests.CompleteFinalSeek(finalRequestGeneration);
            throw;
        }
        if (seekVersion != Interlocked.Read(ref _seekVersion))
        {
            // Superseded by a newer seek while queued behind the lock - bail out
            // before touching VLC at all, instead of issuing a now-stale seek that
            // would just interrupt the newer one's in-flight decode.
            _seekLock.Release();
            _previewRequests.CompleteFinalSeek(finalRequestGeneration);
            return false;
        }
        var milliseconds = Math.Max(0, (long)time.TotalMilliseconds);
        var requested = TimeSpan.FromMilliseconds(milliseconds);
        _isSeeking = true;
        _shouldPlay = resumePlayback;
        _lastRequestedPosition = requested;
        try
        {
            if (finalRequest.QuietPeriod > TimeSpan.Zero)
            {
                AppLog.Debug($"Editor seek waiting for preview quiet period: waitMs={finalRequest.QuietPeriod.TotalMilliseconds:0}, previewWrites={finalRequest.PreviewWriteCount}, generation={seekVersion}.");
                await Task.Delay(finalRequest.QuietPeriod, cancellationToken).ConfigureAwait(false);
            }

            AppLog.Debug($"Editor seek begin: requested={requested.TotalSeconds:0.###}s, vlc={VideoPlayer.Time / 1000d:0.###}s, state={VideoPlayer.State}, resume={resumePlayback}, previewRequests={finalRequest.PreviewRequestCount}, previewWrites={finalRequest.PreviewWriteCount}, coalesced={Math.Max(0, finalRequest.PreviewRequestCount - finalRequest.PreviewWriteCount)}, proactiveReset={finalRequest.RequiresDecoderReset}, generation={seekVersion}.");
            var result = await _seekCoordinator.SeekAsync(
                new PlaybackSeekTransport(this, seekVersion),
                requested,
                resumePlayback,
                seekVersion,
                () => seekVersion == Interlocked.Read(ref _seekVersion),
                cancellationToken,
                finalRequest.RequiresDecoderReset).ConfigureAwait(false);

            if (!result.Succeeded)
            {
                if (seekVersion == Interlocked.Read(ref _seekVersion)) _shouldPlay = false;
                AppLog.Debug($"Editor seek failed: requested={requested.TotalSeconds:0.###}s, landed={result.Landed.TotalSeconds:0.###}s, resume={resumePlayback}, superseded={result.Superseded}, generation={seekVersion}.");
                return false;
            }

            _lastRequestedPosition = result.Landed;
            if (!resumePlayback)
            {
                lock (_transportLock) SeekAudio(result.Landed);
            }

            AppLog.Debug($"Editor seek end: requested={requested.TotalSeconds:0.###}s, landed={result.Landed.TotalSeconds:0.###}s, audioAnchor={result.AudioAnchor.TotalSeconds:0.###}s, rollConfirmed={result.Resumed}, state={VideoPlayer.State}, resume={resumePlayback}, generation={seekVersion}.");
            return !resumePlayback || result.Resumed;
        }
        finally
        {
            if (seekVersion == Interlocked.Read(ref _seekVersion)) _isSeeking = false;
            _seekLock.Release();
            _previewRequests.CompleteFinalSeek(finalRequestGeneration);
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
            if (_audioOutput is not null && _audioOutput.PlaybackState != PlaybackState.Playing) StartAudioAt(Position, Interlocked.Read(ref _seekVersion));
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
                StartAudioAt(Position, Interlocked.Read(ref _seekVersion));
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
                StartAudioAt(position, Interlocked.Read(ref _seekVersion));
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
        _previewRequests.BeginFinalSeek();

        // The preview worker is a detached Task.Run that calls into libvlc
        // (SetPause / Time) and awaits _seekLock. Releasing the media player, the
        // LibVLC instance, or the semaphore while it is still running is a native
        // use-after-free - reachable by closing the editor while the timeline
        // scrubber is being dragged. Drain it first.
        Task? worker;
        lock (_previewLock) worker = _previewWorker;

        var drained = true;
        if (worker is not null && !worker.IsCompleted)
        {
            try
            {
                drained = worker.Wait(PreviewWorkerDrainTimeout);
            }
            catch (Exception error)
            {
                // A faulted worker is already finished, which is all this needs.
                AppLog.Debug($"Editor preview worker ended with an error during dispose: {error.Message}");
            }
        }

        Stop();

        if (!drained)
        {
            // Leak rather than free-and-use: the worker is still inside libvlc, so
            // releasing these handles now would corrupt native state. The process is
            // closing this editor, not the app, so this is a bounded, logged leak.
            AppLog.Error($"Editor preview worker did not stop within {PreviewWorkerDrainTimeout.TotalSeconds:0.#}s; " +
                         "leaving the media player and LibVLC instance alive to avoid a use-after-free.");
            DisposeAudio();
            return;
        }

        VideoPlayer.Dispose();
        DisposeAudio();
        DisposeMedia();
        _libVlc.Dispose();
        _seekLock.Dispose();
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

    private async Task<bool> WaitForPreviewActivityAsync()
    {
        var clock = Stopwatch.StartNew();
        while (clock.Elapsed < TimeSpan.FromMilliseconds(50))
        {
            if (_previewRequests.HasPendingPreview()) return true;
            await Task.Delay(TimeSpan.FromMilliseconds(10)).ConfigureAwait(false);
        }

        return _previewRequests.HasPendingPreview();
    }

    private void StartAudioAt(TimeSpan anchor, long generation)
    {
        if (_audioOutput is null) return;
        StopAudioClockMonitoring();
        _audioOutput.Play();
        _audioAnchorMediaTime = anchor;
        try { _audioAnchorDevicePosition = _audioOutput.GetPosition(); }
        catch { _audioAnchorDevicePosition = 0; }
        _audioAnchorTimestamp = Stopwatch.GetTimestamp();
        _audioClockPolicy.Begin(generation);
        _audioDriftHandler = (_, args) => ObserveAudioDrift(generation, TimeSpan.FromMilliseconds(Math.Max(0, args.Time)));
        VideoPlayer.TimeChanged += _audioDriftHandler;
        AppLog.Debug($"Editor audio anchor: media={anchor.TotalSeconds:0.###}s, hardware={_audioAnchorDevicePosition}, generation={generation}.");
    }

    private void ObserveAudioDrift(long generation, TimeSpan videoTime)
    {
        if (generation != Interlocked.Read(ref _seekVersion) || _audioOutput is null) return;
        long devicePosition;
        try { devicePosition = _audioOutput.GetPosition(); }
        catch { return; }

        var audible = EditorAvClockPolicy.ToMediaTime(_audioAnchorMediaTime, _audioAnchorDevicePosition, devicePosition, 48_000 * 2 * sizeof(float));
        var elapsed = Stopwatch.GetElapsedTime(_audioAnchorTimestamp);
        if (!_audioClockPolicy.TryGetCorrection(generation, elapsed, audible, videoTime, out var correction)) return;

        AppLog.Debug($"Editor A/V drift: audio={audible.TotalSeconds:0.###}s, video={videoTime.TotalSeconds:0.###}s, correction={correction.TotalSeconds:0.###}s, generation={generation}.");
        if (_disposed) return;
        _ = ApplyAudioCorrectionAsync(generation, correction);
    }

    private async Task ApplyAudioCorrectionAsync(long generation, TimeSpan correction)
    {
        // Fire-and-forget from the TimeChanged handler, so it can still be in flight
        // when the editor closes and Dispose() releases _seekLock.
        if (_disposed) return;
        try
        {
            await _seekLock.WaitAsync().ConfigureAwait(false);
        }
        catch (ObjectDisposedException)
        {
            return;
        }

        try
        {
            if (_disposed) return;
            if (generation != Interlocked.Read(ref _seekVersion) || !_shouldPlay || _audioOutput is null) return;
            lock (_transportLock)
            {
                _audioOutput.Stop();
                SeekAudio(correction);
                _audioOutput.Play();
                _audioAnchorMediaTime = correction;
                try { _audioAnchorDevicePosition = _audioOutput.GetPosition(); }
                catch { _audioAnchorDevicePosition = 0; }
                _audioAnchorTimestamp = Stopwatch.GetTimestamp();
            }
            AppLog.Debug($"Editor A/V correction applied: anchor={correction.TotalSeconds:0.###}s, generation={generation}.");
        }
        finally
        {
            try { _seekLock.Release(); } catch (ObjectDisposedException) { }
        }
    }

    private void StopAudioClockMonitoring()
    {
        if (_audioDriftHandler is null) return;
        VideoPlayer.TimeChanged -= _audioDriftHandler;
        _audioDriftHandler = null;
    }

    private sealed class PlaybackSeekTransport(PlaybackSession session, long generation) : IEditorSeekTransport
    {
        private TimeSpan _audioAnchor;

        public bool IsPaused => session.VideoPlayer.State == VLCState.Paused;
        public TimeSpan Position => TimeSpan.FromMilliseconds(Math.Max(0, session.VideoPlayer.Time));

        public void StopAudio()
        {
            lock (session._transportLock)
            {
                session.StopAudioClockMonitoring();
                session._audioOutput?.Stop();
                session.ForceVideoSilent();
            }
        }

        public void PauseVideo()
        {
            lock (session._transportLock)
            {
                session.ForceVideoSilent();
                if (session.IsEnded || session.VideoPlayer.State == VLCState.Stopped)
                {
                    session.VideoPlayer.Stop();
                    session._ended = false;
                    session.VideoPlayer.Play();
                }
                else if (session.VideoPlayer.State != VLCState.Paused && !session.VideoPlayer.IsPlaying)
                {
                    // A just-loaded LibVLC player is often NothingSpecial;
                    // it must be started once before a pause/Time sequence is
                    // accepted by the transport.
                    session.VideoPlayer.Play();
                }
                session.VideoPlayer.SetPause(true);
            }
        }

        public void WritePosition(TimeSpan target)
        {
            lock (session._transportLock) session.VideoPlayer.Time = (long)target.TotalMilliseconds;
        }

        public void ResetVideo()
        {
            lock (session._transportLock)
            {
                session.ForceVideoSilent();
                session.VideoPlayer.Stop();
                session._ended = false;
                session.VideoPlayer.Play();
                session.VideoPlayer.SetPause(true);
            }
        }

        public void ResumeVideo()
        {
            lock (session._transportLock) session.VideoPlayer.SetPause(false);
        }

        public void AnchorAudio(TimeSpan position)
        {
            lock (session._transportLock)
            {
                session.EnsureAudioOutputConnected();
                session.SeekAudio(position);
                _audioAnchor = position;
            }
        }

        public void StartAudio()
        {
            lock (session._transportLock) session.StartAudioAt(_audioAnchor, generation);
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
            StopAudioClockMonitoring();
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
