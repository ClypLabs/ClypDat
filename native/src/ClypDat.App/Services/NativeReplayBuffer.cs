using ClypDat.Capture.Abstractions;
using FFmpeg.AutoGen;
using System.Buffers;
using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text.Json;
using SharpGen.Runtime;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;
using ID3D11Device = Vortice.Direct3D11.ID3D11Device;
using ID3D11Texture2D = Vortice.Direct3D11.ID3D11Texture2D;
using ID3D11VideoDevice = Vortice.Direct3D11.ID3D11VideoDevice;
using ID3D11VideoContext = Vortice.Direct3D11.ID3D11VideoContext;
using ResultCode = Vortice.DXGI.ResultCode;
using MapFlags = Vortice.Direct3D11.MapFlags;
using CpuAccessFlags = Vortice.Direct3D11.CpuAccessFlags;
using BindFlags = Vortice.Direct3D11.BindFlags;
using ResourceUsage = Vortice.Direct3D11.ResourceUsage;
using MapMode = Vortice.Direct3D11.MapMode;
using Texture2DDescription = Vortice.Direct3D11.Texture2DDescription;
using D3D11 = Vortice.Direct3D11.D3D11;
using DeviceCreationFlags = Vortice.Direct3D11.DeviceCreationFlags;

namespace ClypDat.App.Services;

// Native capture engine: direct h264_nvenc/amf/libx264 encode via libavcodec
// (FFmpeg.AutoGen P/Invoke) and a true in-memory packet ring buffer - replacing
// WindowsReplayBuffer's stop/start ScreenRecorderLib segment rotation (and the
// real-time gap that model has at every rotation boundary) with an encoder that
// never stops during normal operation.
//
// Capture source is DXGI Desktop Duplication (IDXGIOutputDuplication), cropped to
// the target window's rect every frame, instead of Windows.Graphics.Capture -
// WGC's FrameArrived delivery measured a hard ~40-46fps ceiling on this hardware
// regardless of target fps/resolution (confirmed via GPUView trace: the GPU
// queues themselves and DWM's own composition rate both run far faster, so the
// bottleneck was specifically WGC's internal frame pacing, invisible to and
// unfixable from application code - ten targeted fixes to buffer depth,
// threading, timer resolution, and consumption model all measured zero effect).
// Desktop Duplication is same lower-level API used by this app's Legacy/
// ScreenRecorderLib backend, proven to hit full target fps on
// this same machine.
//
// The tradeoff: Desktop Duplication captures the composited desktop, not a
// specific window's content directly, so it can't stay "attached" to a window
// through occlusion the way WGC does - if another window covers the crop
// region, duplication would show that window's content instead. Solved by
// checking whether the target window is actually the foreground window every
// frame; when it isn't (alt-tabbed away, minimized, covered), the last
// successfully captured frame is re-submitted to the encoder instead of a fresh
// (potentially other-app) capture, so the recording visually freezes rather
// than leaking other windows' content. Each freeze/resume transition is logged
// as a wall-clock event so SaveReplayAsync can tell the editor which parts of a
// saved clip were frozen, via a "Recording Paused" sidecar.
//
// Same DWM dependency also explains low/laggy capture when the GAME has
// vsync off: AcquireNextFrame only gets a new frame on DWM's own present/
// composition cadence, and an uncapped/tearing game presents outside that
// cadence, so DWM skips composing most of its frames and duplication starves
// - confirmed via avgPresentGapMs in the diag log spiking to hundreds of ms
// (vs. ~13ms at a clean 60fps) during exactly this symptom. Turning the
// game's vsync on locks its presents to refresh, so DWM composes every one
// and duplication gets them reliably again. Nothing to fix here - not
// something Desktop Duplication can be worked around from application code.
//
// Falls back to capturing the primary monitor (no crop, no occlusion pausing)
// when no game window is detected. NVENC only for now (no software fallback -
// machines without NVENC should stay on Legacy). Audio reuses
// AudioCapturePipeline - the same Game/Chat/Microphone routing, WASAPI capture, and mux
// logic WindowsReplayBuffer uses, via its own independent instance.
[SupportedOSPlatform("windows10.0.17763.0")]
public sealed class NativeReplayBuffer : IReplayBuffer, IReplayCaptureDiagnostics, IAdaptiveCaptureFrameRate
{
    private readonly Func<ReplayBufferConfig> _configProvider;
    private readonly string _bufferFolder;
    private readonly AudioCapturePipeline _audio;
    private readonly object _bufferLock = new();
    private readonly List<RingPacket> _packets = new();
    // Running total of _packets' payload bytes, maintained on add/trim under
    // _bufferLock. The diagnostic that reports this used to sum the whole list
    // every 2 seconds - a LINQ walk over up to 14000 entries while holding the
    // one lock the encode thread needs for every packet it produces.
    private long _ringBufferBytes;
    // Live target frame rate. The capture loop owns the pacing interval and
    // reads this once per iteration; anything outside the loop asks via
    // RequestFrameRate rather than touching the interval directly.
    private int _requestedFrameRate;
    // Recording-paused transitions (see class summary) - trimmed alongside
    // _packets so this never grows unbounded across a long session.
    private readonly List<PauseEvent> _pauseEvents = new();

    private CancellationTokenSource? _captureCts;
    private Task? _captureTask;
    private Task? _backgroundFinalize;
    // Guards StartAsync's orphan-WAV sweep across the app: a background
    // finalize still owns capture WAVs after its session stopped, and a new
    // session starting meanwhile must not sweep them out from under it.
    private static int _activeBackgroundFinalizes;
    private volatile bool _sessionActive;
    private AVRational _timeBase = new() { num = 1, den = 1_000_000 };
    private byte[]? _extraData;
    private int _outputWidth;
    private int _outputHeight;
    // Ticks of the last moment genuinely new captured content was scaled into
    // the encoder's frame - the ring buffer alone can't answer "was this clip
    // real?", because a stalled source still produces a full ring of packets,
    // all of them the same padded frame. Written from CaptureLoop, read from
    // SaveReplayAsync on a different thread, so it goes through Volatile.
    private long _lastRealContentTicks;
    private volatile bool _lastSaveVideoWasFrozen;

    public bool LastSaveVideoWasFrozen => _lastSaveVideoWasFrozen;
    // Encode-thread diagnostics (see EncodeLoop) - written with Interlocked from
    // that thread, read/reset from CaptureLoop's own periodic diag line. Plain
    // instance fields are safe here since only one capture session (and so only
    // one encode thread) is ever active at a time.
    private long _encodeMicrosAccum;
    private long _encodeCountAccum;
    private long _encodeDroppedCount;
    private long _totalDroppedFrames;
    private int _peakQueueDepth;
    private DateTime? _lastDegradedUtc;
    private ReplayCaptureHealth _health = ReplayCaptureHealth.Unknown("Native");

    public NativeReplayBuffer(Func<ReplayBufferConfig> configProvider)
    {
        _configProvider = configProvider;
        _bufferFolder = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "ClypDat",
            "native-replay-buffer");
        _audio = new AudioCapturePipeline(_bufferFolder);
    }

    public bool IsRecording => _sessionActive;

    // Retargets pacing on a running session. Cheap and safe mid-session: only
    // how often a frame is scheduled changes, so the encoder, the GPU scaler
    // and the stream's parameters all stay exactly as they were and packets
    // recorded either side of the change still mux into a single clip.
    //
    // Resolution deliberately has no equivalent. Changing it means rebuilding
    // the encoder, which would leave the ring holding packets of two different
    // sizes - unmuxable into one file - so the only honest implementation would
    // discard the user's buffered replay history at the exact moment the
    // machine is struggling. Frame rate is the lever that costs nothing.
    public void RequestFrameRate(int frameRate)
    {
        if (!_sessionActive) return;
        Volatile.Write(ref _requestedFrameRate, Math.Clamp(frameRate, 15, 240));
    }

    public TimeSpan Duration { get; private set; } = TimeSpan.FromSeconds(60);
    public event EventHandler? RecordingStopped;
    public event EventHandler<ReplayCaptureHealth>? HealthChanged;

    public ReplayCaptureHealth GetHealthSnapshot() => _health;

    private void SetHealth(ReplayCaptureHealth health)
    {
        _health = health;
        HealthChanged?.Invoke(this, health);
    }

    private static void UpdatePeak(ref int value, int candidate)
    {
        var current = Volatile.Read(ref value);
        while (candidate > current)
        {
            var observed = Interlocked.CompareExchange(ref value, candidate, current);
            if (observed == current) return;
            current = observed;
        }
    }

    public Task StartAsync(CancellationToken cancellationToken = default)
    {
        if (_sessionActive) return Task.CompletedTask;

        Directory.CreateDirectory(_bufferFolder);
        if (Volatile.Read(ref _activeBackgroundFinalizes) == 0)
        {
            CleanupOldFiles();
        }
        else
        {
            AppLog.Info("Native replay start: skipping orphan-WAV sweep, a background session finalize still owns capture files.");
        }

        var config = _configProvider();
        Duration = TimeSpan.FromSeconds(Math.Clamp(config.DurationSeconds, 30, 1200));

        // Captured once per session from the fresh encoder's SPS/PPS (see
        // CaptureLoop) - without resetting it here, a resolution change +
        // Restart Buffer opens a new encoder at the new size but keeps muxing
        // clips with the PREVIOUS session's stale extradata, which still
        // declares the old resolution. The container's declared size then
        // doesn't match the actual encoded frame data, producing exactly the
        // stride-mismatch smearing/corruption reported after a resolution
        // change.
        _extraData = null;
        Interlocked.Exchange(ref _totalDroppedFrames, 0);
        Volatile.Write(ref _peakQueueDepth, 0);
        _lastDegradedUtc = null;
        lock (_bufferLock) _pauseEvents.Clear();

        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _captureCts = new CancellationTokenSource();
        var token = _captureCts.Token;

        // Set BEFORE starting the capture loop, not after. CaptureLoop runs on
        // its own thread and can fail almost immediately (e.g. the encoder
        // isn't available at all), and its catch sets _sessionActive = false.
        // Assigning true after StartNew raced that: _audio.Start below takes
        // hundreds of ms (device enumeration, per-process loopback init), so a
        // fast failure set false first and then this overwrote it back to true.
        // The buffer then reported IsRecording forever with an empty ring, and
        // every clip attempt surfaced the ring's "Replay just started. Try
        // again in a second." instead of the actual start failure.
        _sessionActive = true;
        SetHealth(new ReplayCaptureHealth("Native", "Desktop Duplication", ReplayCaptureState.Starting,
            config.FrameRate, 0, 0, 0, 0, 0, 0, string.Empty, string.Empty, string.Empty, DateTime.UtcNow));
        _captureTask = Task.Factory.StartNew(
            () => CaptureLoop(token, ready),
            token,
            TaskCreationOptions.LongRunning,
            TaskScheduler.Default);

        try
        {
            _audio.Start(config);
        }
        catch
        {
            _sessionActive = false;
            throw;
        }

        return ready.Task;
    }

    // Sweeps orphaned raw audio WAVs left behind by an ungraceful shutdown
    // (crash, Task Manager kill, power loss) - the normal Stop()/PruneOlderThan
    // paths delete these as they go, but neither runs if the process never got
    // to call them. Safe to run unconditionally at StartAsync: nothing is
    // actively writing to this folder yet since no capture has started.
    private void CleanupOldFiles()
    {
        foreach (var file in Directory.EnumerateFiles(_bufferFolder, "game_*.wav")
                     .Concat(Directory.EnumerateFiles(_bufferFolder, "chat_*.wav"))
                     .Concat(Directory.EnumerateFiles(_bufferFolder, "microphone_*.wav"))
                     .Concat(Directory.EnumerateFiles(_bufferFolder, "audio_*.wav"))
                     .Concat(Directory.EnumerateFiles(_bufferFolder, "audio_*.txt")))
        {
            AudioCapturePipeline.TryDelete(file);
        }
    }

    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        if (!_sessionActive) return;
        _sessionActive = false;
        _captureCts?.Cancel();
        if (_captureTask is not null)
        {
            try { await _captureTask; }
            catch (OperationCanceledException) { }
        }

        // When a background finalize is running it took a snapshot of the
        // capture set and deletes the WAVs itself once the session file is
        // complete - deleting them here would yank them out from under it.
        _audio.Stop(deleteCaptureFiles: _backgroundFinalize is null || _backgroundFinalize.IsCompleted);
        lock (_bufferLock)
        {
            ReturnPooledPackets(0, _packets.Count);
            _packets.Clear();
            _ringBufferBytes = 0;
            _pauseEvents.Clear();
        }
    }

    public async Task<string> SaveReplayAsync(string outputFolder, CancellationToken cancellationToken = default, string? titleOverride = null, ReplayClipWindow? clipWindow = null)
    {
        if (!_sessionActive) throw new InvalidOperationException("Replay buffer is not recording.");

        var requestedStartUtc = clipWindow?.StartUtc ?? MonotonicClock.UtcNow - Duration;
        var requestedEndUtc = clipWindow?.EndUtc ?? MonotonicClock.UtcNow;
        if (requestedEndUtc <= requestedStartUtc)
        {
            throw new InvalidOperationException("The requested replay window is empty.");
        }

        // Selects and copies in one locked pass - see CopyWindowUnderLock for
        // why the copy is what keeps the ring's pooled payloads safe.
        var window = CopyWindowUnderLock(requestedStartUtc, requestedEndUtc);

        var config = _configProvider();
        var clipName = string.IsNullOrWhiteSpace(titleOverride) ? config.GameDisplayName : titleOverride;
        var gameFolder = Path.Combine(outputFolder, ClipFileNaming.BuildBaseName(config.GameDisplayName));
        Directory.CreateDirectory(gameFolder);
        var outputPath = ClipFileNaming.BuildUniquePath(gameFolder, ClipFileNaming.BuildFileName(clipName, DateTime.Now, "mp4", config.ClipFileNameScheme, config.CustomClipFileNameTemplate, config.GameDisplayName));

        // A capture source can go silent while every stage after it keeps
        // working: the pacing gate pads the last frame, the ring fills with
        // packets, the save succeeds, and the clip is one frozen frame stretched
        // over its full length. The packets can't reveal that - they look
        // completely normal - so compare the window against the last moment new
        // content actually reached the encoder. Not fatal: the audio tracks are
        // still worth keeping, so this only warns.
        var lastRealContentUtc = new DateTime(Volatile.Read(ref _lastRealContentTicks), DateTimeKind.Utc);
        _lastSaveVideoWasFrozen = lastRealContentUtc < window[0].WallClockUtc;
        if (_lastSaveVideoWasFrozen)
        {
            AppLog.Info($"Native replay: saved window contains no new video frames - the clip's video is frozen. Last new frame {(MonotonicClock.UtcNow - lastRealContentUtc).TotalSeconds:0}s ago, path={outputPath}.");
        }

        var tempVideoPath = Path.Combine(Path.GetTempPath(), $"clypdat-native-video-{Guid.NewGuid():N}.mp4");
        var snapshots = new List<string>();
        try
        {
            // Thousands of packet copies and disk writes back to back. Dropping
            // the pooled thread's priority for the duration keeps that burst
            // from competing with the game and the desktop compositor - the
            // body is fully synchronous, so the restore in the finally really
            // does bracket all of the work (the thread returns to the pool
            // afterwards, so it must be put back).
            // Stage timings, because "the save takes ages" is not something a
            // log full of per-stage silence can answer. A real report put 50.9s
            // between the hotkey and the saved toast with only the audio
            // snapshot lines to show for it.
            var saveTimer = System.Diagnostics.Stopwatch.StartNew();
            // Started, not awaited. The audio pipeline below needs nothing from
            // the remux (it works off `window`'s timestamps and its own capture
            // WAVs) and the remux needs nothing from it, but they used to run
            // strictly one after the other anyway - on a laptop that meant ~9s
            // of disk-bound remux followed by ~30s of CPU-bound ffmpeg, when
            // the two barely compete for the same resource. Awaited together
            // below, before the mux that is the first thing to need both.
            var remuxTask = Task.Run(() =>
            {
                var stageTimer = System.Diagnostics.Stopwatch.StartNew();
                var previousPriority = Thread.CurrentThread.Priority;
                Thread.CurrentThread.Priority = ThreadPriority.BelowNormal;
                try { RemuxWindowToMp4(window, tempVideoPath); }
                finally { Thread.CurrentThread.Priority = previousPriority; }
                return stageTimer.ElapsedMilliseconds;
            }, cancellationToken);

            // The ring buffer already remuxes exactly the desired window starting at a
            // real keyframe - no offset/trim needed here the way WindowsReplayBuffer's
            // keyframe-seek fallback requires.
            var windowStartUtc = window[0].WallClockUtc;
            var windowDurationSeconds = Math.Max(1, (window[^1].WallClockUtc - windowStartUtc).TotalSeconds);

            // Diagnostic only (see audio-desync investigation) - video's own
            // internal duration comes from Stopwatch-based PTS (monotonic,
            // high precision), while the audio segment above is sized from
            // wall-clock (DateTime.UtcNow) deltas between the same two
            // packets. If these disagree by more than a few ms, the audio
            // track gets built to a different total length than the video
            // actually has, which wouldn't just be a start offset - it'd get
            // worse toward the end of the clip.
            var videoDurationSeconds = (window[^1].PtsMs - window[0].PtsMs) / 1_000_000.0;
            AppLog.Debug($"Native replay audio/video duration check: videoDurationSeconds={videoDurationSeconds:0.000}, audioWindowDurationSeconds={windowDurationSeconds:0.000}, deltaMs={(windowDurationSeconds - videoDurationSeconds) * 1000:0.0}, packetCount={window.Length}.");

            // A capture stall (the loop goes an extended stretch without
            // acquiring/encoding a frame - seen under heavy GPU load, driver
            // hiccups, etc.) leaves real gaps in the ring buffer: fewer video
            // packets than wall-clock time would suggest, while audio (an
            // entirely separate capture pipeline) keeps recording the whole
            // window regardless. Left uncorrected, the saved clip gets an
            // audio track much longer than its video track, which plays back
            // as "video freezes, audio keeps going" for however long the
            // shortfall is. Trimming the audio window down to the real video
            // length (keeping the END - the moment closest to the save
            // request - and cutting from the front) turns that into a
            // shorter but correctly synced clip instead.
            //
            // The threshold used to be a full second, which is roughly 20x the
            // point at which a human hears audio and picture come apart. On a
            // machine whose encoder is struggling the shortfall lands squarely
            // in the gap that left open: measured saves on an Iris Xe laptop
            // came in at 323ms and 486ms of a ~60s clip, both waved through
            // uncorrected. The audio then plays a window that covers more real
            // time than the video does, so the two drift steadily further
            // apart toward the end of the clip - loudest on game audio, where
            // gunshots and footsteps make a third of a second obvious in a way
            // voice chat does not. 50ms is about where a mismatch stops being
            // audible; below it the correction is not worth the frames.
            const double MaxAudioVideoDeltaSeconds = 0.05;
            if (windowDurationSeconds - videoDurationSeconds > MaxAudioVideoDeltaSeconds)
            {
                var message = $"Native replay: video came up short ({videoDurationSeconds:0.000}s of {windowDurationSeconds:0.000}s requested) - trimming audio to match.";
                // A second-plus shortfall is a capture stall and worth saying
                // out loud; the sub-second ones are routine on a loaded
                // machine and would only spam the log.
                if (windowDurationSeconds - videoDurationSeconds > 1.0) AppLog.Info(message);
                else AppLog.Debug(message);
                windowStartUtc = window[^1].WallClockUtc - TimeSpan.FromSeconds(videoDurationSeconds);
                windowDurationSeconds = videoDurationSeconds;
            }

            // One giant segment spanning the whole saved window let audio/video
            // clock drift (real hardware sample clocks are never exactly
            // 48000.000000Hz) accumulate uncorrected across the entire clip -
            // FinalizeFullSessionRecording already chunks its (much longer)
            // window into 60s segments with a periodic resync for exactly this
            // reason, but a regular clip save at the default 60s replay length
            // is long enough to hit the same drift, just less obviously since
            // it's usually the ONLY segment. Chunking here the same way fixes it
            // for any configured replay length, not just multi-hour sessions.
            const double SegmentChunkSeconds = 60;
            var segmentWindows = new List<(DateTime StartUtc, double DurationSeconds)>();
            var chunkStartUtc = windowStartUtc;
            var remainingSeconds = windowDurationSeconds;
            while (remainingSeconds > 0)
            {
                var chunkSeconds = Math.Min(SegmentChunkSeconds, remainingSeconds);
                // Absorb a runt tail rather than emitting a segment for it. A
                // 60s replay buffer measures a hair over 60s of wall clock, so
                // the old loop produced a full segment plus a ~1.3s one - and
                // every segment costs an ffmpeg process PER TRACK, plus a
                // concat pass per track that a single segment does not need at
                // all. That turned the common 3-process save into 9, on the
                // machines least able to afford it. Drift over 61s is no worse
                // than over 60, which is why the 60s figure was approximate to
                // begin with.
                if (remainingSeconds - chunkSeconds < SegmentChunkSeconds / 2)
                {
                    chunkSeconds = remainingSeconds;
                }

                segmentWindows.Add((chunkStartUtc, chunkSeconds));
                chunkStartUtc += TimeSpan.FromSeconds(chunkSeconds);
                remainingSeconds -= chunkSeconds;
            }

            WritePausedRangesSidecar(config.LibraryFolder, outputPath, ComputePausedRangesSeconds(GetOrderedPauseEvents(), windowStartUtc, windowStartUtc + TimeSpan.FromSeconds(windowDurationSeconds)));

            var tracksStartMs = saveTimer.ElapsedMilliseconds;
            List<(string Label, string Path)> tracks;
            try
            {
                tracks = await _audio.BuildAlignedTracksAsync(segmentWindows, config, snapshots, cancellationToken);
            }
            catch
            {
                // Let the remux finish before the finally deletes the file it
                // is still writing, and observe its own failure if it had one.
                try { await remuxTask; } catch { /* the audio failure is the one worth reporting */ }
                throw;
            }

            var tracksMs = saveTimer.ElapsedMilliseconds - tracksStartMs;
            // The mux is the first stage that needs the video file, so this is
            // where the concurrent remux gets collected. Usually already done.
            var remuxMs = await remuxTask;
            var remuxWaitMs = saveTimer.ElapsedMilliseconds - tracksStartMs - tracksMs;

            var muxStartMs = saveTimer.ElapsedMilliseconds;
            var muxArgs = new List<string> { "-y", "-i", tempVideoPath };
            foreach (var track in tracks) muxArgs.AddRange(new[] { "-i", track.Path });
            muxArgs.AddRange(new[] { "-map", "0:v" });
            for (var i = 0; i < tracks.Count; i++) muxArgs.AddRange(new[] { "-map", $"{i + 1}:a" });
            muxArgs.AddRange(new[] { "-c:v", "copy", "-c:a", "aac", "-b:a", "192k" });
            for (var i = 0; i < tracks.Count; i++) muxArgs.AddRange(new[] { $"-metadata:s:a:{i}", $"title={tracks[i].Label}" });
            muxArgs.AddRange(new[] { "-metadata", $"comment={ClipMetadataTagger.BuildCommentValue("Native")}", outputPath });
            var result = await AudioCapturePipeline.RunProcessAsync("ffmpeg", muxArgs, cancellationToken);
            if (result.ExitCode != 0)
            {
                throw new InvalidOperationException(string.IsNullOrWhiteSpace(result.Error) ? "ffmpeg mux failed." : result.Error);
            }

            AppLog.Info($"Native replay save timings: totalMs={saveTimer.ElapsedMilliseconds}, remuxMs={remuxMs} (waited {remuxWaitMs}), audioTracksMs={tracksMs}, muxMs={saveTimer.ElapsedMilliseconds - muxStartMs}, tracks={tracks.Count}, segments={segmentWindows.Count}, packets={window.Length}.");
        }
        finally
        {
            AudioCapturePipeline.TryDelete(tempVideoPath);
            foreach (var snapshot in snapshots) AudioCapturePipeline.TryDelete(snapshot);
        }

        AppLog.Info($"Native replay saved: path={outputPath}, packets={window.Length}.");
        return outputPath;
    }

    public void SetCapturePaused(bool paused)
    {
        // No-op for now: the capture loop always runs while a session is active.
        // A pause would need to stop encoding without tearing down the ring buffer,
        // not currently needed since nothing calls this on the Windows Capture path.
    }

    public void Dispose()
    {
        _captureCts?.Cancel();
        _captureCts?.Dispose();
    }

    private unsafe void CaptureLoop(CancellationToken token, TaskCompletionSource ready)
    {
        // Every save (auto or manual) runs RemuxWindowToMp4 - a synchronous,
        // single-threaded loop of thousands of native FFmpeg calls writing
        // straight to disk - on a plain Task.Run threadpool thread at Normal
        // priority, same as this loop. On a system without much CPU headroom,
        // that's enough to starve this thread of scheduling for a second or
        // more at a time: per-frame GPU/encode costs stay normal during a
        // stall (see Native capture diag's avgScaleMs/avgEncodeMs), but the
        // loop's own iteration rate collapses, which is what "video freezes/
        // stutters right when a clip saves - even via the manual hotkey"
        // turned out to be. AboveNormal (not Highest, which risks starving
        // the OS's own threads if held for this thread's entire multi-hour
        // capture-session lifetime) gives the scheduler a reason to favor
        // this over that remux work specifically during the contention.
        try
        {
            Thread.CurrentThread.Priority = ThreadPriority.AboveNormal;
        }
        catch (Exception error)
        {
            AppLog.Error("Native capture: failed to raise capture thread priority (non-fatal)", error);
        }

        ID3D11Device? device = null;
        ID3D11Texture2D? staging = null;
        IDXGIOutputDuplication? duplication = null;
        // GPU-side downscale path (see TrySetupGpuScale) - only actually used
        // when useGpuScale ends up true; `staging`/swsContext above always
        // still get created too so there's a guaranteed-working fallback if
        // GPU scale setup fails on this hardware/driver.
        ID3D11VideoDevice? videoDevice = null;
        ID3D11VideoContext? videoContext = null;
        ID3D11VideoProcessorEnumerator? vpEnumerator = null;
        ID3D11VideoProcessor? videoProcessor = null;
        ID3D11Texture2D? croppedTexture = null;
        ID3D11Texture2D? nv12Output = null;
        // A ring of staging textures rather than one: Map() without DoNotWait
        // blocks until the GPU finishes EVERYTHING queued before it, and
        // mapping the texture we just issued a CopyResource into forces exactly
        // that stall (measured 12-14ms, wildly variable with GPU load -
        // defeating much of the point of GPU scale). Writing one slot while
        // reading an older one whose copy has had time to finish avoids it -
        // standard multi-buffered GPU readback. See the read logic in
        // EncodeScheduledFrame for how a slot is chosen.
        ID3D11Texture2D[]? nv12StagingRing = null;
        var nv12StagingIndex = 0;
        // How many slots hold a copy that was actually issued. Only matters
        // while the ring fills for the first time; after that every slot has
        // been written at least once and this pins at the ring's length.
        var nv12RingWritten = 0;
        ID3D11VideoProcessorOutputView? outputView = null;
        ID3D11VideoProcessorInputView? inputView = null;
        var useGpuScale = false;
        // Set when a present has landed fresh pixels in croppedTexture that
        // have not been converted into frame->data yet. The crop copy runs per
        // present (it has to - the duplication frame is released immediately
        // after), but the conversion it feeds only runs per ENCODED frame, so
        // this is what tells the encode tick whether there is anything new to
        // convert or whether it should just re-encode the last content as a
        // duplicate. See the encode-time conversion in EncodeScheduledFrame.
        var croppedDirty = false;
        // Set when the scale/convert Blt already ran at present time straight
        // off the duplication texture, so the encode tick only has to read
        // nv12Output back rather than produce it. See directBltAvailable.
        var nv12Ready = false;
        // The crop copy above exists purely to outlive the duplication frame.
        // It is also a full-resolution BGRA copy - at 4K that is 33MB read plus
        // 33MB written, and the gate below permits it up to TWICE per encode
        // interval, so a busy screen pays ~8GB/s for it. Measured: this app's
        // 3D-engine share sits at ~6.5% on a static desktop and climbs past 25%
        // when the screen is changing, which is the shape of exactly this copy
        // and nothing else in the pipeline (the Blt and the readback are both
        // pinned to the encode rate).
        //
        // It is avoidable. The Video Processor can read the duplication texture
        // directly with a source rect, so the crop happens as part of the scale
        // it was feeding instead of as a separate pass beforehand. That means
        // doing the Blt while the frame is still acquired rather than deferring
        // it, which costs nothing: the gate below already limits this block to
        // roughly the encode rate, which is the rate the deferred Blt ran at.
        //
        // Falls back to the copy for the rest of the session if a driver will
        // not hand out an input view for a duplication texture.
        // CLYPDAT_DISABLE_DIRECT_BLT=1 forces the old staging-copy path, so the
        // two can be measured against each other on one build.
        var directBltAvailable = Environment.GetEnvironmentVariable("CLYPDAT_DISABLE_DIRECT_BLT") != "1";
        // Input views are per-texture, and Desktop Duplication rotates a small
        // pool of them, so these are cached by native pointer rather than
        // rebuilt per frame. Disposed with the rest of the D3D state.
        var desktopInputViews = new Dictionary<nint, ID3D11VideoProcessorInputView>();
        // Cursor position for the GPU path, already converted into OUTPUT-resolution
        // pixels at crop time. The crop block is the only place the crop origin and
        // capture size are in scope, but the cursor has to be drawn after the scaled
        // readback (drawing it before would mean the video processor resamples the
        // arrow along with the frame). int.MinValue means "no cursor this frame".
        var cursorOutputX = int.MinValue;
        var cursorOutputY = int.MinValue;
        AVCodecContext* codecContext = null;
        SwsContext* swsContext = null;
        AVFrame* frame = null;
        AVPacket* packet = null;
        AVFormatContext* fullSessionFormatContext = null;
        AVStream* fullSessionStream = null;
        // Encode (avcodec_send_frame/receive_packet, both of which can block on
        // NVENC for a while under real GPU contention - see EncodeLoop) runs on
        // its own thread so a slow encode call never blocks AcquireNextFrame on
        // the thread below it. Declared out here (not inside the try) so the
        // finally block can always drain/join it before codecContext gets freed,
        // on every exit path including an exception mid-loop.
        BlockingCollection<EncodeJob>? encodeQueue = null;
        Thread? encodeThread = null;
        // Zero-copy encode state (see TryCreateD3D11EncodeFrames). Both stay 0
        // on the system-memory path, which is what every check below tests for.
        nint hwDeviceRef = 0;
        nint hwFramesRef = 0;
        // Whether anything has ever been scaled into nv12Output, which is the
        // texture every hardware frame is copied from - a padding tick is only
        // meaningful once there is content there to repeat.
        var hasHardwareContent = false;
        // Vortice wrappers over the pool's texture pointers. ffmpeg hands back
        // a raw ID3D11Texture2D*; wrapping it fresh per frame would allocate 60
        // times a second, and disposing a wrapper would Release a reference the
        // pool owns - so these are cached for the session and never disposed.
        var hardwarePoolTextures = new Dictionary<nint, ID3D11Texture2D>();
        // Encoders retired by a mid-session swap (see EncodeJob). Freed only
        // after the encode thread has joined - it may still be inside one.
        var retiredCodecContexts = new List<nint>();
        var fullSessionTempVideoPath = string.Empty;
        var fullSessionFinalOutputPath = string.Empty;
        var fullSessionStartUtc = MonotonicClock.UtcNow;
        // Real wall-clock twin of fullSessionStartUtc, only for the sidecar's
        // user-facing CreatedAt - all alignment math stays on MonotonicClock.
        var fullSessionStartWallUtc = DateTime.UtcNow;
        var fullSessionGameDisplayName = string.Empty;
        var timerResolutionRaised = TimeBeginPeriod(1) == 0;

        try
        {
            var config = _configProvider();
            device = CreateD3D11Device();

            var targetHandle = ResolveTargetWindow(config);
            var isMonitorMode = targetHandle == 0;
            // Which output the duplication below is actually bound to - see the
            // once-a-second target recheck in the loop for why the window handle
            // alone is the wrong thing to compare against.
            var targetMonitor = ResolveTargetMonitor(targetHandle, config);
            duplication = CreateDuplicationFor(device, targetHandle, config, out var desktopBounds);

            var (captureWidth, captureHeight) = isMonitorMode
                ? (desktopBounds.Right - desktopBounds.Left, desktopBounds.Bottom - desktopBounds.Top)
                : GetInitialCropSize(targetHandle, desktopBounds);

            var (outputWidth, outputHeight) = CaptureOutputSize(config, captureWidth, captureHeight);
            _outputWidth = outputWidth;
            _outputHeight = outputHeight;

            // Debounces the crop-size-changed resource rebuild below (staging
            // texture, scaler, GPU crop view) against a single-iteration blip
            // in DwmGetWindowAttribute's reported bounds. Alt-tabbing back into
            // a window plays a compositor restore animation for a few frames
            // (Windows' own alt-tab switcher, unless the user has disabled
            // animations), during which the extended frame bounds genuinely
            // read different sizes iteration to iteration before settling -
            // rebuilding real GPU resources on every one of those transient
            // reads, right as gameplay resumes, is expensive enough on its own
            // to look exactly like the encoder overload this was chasing.
            // Requiring the same new size to repeat a few times before
            // committing to a rebuild costs at most a few iterations' (tens of
            // ms) latency on a genuine resize, which is imperceptible, while
            // skipping the rebuild entirely for a same-frame-or-two blip that
            // never repeats.
            var pendingCropWidth = captureWidth;
            var pendingCropHeight = captureHeight;
            var pendingCropStableCount = 0;
            const int cropStableThreshold = 3;

            staging = CreateStagingTexture(device, captureWidth, captureHeight);

            // Copying+CPU-scaling the full captured crop (often the game's
            // native 4K render size) down to the output resolution every
            // frame measured ~17-18ms/frame by itself - most of a 60fps
            // frame budget - and is what's actually capping fps now that
            // Desktop Duplication itself isn't the bottleneck anymore.
            // Scaling on the GPU instead means only the already-small output
            // resolution ever gets read back to the CPU. Best-effort: if
            // this hardware/driver doesn't support it, useGpuScale stays
            // false and the CPU sws_scale path above still works normally.
            // CaptureCursor used to disable this whole path ("desktop cursor uses CPU
            // composition"), which quietly cost anyone recording with the cursor on
            // the entire GPU scaler: a 2560x1440 -> 1920x1080 sws_scale measured
            // 10-13ms per frame of a 16.7ms budget at 60fps, so capture stuttered on
            // machines whose GPU was nearly idle. The cursor is a synthetic arrow, not
            // a sampled cursor image, so it does not need the full-resolution BGRA
            // buffer at all - it is drawn into the NV12 frame after the scaled
            // readback instead (see DrawDesktopCursorNv12).
            try
            {
                (videoDevice, videoContext, vpEnumerator, videoProcessor, nv12Output, nv12StagingRing, outputView) =
                    CreateGpuScaler(device, captureWidth, captureHeight, outputWidth, outputHeight, config.FrameRate);
                (croppedTexture, inputView) = CreateGpuCropInputView(device, videoDevice, vpEnumerator, captureWidth, captureHeight);
                useGpuScale = true;
                AppLog.Info("Native capture: GPU downscale (D3D11 Video Processor) available, using it.");
            }
            catch (Exception error)
            {
                AppLog.Info($"Native capture: GPU downscale unavailable, falling back to CPU scale: {error.Message}");
                useGpuScale = false;
            }

            // Queue depth is decided before the encoder, because the D3D11 frame
            // pool has to cover it: every queued frame holds a VRAM surface.
            // Keep at most roughly half a second of stale work. A 120-frame queue
            // at 60 FPS turned a short GPU stall into seconds of old frames, then
            // magnified it with catch-up work. Dropping early is recoverable;
            // encoding stale frames is visible lag.
            var encodeQueueCapacity = Math.Clamp(config.FrameRate / 2, 8, 60);

            // Zero-copy needs the Video Processor's NV12 output to copy from,
            // so it only applies when the GPU scaler is up. The desktop cursor
            // is composited into the frame on the CPU (DrawDesktopCursorNv12)
            // and there is nothing on the GPU path to draw it with, so a
            // cursor-enabled capture keeps the system-memory frames it needs.
            if (useGpuScale && !config.CaptureCursor)
            {
                (hwDeviceRef, hwFramesRef) = TryCreateD3D11EncodeFrames(
                    device, outputWidth, outputHeight, encodeQueueCapacity + HardwareFramePoolHeadroom);
            }

            codecContext = CreateEncoder(config, outputWidth, outputHeight, hwFramesRef, out var codecTimeBase, out var encoderName, out var hardwareFramesActive);
            _timeBase = codecTimeBase;

            if (!hardwareFramesActive) ReleaseHardwareFrames(ref hwDeviceRef, ref hwFramesRef);

            if (InitFullSessionWriter(config, codecContext, out fullSessionFormatContext, out fullSessionStream, out fullSessionTempVideoPath, out fullSessionFinalOutputPath))
            {
                fullSessionStartUtc = MonotonicClock.UtcNow;
                fullSessionStartWallUtc = DateTime.UtcNow;
                fullSessionGameDisplayName = config.GameDisplayName;
            }

            swsContext = CreateScaler(captureWidth, captureHeight, outputWidth, outputHeight);

            frame = ffmpeg.av_frame_alloc();
            frame->format = (int)AVPixelFormat.AV_PIX_FMT_NV12;
            frame->width = outputWidth;
            frame->height = outputHeight;
            ffmpeg.av_frame_get_buffer(frame, 32);
            // av_frame_get_buffer leaves the buffer uninitialized - if the
            // target window starts occluded (recording begins before the game
            // has focus, common when starting the buffer from ClypDat's own
            // window), the very first frames get encoded straight from that
            // garbage NV12 data before any real capture ever lands in it,
            // which renders as solid green (Y/U/V all near zero decodes to
            // bright green in YUV->RGB). Fill it to black up front instead.
            FillFrameBlack(frame, outputHeight);

            packet = ffmpeg.av_packet_alloc();

            encodeQueue = new BlockingCollection<EncodeJob>(boundedCapacity: encodeQueueCapacity);
            // Pointer locals can't be captured by a lambda closure directly - cross
            // the thread boundary as nint instead, cast back inside EncodeLoop.
            var encodeCodecContextPtr = (nint)codecContext;
            var encodePacketPtr = (nint)packet;
            var encodeFullSessionFormatContextPtr = (nint)fullSessionFormatContext;
            var encodeFullSessionStreamPtr = (nint)fullSessionStream;
            encodeThread = new Thread(() => EncodeLoop(encodeQueue, encodeCodecContextPtr, encodePacketPtr, encodeFullSessionFormatContextPtr, encodeFullSessionStreamPtr))
            {
                IsBackground = true,
                Name = "ClypDat-NativeEncode"
            };
            try { encodeThread.Priority = ThreadPriority.AboveNormal; }
            catch (Exception error) { AppLog.Error("Native capture: failed to raise encode thread priority (non-fatal)", error); }
            encodeThread.Start();

            var adapterDescription = DescribeAdapter(device);
            AppLog.Info($"Native capture started (DXGI Desktop Duplication): target={(targetHandle != 0 ? "window" : "primary monitor")}, source={captureWidth}x{captureHeight}, output={outputWidth}x{outputHeight}, encoder={encoderName}, adapter={adapterDescription}, preset={config.EncoderPreset}, configFrameRate={config.FrameRate}.");
            SetHealth(new ReplayCaptureHealth("Native", "Desktop Duplication", ReplayCaptureState.Healthy,
                config.FrameRate, 0, 0, 0, 0, 0, 0, encoderName, "Default adapter", string.Empty, DateTime.UtcNow)
            {
                AdapterDescription = adapterDescription,
                EncoderPreset = config.EncoderPreset,
                EncodeQueueCapacity = encodeQueueCapacity
            });
            ready.TrySetResult();

            var stopwatch = System.Diagnostics.Stopwatch.StartNew();
            // Anchors packet->pts (a Stopwatch-based, accurate-to-the-real-
            // capture-moment value) back to a real wall-clock instant, so
            var lastForcedKeyframe = TimeSpan.Zero;
            var lastTargetRefresh = TimeSpan.Zero;
            var lastEncodedAt = TimeSpan.Zero;
            // Last time the per-present crop copy actually ran - see the gate at
            // its call site for why it is rate-limited rather than run on every
            // present.
            var lastCropCopyAt = TimeSpan.Zero;
            var targetFrameInterval = TimeSpan.FromSeconds(1.0 / Math.Clamp(config.FrameRate, 15, 240));
            // Counts encoded frames (including duplicate/padding ones) so
            // frame->pts can be assigned an IDEAL, constant-rate timestamp
            // (index * exact interval) rather than real elapsed time - see
            // the pacing gate below for why. RingPacket.WallClockUtc (used
            // only for audio alignment) is tracked completely separately via
            // a MonotonicClock.UtcNow captured at each actual encode, passed
            // straight into DrainToRingBuffer, so idealizing video's own
            // timeline can't drag audio sync off with it.
            // Accumulated rather than "index * interval", because the interval
            // is no longer fixed for the life of the session - RequestFrameRate
            // can halve it when the encoder cannot keep up. Multiplying a
            // running index by a changed interval would retroactively restate
            // every timestamp already emitted and jump the timeline; adding the
            // current interval each time keeps PTS monotonic and exactly spaced
            // across the change. time_base is microseconds (see the encoder
            // setup) and independent of frame rate, so nothing else moves.
            var nextPtsMicroseconds = 0.0;
            var idealFrameIntervalMicroseconds = 1_000_000.0 / Math.Clamp(config.FrameRate, 15, 240);
            var activeFrameRate = Math.Clamp(config.FrameRate, 15, 240);
            Volatile.Write(ref _requestedFrameRate, activeFrameRate);
            // The real capture-moment timestamp FIFO (one per avcodec_send_frame
            // call, dequeued one-for-one as packets actually come out - see
            // DrainToRingBuffer for why this, not just "now" at drain time, is
            // what fixes audio sync) now lives inside EncodeLoop, since send_frame
            // itself moved there - see the encode-queue enqueue below.
            // Short enough to stay well under even a 240fps target interval
            // (4.17ms) so the pacing gate below is never blocked waiting on
            // this call - see its call site for why that matters now. Lower
            // than this measured no further benefit and just adds pure
            // syscall/COM-marshaling overhead from calling AcquireNextFrame
            // more often for no timing gain.
            // Two frame intervals, capped at 33ms - it used to be half an interval
            // capped at 8ms.
            //
            // Windows bills this process for GPU engine time PER AcquireNextFrame
            // CALL, not per millisecond spent waiting in one. Measured on an idle
            // 4K desktop, where time inside the call was ~100% of wall clock at
            // every timeout tried, so only the call count moved:
            //
            //   timeout   calls/2s   3d
            //   8ms         280      22.7
            //   16ms        175      20.3
            //   33ms        112      12.9
            //   100ms        74       8.4
            //
            // Latency is unaffected while anything is actually happening, because
            // the timeout is a CEILING: AcquireNextFrame returns the moment a
            // frame arrives, so a source presenting at the target rate returns in
            // ~one interval no matter what this is set to. It only lengthens the
            // wait when the desktop is producing nothing - precisely when there is
            // no latency to lose.
            //
            // Not pushed to 100ms: this loop also drives the encode tick, so the
            // wait is how long that tick can be delayed, and at 100ms a single
            // iteration measured 93ms against a 16.7ms tick. 33ms keeps the worst
            // case near two intervals, which the catch-up path absorbs without
            // padding (padsSkipped stayed 0 in every run). Getting the rest of the
            // way needs acquisition moved off this thread entirely.
            // CLYPDAT_ACQUIRE_TIMEOUT_MS overrides the cap below, to test whether
            // acquisition and encode pacing sharing one loop is what gets this
            // process billed 20-25% of the GPU 3D engine on an idle desktop.
            //
            // The cap exists because this loop also drives the encode tick, which
            // has to fire every target interval - so the acquire wait can never
            // exceed half of one. On an idle desktop that means ~121 calls a
            // second against ~11 real frames, i.e. ~110 waits per second spent
            // INSIDE a DXGI call, which Windows bills as GPU engine time. The
            // Legacy/ScreenRecorderLib backend drives the same API from a thread
            // with no such constraint and measures 0.70% against this engine's
            // 21-27%.
            //
            // Raising it should trade billed GPU time for pacing accuracy: fewer,
            // longer waits, but an encode tick that wakes late. Watch padsSkipped
            // and avgPreAcquireMs alongside the GPU counter - if billed time falls
            // and pacing degrades, the coupling is confirmed and the real fix is
            // to decouple the two rather than to keep this override.
            var acquireTimeoutOverride = Environment.GetEnvironmentVariable("CLYPDAT_ACQUIRE_TIMEOUT_MS");
            var acquireTimeoutForcedMs = int.TryParse(acquireTimeoutOverride, out var parsedTimeout) && parsedTimeout is > 0 and <= 1000
                ? parsedTimeout
                : 0;
            var acquireTimeoutMs = acquireTimeoutForcedMs > 0
                ? (uint)acquireTimeoutForcedMs
                : (uint)Math.Clamp((int)Math.Round(targetFrameInterval.TotalMilliseconds * 2), 1, 33);
            if (acquireTimeoutForcedMs > 0) AppLog.Info($"Native capture: acquire timeout forced to {acquireTimeoutMs}ms.");
            var lastDiagLog = TimeSpan.Zero;
            var lastRingTrim = TimeSpan.Zero;
            var framesSeen = 0;
            var framesSeenSinceLog = 0;
            var framesProcessedSinceLog = 0;
            var framesEncoded = 0;
            var copyMapMs = 0.0;
            var scaleMs = 0.0;
            var encodeMs = 0.0;
            var framesEncodedSinceLog = 0;
            var stageStopwatch = new System.Diagnostics.Stopwatch();
            var getFrameMs = 0.0;
            var waitMs = 0.0;
            var iterationsSinceLog = 0;
            // AcquireNextFrame's OutduplFrameInfo was previously discarded
            // entirely (`out _`) - LastPresentTime==0 specifically means the
            // desktop IMAGE didn't actually change (e.g. only the OS cursor
            // moved), and AccumulatedFrames>1 means the OS coalesced more
            // than one real present into this single delivery because we
            // weren't keeping up. Both were being silently treated as "a new
            // real frame" before, which could burn pacing-gate slots on
            // duplicate content instead of genuinely new ones. Tracked here
            // to find out which is actually happening on this hardware
            // instead of continuing to guess.
            var zeroPresentSkips = 0;
            // Raw per-present crop-copy counts. Deliberately NOT folded into
            // avgCopyMapMs: that average divides by frames ENCODED, which is
            // exactly the metric-reading mistake documented at the crop call
            // site (it understated the true per-present cost by the
            // source/target frame rate ratio). These two are the only numbers
            // that actually show whether the crop-copy rate limiter is working.
            var cropCopies = 0;
            var cropCopiesSkipped = 0;
            // Per-frame scratch, hoisted out of the hot path - see their use
            // sites. Allocated once per session rather than 60+ times a second.
            var bltStreams = new VideoProcessorStream[1];
            var swsSrcData = new byte*[1];
            var swsSrcStride = new int[1];
            var accumulatedFramesSum = 0L;
            var accumulatedFramesMax = 0L;
            var lastRealPresentTicks = 0L;
            var presentGapSumMs = 0.0;
            var presentGapCount = 0;
            var presentGapMaxMs = 0.0;
            // Diagnostic for the "video freezes/stutters, but avgCopyMapMs/
            // avgScaleMs/avgEncodeMs/avgWaitMs/avgGetFrameMs all stay normal"
            // investigation - none of those named stages cover the top of the
            // loop (diag logging, the once-a-second ResolveTargetWindow
            // recheck and its DXGI duplication recreate if the target
            // changed, the null-duplication retry path). A real stall in a
            // region no stopwatch was watching would be invisible in every
            // other metric while iterations/sec still collapsed - which is
            // exactly what the logs showed. This measures everything between
            // the end of one iteration's work and the start of the next
            // AcquireNextFrame call, whatever it turns out to be.
            var preAcquireStopwatch = new System.Diagnostics.Stopwatch();
            var preAcquireMs = 0.0;
            var preAcquireMaxMs = 0.0;
            preAcquireStopwatch.Start();
            var gen2CountAtLastIteration = GC.CollectionCount(2);
            var gen1CountAtLastIteration = GC.CollectionCount(1);
            // Whether the target window is currently NOT foreground/visible - the
            // capture keeps encoding through this (re-submitting the last good
            // frame, see below) instead of stopping, so the ring buffer/full
            // session recording never has a real gap; SaveReplayAsync/
            // FinalizeFullSessionRecording read _pauseEvents to tell the editor
            // which parts of a saved clip were frozen like this.
            var isPaused = false;
            // Whether a real (non-occluded) frame has ever been captured yet.
            // The buffer arms the instant a game is detected, which is often
            // before the game window has focus (user alt-tabbed away, or just
            // hasn't clicked in yet) - frame->data is still FillFrameBlack's
            // placeholder at that point. Encoding used to start immediately
            // regardless, so the ring buffer/full session opened on however
            // many seconds of solid black preceded the user actually looking
            // at the game. Gating the encode/ring-write step on this instead
            // means that stretch is simply never recorded - once the window
            // has been seen in the foreground once, later occlusions (a
            // mid-session alt-tab) go back to the existing freeze-and-keep-
            // recording behavior above, unaffected.
            var hasCapturedRealFrame = false;
            // See the content-write sites above and the pacing gate below -
            // measures how stale frame->data's content actually is at the
            // moment each output frame gets encoded, to find out whether a
            // high-source-fps/lower-target-fps mismatch (e.g. 240->60) is a
            // real, measurable contributor to perceived judder or not, before
            // touching the pacing algorithm itself.
            var lastFrameContentCapturedUtc = MonotonicClock.UtcNow;
            var frameStalenessMs = 0.0;
            var frameStalenessMaxMs = 0.0;
            var frameStalenessCount = 0;
            // Whether anything new has reached frame->data since the last frame
            // was handed to the encoder. False means the next scheduled frame
            // would be a pure duplicate - see the pacing gate below.
            var freshContentSinceLastEncode = false;
            var padsSkippedSinceLog = 0;

            // Watchdog state for a duplication that stops delivering frames
            // without ever reporting AccessLost. Only AccessLost triggered a
            // recreate, so any other persistent AcquireNextFrame failure just
            // hit the 50ms backoff below and retried forever, silently - the
            // HRESULT wasn't even logged. One session sat like that for 105
            // minutes: framesSeen stuck at 1, the pacing gate happily padding
            // that single frame out at 60fps, and the clip saved from it was
            // 61 seconds of one black frame. Recreating the duplication is the
            // first move; if that doesn't take, the D3D device itself is
            // rebuilt, since a device lost underneath us can't produce a
            // working duplication no matter how many times we ask.
            var consecutiveAcquireFailures = 0;
            // An hour back rather than TimeSpan.MinValue: these are only ever
            // used as `stopwatch.Elapsed - x`, and subtracting MinValue
            // overflows TimeSpan outright.
            var lastAcquireFailureLog = TimeSpan.FromHours(-1);
            var lastRealFrameElapsed = TimeSpan.Zero;
            var lastRecoveryAttempt = TimeSpan.FromHours(-1);
            var recoveryAttempts = 0;
            var isStalled = false;
            // ~1s of solid failures at the 50ms transient backoff. Long enough
            // that a genuine desktop-switch blip rides it out untouched.
            const int acquireFailureRecreateThreshold = 20;
            // A game legitimately presenting nothing for this long (paused on a
            // static menu) is possible, so this path only ever recreates the
            // duplication - cheap, and invisible if it wasn't needed.
            var stallRecreateAfter = TimeSpan.FromSeconds(10);
            var recoveryRetryInterval = TimeSpan.FromSeconds(2);
            // Three failed recreates means the problem isn't the duplication.
            const int recoveryAttemptsBeforeDeviceRebuild = 3;

            while (!token.IsCancellationRequested)
            {
                if (stopwatch.Elapsed - lastDiagLog >= TimeSpan.FromSeconds(2))
                {
                    var diagElapsed = Math.Max(0.001, (stopwatch.Elapsed - lastDiagLog).TotalSeconds);
                    lastDiagLog = stopwatch.Elapsed;
                    var n = Math.Max(1, framesEncodedSinceLog);
                    var m = Math.Max(1, iterationsSinceLog);
                    // accumulatedFramesSum is only added to on iterations that
                    // actually delivered a real frame, so framesSeenSinceLog is
                    // its matching denominator. Subtracting zeroPresentSkips from
                    // total iterations (what this used to do) still left every
                    // AcquireNextFrame TIMEOUT in the count - the majority of
                    // iterations at any target below the poll rate - which scaled
                    // avgAccumulatedFrames down by ~2.3x and made a healthy
                    // one-present-per-acquire capture read as 0.43, i.e. as if
                    // over half of all acquires were coming back empty.
                    var realFrameCount = Math.Max(1, framesSeenSinceLog);
                    var presentGapDenom = Math.Max(1, presentGapCount);
                    // gen2Count/managedMb: if a stall coincides with gen2Count
                    // actually incrementing between two diag lines (or between
                    // the before/after read on a single spike below), that's
                    // a blocking GC pause, not a GPU/DXGI/scheduling stall -
                    // a multi-GB managed heap (see managedMb) can produce
                    // multi-second Gen2/full collections that freeze every
                    // managed thread at once, including this one, which would
                    // explain gaps this large with every named stage timer
                    // (GPU copy/scale/encode, AcquireNextFrame itself) still
                    // reading normal.
                    var managedMb = GC.GetTotalMemory(false) / (1024 * 1024);
                    // Distinguishes "the video ring buffer itself is what's
                    // ballooning" from "something else is" (the audio
                    // capture side - AudioCapturePipeline - runs on its own
                    // 2s trim timer, completely independent of this loop, and
                    // is the other obvious candidate for runaway MemoryStream
                    // growth). If ringBufferMb tracks managedMb closely, the
                    // problem is in this ring buffer/packet handling; if
                    // managedMb spikes far above ringBufferMb, it's elsewhere.
                    long ringBufferBytes;
                    lock (_bufferLock) ringBufferBytes = _ringBufferBytes;
                    var ringBufferMb = ringBufferBytes / (1024 * 1024);
                    // avgEncodeMs now comes from EncodeLoop's own thread (Interlocked
                    // handoff, reset via Exchange so nothing's lost mid-read) - the
                    // capture-thread-local encodeMs is relabeled avgQueueMs, since
                    // it's now just av_frame_clone+TryAdd cost, not the real encode
                    // call. queueDepth/droppedFrames confirm whether decoupling is
                    // actually keeping up: a growing depth or nonzero drops under
                    // load means the encoder itself is too slow, not just blocked.
                    var encodeCountSinceLog = Math.Max(1, Interlocked.Exchange(ref _encodeCountAccum, 0));
                    var encodeMicrosSinceLog = Interlocked.Exchange(ref _encodeMicrosAccum, 0);
                    var droppedSinceLog = Interlocked.Exchange(ref _encodeDroppedCount, 0);
                    UpdatePeak(ref _peakQueueDepth, encodeQueue.Count);
                    var frameStalenessDenom = Math.Max(1, frameStalenessCount);
                    AppLog.Debug($"Native capture diag: framesSeen={framesSeen}, framesEncoded={framesEncoded}, ringPackets={_packets.Count}, ringBufferMb={ringBufferMb}, avgCopyMapMs={copyMapMs / n:0.00}, avgScaleMs={scaleMs / n:0.00}, avgQueueMs={encodeMs / n:0.00}, avgEncodeMs={encodeMicrosSinceLog / 1000.0 / encodeCountSinceLog:0.00}, queueDepth={encodeQueue.Count}, droppedFrames={droppedSinceLog}, padsSkipped={padsSkippedSinceLog}, avgWaitMs={waitMs / m:0.00}, avgGetFrameMs={getFrameMs / m:0.00}, avgPreAcquireMs={preAcquireMs / m:0.00}, maxPreAcquireMs={preAcquireMaxMs:0.00}, avgFrameStalenessMs={frameStalenessMs / frameStalenessDenom:0.00}, maxFrameStalenessMs={frameStalenessMaxMs:0.00}, iterations={iterationsSinceLog}, cropCopies={cropCopies}, cropCopiesSkipped={cropCopiesSkipped}, zeroPresentSkips={zeroPresentSkips}, avgAccumulatedFrames={(double)accumulatedFramesSum / realFrameCount:0.00}, maxAccumulatedFrames={accumulatedFramesMax}, avgPresentGapMs={presentGapSumMs / presentGapDenom:0.00}, maxPresentGapMs={presentGapMaxMs:0.00}, managedMb={managedMb}, gen0={GC.CollectionCount(0)}, gen1={GC.CollectionCount(1)}, gen2={GC.CollectionCount(2)}.");
                    // Raw encoded rate, deliberately NOT crediting suppressed pads
                    // back in. Pads are only ever skipped under real queue
                    // pressure (see the pacing gate), so a rate that falls short
                    // of target because of them is a genuine overload and should
                    // read as one.
                    var outputFrameRate = framesEncodedSinceLog / diagElapsed;
                    // Judged against the rate actually being targeted, not the
                    // configured one: once the tuner has lowered it, hitting 30
                    // of 30 is healthy, and comparing against the original 60
                    // would report a permanent overload and ratchet forever.
                    var overloaded = droppedSinceLog > 0 || encodeQueue.Count * 4 >= encodeQueueCapacity * 3 ||
                                     (hasCapturedRealFrame && outputFrameRate < activeFrameRate * 0.9);
                    // A stall is worse than an overload and reads nothing like one:
                    // no frames arrive at all, so nothing gets dropped and the queue
                    // stays empty - every overload signal above says "healthy" right
                    // up until the clip comes out frozen. Report it while it is
                    // happening instead, so the UI can say so live rather than the
                    // save-time warning being the first anyone hears of it.
                    if (isStalled) _lastDegradedUtc = DateTime.UtcNow;
                    if (overloaded)
                    {
                        _lastDegradedUtc = DateTime.UtcNow;
                        // Same numbers as the DEBUG diag line above, but at INFO so
                        // an overload shows up in the log a user would actually
                        // send without needing a full diagnostics export - the
                        // debug log is 1-8MB/day and isn't something to ask for
                        // first when triaging "clips are choppy."
                        var recoveryGuidance = string.Equals(config.EncoderPreset, "P1", StringComparison.OrdinalIgnoreCase)
                            ? "P1 is already active; reduce capture resolution or frame rate."
                            : "Try a faster Encoder preset in Settings.";
                        AppLog.Info($"Native capture: overload - dropped {droppedSinceLog} frame(s) in the last {diagElapsed:0.0}s, queue {encodeQueue.Count}/{encodeQueueCapacity}, avgEncodeMs={encodeMicrosSinceLog / 1000.0 / encodeCountSinceLog:0.0}, avgScaleMs={scaleMs / n:0.0}. {recoveryGuidance}");
                    }
                    SetHealth(new ReplayCaptureHealth("Native", "Desktop Duplication",
                        overloaded || isStalled ? ReplayCaptureState.Degraded : ReplayCaptureState.Healthy,
                        activeFrameRate, framesSeenSinceLog / diagElapsed, framesProcessedSinceLog / diagElapsed,
                        outputFrameRate, Math.Max(0, framesEncodedSinceLog - framesProcessedSinceLog), droppedSinceLog, encodeQueue.Count,
                        encoderName, "Default adapter",
                        isStalled ? "Capture stalled - no new frames from the display. Recovering." :
                        overloaded ? "Capture overload. Output may fall below target FPS." : string.Empty,
                        DateTime.UtcNow)
                    {
                        TotalDroppedFrames = Interlocked.Read(ref _totalDroppedFrames),
                        PeakQueueDepth = Volatile.Read(ref _peakQueueDepth),
                        LastDegradedUtc = _lastDegradedUtc,
                        // Stall wins when both are true: no frames are arriving,
                        // so whatever the encoder looks like is a consequence of
                        // that rather than the encode settings being too costly.
                        DegradeReason = isStalled ? ReplayDegradeReason.CaptureStall
                            : overloaded ? ReplayDegradeReason.EncoderOverload
                            : ReplayDegradeReason.None,
                        AdapterDescription = adapterDescription,
                        EncoderPreset = config.EncoderPreset,
                        EncodeQueueCapacity = encodeQueueCapacity
                    });
                    copyMapMs = 0;
                    scaleMs = 0;
                    encodeMs = 0;
                    frameStalenessMs = 0;
                    frameStalenessMaxMs = 0;
                    frameStalenessCount = 0;
                    framesEncodedSinceLog = 0;
                    framesSeenSinceLog = 0;
                    framesProcessedSinceLog = 0;
                    padsSkippedSinceLog = 0;
                    waitMs = 0;
                    getFrameMs = 0;
                    iterationsSinceLog = 0;
                    cropCopies = 0;
                    cropCopiesSkipped = 0;
                    zeroPresentSkips = 0;
                    accumulatedFramesSum = 0;
                    accumulatedFramesMax = 0;
                    presentGapSumMs = 0;
                    presentGapCount = 0;
                    presentGapMaxMs = 0;
                    preAcquireMs = 0;
                    preAcquireMaxMs = 0;
                }

                // Re-check which window/monitor we should be capturing every second -
                // the detected game can change (switch games, close the game) mid-session,
                // and this backend never rotates/restarts the way WindowsReplayBuffer does
                // to naturally pick up fresh config.
                if (stopwatch.Elapsed - lastTargetRefresh >= TimeSpan.FromSeconds(1))
                {
                    lastTargetRefresh = stopwatch.Elapsed;
                    var freshHandle = ResolveTargetWindow(_configProvider());
                    if (freshHandle != targetHandle)
                    {
                        targetHandle = freshHandle;
                        isMonitorMode = targetHandle == 0;

                        // Duplication is per-OUTPUT, not per-window: the window is
                        // followed by cropping each frame, so a new target on the
                        // same monitor needs nothing rebuilt here. This used to tear
                        // down and recreate on every target change regardless, and a
                        // recreate is precisely where things go wrong - DuplicateOutput
                        // returns E_ACCESSDENIED whenever the secure desktop is up or
                        // another process holds the output (one session logged 460 of
                        // those in 45 seconds), and every rebuild is another chance to
                        // land in a duplication that never delivers again. Game
                        // detection flapping between a launcher and the game, or the
                        // game closing and falling back to monitor mode, all resolve
                        // to the same monitor and are now free.
                        var freshMonitor = ResolveTargetMonitor(targetHandle, config);
                        if (freshMonitor != targetMonitor || duplication is null)
                        {
                            targetMonitor = freshMonitor;
                            duplication?.Dispose();
                            // Null out before the recreate attempt - if it throws, `duplication`
                            // must not be left pointing at the just-disposed object, or the next
                            // AcquireNextFrame call below crashes the whole loop with an NRE
                            // instead of just retrying (see the null-guard above the acquire call).
                            duplication = null;
                            try
                            {
                                duplication = CreateDuplicationFor(device, targetHandle, config, out desktopBounds);
                            }
                            catch (Exception error)
                            {
                                AppLog.Error("Native capture: failed to switch DXGI duplication target.", error);
                            }
                        }

                        if (isPaused)
                        {
                            isPaused = false;
                            lock (_bufferLock) _pauseEvents.Add(new PauseEvent(MonotonicClock.UtcNow, false));
                        }
                    }
                }

                // Whole-iteration wall time (previous AcquireNextFrame call
                // through this one) - compare against
                // waitMs+copyMapMs+scaleMs+encodeMs+getFrameMs in the diag
                // line below: if this runs meaningfully higher than the sum
                // of the named stages, something between them (diag
                // logging, the once-a-second target-window recheck/DXGI
                // recreate, GC, thread scheduling) is eating time none of
                // them are watching.
                var preAcquireElapsedMs = preAcquireStopwatch.Elapsed.TotalMilliseconds;
                preAcquireStopwatch.Restart();
                preAcquireMs += preAcquireElapsedMs;
                if (preAcquireElapsedMs > preAcquireMaxMs) preAcquireMaxMs = preAcquireElapsedMs;
                var gen2CountNow = GC.CollectionCount(2);
                var gen1CountNow = GC.CollectionCount(1);
                if (preAcquireElapsedMs > 200)
                {
                    // If gen1Delta/gen2Delta are both 0, this gap has nothing
                    // to do with GC - back to looking at DXGI/scheduling. If
                    // either incremented, a blocking collection ran somewhere
                    // in this window and froze every managed thread,
                    // including this one - managedMb shows how big a heap
                    // that collection had to walk.
                    AppLog.Info($"Native capture: {preAcquireElapsedMs:0}ms iteration-to-iteration gap - gen1Delta={gen1CountNow - gen1CountAtLastIteration}, gen2Delta={gen2CountNow - gen2CountAtLastIteration}, managedMb={GC.GetTotalMemory(false) / (1024 * 1024)}.");
                }

                gen1CountAtLastIteration = gen1CountNow;
                gen2CountAtLastIteration = gen2CountNow;

                iterationsSinceLog++;
                stageStopwatch.Restart();
                // A long (500ms) timeout meant this call itself blocked well
                // past a single frame interval whenever the source hadn't
                // produced anything new yet, so the pacing-gate/encode step
                // below (which only ran AFTER a successful acquire) could
                // fall arbitrarily far behind wall-clock time during any lull
                // in the source's own present rate - confirmed via
                // avgPresentGapMs/avgAccumulatedFrames staying ~1 (we were
                // never actually behind the source) while encoded fps still
                // measured well under target, meaning frames were being
                // skipped rather than duplicated to fill those gaps, unlike
                // every other capture tool (such as ScreenRecorderLib), which
                // pads with the last frame instead. A short timeout keeps
                // this loop returning often enough for the pacing gate below
                // (now unconditional, not gated on a successful acquire) to
                // actually catch up and duplicate-encode on schedule.
                // duplication can be null here if a prior recreate attempt (target
                // switch or access-loss recovery below) failed - retry it every
                // iteration with a short backoff instead of dereferencing null,
                // which previously crashed the whole capture session on any
                // transient DuplicateOutput failure (e.g. a fullscreen-exclusive
                // transition briefly denying access).
                if (duplication is null)
                {
                    Thread.Sleep(50);
                    try
                    {
                        duplication = CreateDuplicationFor(device, targetHandle, config, out desktopBounds);
                        AppLog.Info("Native capture: DXGI duplication recreated after prior failure.");
                    }
                    catch (Exception error)
                    {
                        AppLog.Error("Native capture: duplication recreate retry failed.", error);
                    }
                    continue;
                }

                var acquireResult = duplication.AcquireNextFrame(acquireTimeoutMs, out var frameInfo, out var desktopResource);
                waitMs += stageStopwatch.Elapsed.TotalMilliseconds;

                var occluded = !isMonitorMode && !IsWindowForegroundAndVisible(targetHandle);

                if (acquireResult.Success)
                {
                    consecutiveAcquireFailures = 0;
                    // LastPresentTime is 0 when the desktop IMAGE itself hasn't
                    // actually changed since the last delivered frame (e.g. only
                    // the OS cursor moved) - AcquireNextFrame still "succeeds" for
                    // these, so without this check every one of them was being
                    // treated as fresh content: cropped, GPU-scaled, and burning a
                    // pacing-gate slot on byte-identical data instead of the next
                    // genuinely new frame.
                    if (frameInfo.LastPresentTime == 0)
                    {
                        zeroPresentSkips++;
                        duplication.ReleaseFrame();
                        desktopResource.Dispose();
                    }
                    else
                    {
                        accumulatedFramesSum += frameInfo.AccumulatedFrames;
                        if (frameInfo.AccumulatedFrames > accumulatedFramesMax) accumulatedFramesMax = frameInfo.AccumulatedFrames;
                        if (lastRealPresentTicks != 0)
                        {
                            var gapMs = (frameInfo.LastPresentTime - lastRealPresentTicks) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
                            presentGapSumMs += gapMs;
                            presentGapCount++;
                            if (gapMs > presentGapMaxMs) presentGapMaxMs = gapMs;
                        }
                        lastRealPresentTicks = frameInfo.LastPresentTime;

                        try
                        {
                            framesSeen++;
                            framesSeenSinceLog++;
                            // The watchdog's heartbeat: the duplication just
                            // handed us genuinely new desktop content, which is
                            // the one thing a stalled capture never does.
                            if (isStalled)
                            {
                                isStalled = false;
                                AppLog.Info($"Native capture: frames resumed after a {(stopwatch.Elapsed - lastRealFrameElapsed).TotalSeconds:0.#}s stall.");
                            }
                            lastRealFrameElapsed = stopwatch.Elapsed;
                            recoveryAttempts = 0;

                            stageStopwatch.Restart();
                            int cropLeft = 0, cropTop = 0, cropWidth = captureWidth, cropHeight = captureHeight;
                            if (isMonitorMode)
                            {
                                cropWidth = desktopBounds.Right - desktopBounds.Left;
                                cropHeight = desktopBounds.Bottom - desktopBounds.Top;
                            }
                            else if (!occluded && !TryGetWindowCropRect(targetHandle, desktopBounds, out cropLeft, out cropTop, out cropWidth, out cropHeight))
                            {
                                // Window rect lookup failed transiently (e.g. mid-move/resize) -
                                // treat this frame as occluded/frozen instead of risking a bad copy.
                                occluded = true;
                                cropWidth = captureWidth;
                                cropHeight = captureHeight;
                            }
                            getFrameMs += stageStopwatch.Elapsed.TotalMilliseconds;

                            if (!occluded && (cropWidth != captureWidth || cropHeight != captureHeight))
                            {
                                if (cropWidth == pendingCropWidth && cropHeight == pendingCropHeight)
                                {
                                    pendingCropStableCount++;
                                }
                                else
                                {
                                    pendingCropWidth = cropWidth;
                                    pendingCropHeight = cropHeight;
                                    pendingCropStableCount = 1;
                                }

                                if (pendingCropStableCount >= cropStableThreshold)
                                {
                                    captureWidth = Math.Max(2, cropWidth);
                                    captureHeight = Math.Max(2, cropHeight);
                                    staging.Dispose();
                                    staging = CreateStagingTexture(device, captureWidth, captureHeight);
                                    ffmpeg.sws_freeContext(swsContext);
                                    swsContext = CreateScaler(captureWidth, captureHeight, outputWidth, outputHeight);

                                    if (useGpuScale)
                                    {
                                        inputView!.Dispose();
                                        croppedTexture!.Dispose();
                                        (croppedTexture, inputView) = CreateGpuCropInputView(device, videoDevice!, vpEnumerator!, captureWidth, captureHeight);
                                        // The texture the pending crop lived in is gone - the
                                        // replacement holds nothing yet, so there is nothing
                                        // for the next encode tick to convert until a fresh
                                        // present lands in it.
                                        croppedDirty = false;
                                        nv12Ready = false;
                                    }

                                    pendingCropStableCount = 0;
                                }
                            }
                            else
                            {
                                pendingCropWidth = captureWidth;
                                pendingCropHeight = captureHeight;
                                pendingCropStableCount = 0;
                            }

                            // Always process every genuinely new present (this used to be
                            // throttled to "at most once per target interval," reasoning that
                            // conversion/readback can't improve on an already-fresh scheduled
                            // frame) - two rounds of tuning that throttle (a phase-lock fix,
                            // then a jitter tolerance) each helped but never eliminated a
                            // real content-loss pattern: real presentation timing comes in
                            // bursts (a short gap right after a long one, confirmed via
                            // maxPresentGapMs swinging several ms around the mean even during
                            // steady gameplay), and a reactive "was it due YET" gate checked
                            // only at each present's own arrival moment permanently drops
                            // whichever presents land during a burst, no matter how generous
                            // the tolerance - there's no queue to catch up from later. That
                            // capped real output at ~90-93% of the source's own rate regardless
                            // of target fps (60/90/120/144 all equally affected, since the
                            // mechanism has nothing to do with the specific numbers involved).
                            // Scale/copy itself is cheap (~1ms, see avgScaleMs), so keeping
                            // frame->data always maximally fresh costs little even at a
                            // V-Sync-off source presenting in the hundreds of fps - the actual
                            // rate cap that matters (respecting the user's chosen target)
                            // still happens below: the fixed-rate encode-tick loop already
                            // throttles correctly regardless of how fresh its input is, where
                            // a skip is harmless (the next check just finds the still-fresh
                            // frame) rather than a permanently lost capture.
                            if (!occluded)
                            {
                                // Whether this present's pixels actually reached the
                                // pipeline, as opposed to being superseded by a
                                // newer present already sitting unconsumed in
                                // croppedTexture - see the crop-copy gate below.
                                // Always true on the CPU path, which has nowhere to
                                // hold a pending present and so must convert each one.
                                var contentAdvanced = false;
                                // NVENC's actual encode runs asynchronously - avcodec_send_frame
                                // can return before the encoder has finished reading a PREVIOUS
                                // submission of this same reused AVFrame's buffer. Without this,
                                // overwriting frame->data here for the next real frame could race
                                // the encoder still reading the last one, corrupting whatever it
                                // was mid-read on (observed as the frozen/occluded frame coming
                                // out black instead of the real last frame). Only actually makes
                                // a copy if something else still references the old buffer.
                                if (useGpuScale)
                                {
                                    // Crop only. This is the one part that genuinely has to
                                    // run per present, because the duplication frame is
                                    // released the moment this block exits - everything
                                    // downstream of it (the scale/convert Blt, the readback
                                    // copy and the CPU-side plane copy) reads croppedTexture
                                    // instead, so it can wait for a tick that will actually
                                    // encode.
                                    //
                                    // It used to all run here, once per present. At a target
                                    // well below the source's own rate that is mostly wasted
                                    // work: a 240fps source feeding a 60fps target converted
                                    // roughly four frames for every one that was ever
                                    // encoded, and measured ~24% of a 4070 Ti doing it (the
                                    // ~1ms conversion, 232x/second). The comment that called
                                    // this "cheap (~1ms, see avgScaleMs)" was reading its own
                                    // metric wrong - avgScaleMs divides by frames ENCODED,
                                    // not by the far larger number of presents it actually
                                    // ran on, so the true cost was understated by exactly the
                                    // source/target ratio.
                                    // ...but not necessarily on EVERY present. The loop
                                    // polls at roughly twice the target rate (see
                                    // acquireTimeoutMs), and a source presenting faster
                                    // than the target hands us several presents per
                                    // encode tick, all but the last of which are
                                    // overwritten in croppedTexture before anything ever
                                    // reads it. This is a full-resolution BGRA copy - at
                                    // 1440p that is ~14MB of copy bandwidth thrown away
                                    // per wasted present, which on an iGPU comes straight
                                    // out of the same system memory the game is using.
                                    //
                                    // This is NOT the throttle described above that was
                                    // reverted. That one asked "is an encode due yet?" at
                                    // each present's arrival, so it could refuse a present
                                    // while croppedTexture held nothing - i.e. while that
                                    // present was the only candidate content for the
                                    // coming tick - and that content was then permanently
                                    // gone, which is the 90-93% cap it measured. This gate
                                    // asks "is there already an unconsumed present sitting
                                    // in croppedTexture?" instead. The !croppedDirty branch
                                    // has no timer in it at all, and EncodeScheduledFrame
                                    // clears croppedDirty on every tick that consumes
                                    // content, so the first present after each tick is
                                    // always copied immediately. A skip can therefore only
                                    // happen when a newer-or-equal present is already
                                    // pending and guaranteed to be consumed next tick - no
                                    // unique frame is ever lost. When the source presents
                                    // no faster than the target (any V-Sync-on game at a
                                    // matching target), croppedDirty is false on virtually
                                    // every arrival and this is bit-identical to copying
                                    // unconditionally.
                                    //
                                    // The cost is freshness, not content: the pixels a tick
                                    // converts can be up to one target interval (16.7ms at
                                    // 60fps) older than the newest present. That is bounded
                                    // and already measured - watch avgFrameStalenessMs.
                                    //
                                    // A full interval, not the half it used to be. The
                                    // refresh was close to free while this block was a
                                    // staging copy and the expensive scale/convert Blt was
                                    // deferred to the encode tick. It is not free now that
                                    // the Blt happens HERE: at half an interval a
                                    // fast-presenting source (a browser scrolling, a video
                                    // playing) ran ~87 scale passes a second against a 60fps
                                    // encode, and the extra ~27 were overwritten before
                                    // anything read them. Measured on a 4K desktop scaled to
                                    // 1080p60, that was the difference between ~11% and ~16%
                                    // of the GPU's 3D engine spent on frames nobody sees.
                                    //
                                    // The !croppedDirty branch is untouched, so the first
                                    // present after every tick is still converted
                                    // immediately and no unique frame is lost.
                                    if (!croppedDirty || stopwatch.Elapsed - lastCropCopyAt >= targetFrameInterval)
                                    {
                                        stageStopwatch.Restart();
                                        using (var desktopTexture = desktopResource.QueryInterface<ID3D11Texture2D>())
                                        {
                                            // Preferred path: scale/convert straight out of the
                                            // duplication texture with a source rect, so the crop
                                            // costs nothing of its own. Only valid right here,
                                            // while the frame is still acquired.
                                            // One guard around the whole attempt, not just the
                                            // view creation. The processor was built with the CROP
                                            // as its declared input size and is now handed the full
                                            // desktop texture plus a source rect, which the content
                                            // description is only a hint about - a driver may reject
                                            // the view, the rect, or the Blt itself, and any of
                                            // those throwing here would kill the capture thread.
                                            if (directBltAvailable)
                                            {
                                                try
                                                {
                                                    var desktopKey = desktopTexture.NativePointer;
                                                    if (!desktopInputViews.TryGetValue(desktopKey, out var desktopView))
                                                    {
                                                        desktopView = videoDevice!.CreateVideoProcessorInputView(desktopTexture, vpEnumerator!, new VideoProcessorInputViewDescription
                                                        {
                                                            FourCC = 0,
                                                            ViewDimension = VideoProcessorInputViewDimension.Texture2D,
                                                            Texture2D = new Texture2DVideoProcessorInputView { MipSlice = 0, ArraySlice = 0 }
                                                        });
                                                        desktopInputViews[desktopKey] = desktopView;
                                                    }

                                                    videoContext!.VideoProcessorSetStreamSourceRect(
                                                        videoProcessor, 0, true,
                                                        new Vortice.RawRect(cropLeft, cropTop, cropLeft + captureWidth, cropTop + captureHeight));
                                                    bltStreams[0].Enable = true;
                                                    bltStreams[0].InputSurface = desktopView;
                                                    videoContext.VideoProcessorBlt(videoProcessor, outputView, 0, 1, bltStreams);
                                                    nv12Ready = true;
                                                }
                                                catch (Exception error)
                                                {
                                                    // Take the copy for the rest of the session, and
                                                    // clear the stream rect so the deferred Blt reads
                                                    // all of croppedTexture as it always did.
                                                    directBltAvailable = false;
                                                    nv12Ready = false;
                                                    try { videoContext!.VideoProcessorSetStreamSourceRect(videoProcessor, 0, false, null); } catch { /* best effort */ }
                                                    AppLog.Info($"Native capture: direct video-processor crop unavailable ({error.Message}) - falling back to the full-resolution crop copy.");
                                                }
                                            }

                                            if (!nv12Ready)
                                            {
                                                var box = new Vortice.Mathematics.Box(cropLeft, cropTop, 0, cropLeft + captureWidth, cropTop + captureHeight, 1);
                                                device.ImmediateContext.CopySubresourceRegion(croppedTexture, 0, 0, 0, 0, desktopTexture, 0, box);
                                            }
                                        }
                                        copyMapMs += stageStopwatch.Elapsed.TotalMilliseconds;

                                        // Same screen->crop conversion the CPU path does, then scaled
                                        // into output pixels so the readback side can draw straight
                                        // into the NV12 frame without needing the crop rect.
                                        if (config.CaptureCursor && GetCursorPos(out var gpuCursor))
                                        {
                                            var cropX = gpuCursor.X - desktopBounds.Left - cropLeft;
                                            var cropY = gpuCursor.Y - desktopBounds.Top - cropTop;
                                            cursorOutputX = (int)((long)cropX * outputWidth / captureWidth);
                                            cursorOutputY = (int)((long)cropY * outputHeight / captureHeight);
                                        }
                                        else
                                        {
                                            cursorOutputX = int.MinValue;
                                            cursorOutputY = int.MinValue;
                                        }

                                        croppedDirty = true;
                                        lastCropCopyAt = stopwatch.Elapsed;
                                        cropCopies++;
                                        contentAdvanced = true;
                                    }
                                    else
                                    {
                                        cropCopiesSkipped++;
                                    }
                                }
                                else
                                {
                                    ffmpeg.av_frame_make_writable(frame);
                                    stageStopwatch.Restart();
                                    using (var desktopTexture = desktopResource.QueryInterface<ID3D11Texture2D>())
                                    {
                                        var box = new Vortice.Mathematics.Box(cropLeft, cropTop, 0, cropLeft + captureWidth, cropTop + captureHeight, 1);
                                        device.ImmediateContext.CopySubresourceRegion(staging, 0, 0, 0, 0, desktopTexture, 0, box);
                                    }

                                    var mapped = device.ImmediateContext.Map(staging, 0, MapMode.Read, MapFlags.None);
                                    copyMapMs += stageStopwatch.Elapsed.TotalMilliseconds;

                                    stageStopwatch.Restart();
                                    try
                                    {
                                        if (config.CaptureCursor && GetCursorPos(out var cursor))
                                        {
                                            DrawDesktopCursor((byte*)mapped.DataPointer, (int)mapped.RowPitch, captureWidth, captureHeight,
                                                cursor.X - desktopBounds.Left - cropLeft,
                                                cursor.Y - desktopBounds.Top - cropTop);
                                        }
                                        // Reused, same reason as the VideoProcessorStream
                                        // array on the GPU path: these ran once per
                                        // present, not per encoded frame.
                                        swsSrcData[0] = (byte*)mapped.DataPointer;
                                        swsSrcStride[0] = (int)mapped.RowPitch;
                                        ffmpeg.sws_scale(swsContext, swsSrcData, swsSrcStride, 0, captureHeight, frame->data, frame->linesize);
                                    }
                                    finally
                                    {
                                        device.ImmediateContext.Unmap(staging, 0);
                                    }
                                    scaleMs += stageStopwatch.Elapsed.TotalMilliseconds;
                                    contentAdvanced = true;
                                }

                                // Real-world moment this content actually landed in frame->data -
                                // see the pacing gate below (frameStalenessMs) for why: at a source
                                // fps well above target (240 ->60 here), whatever's newest in
                                // frame->data when the pacing gate fires can still be up to one
                                // source-interval stale, varying iteration to iteration since the
                                // two clocks (source presents, target-fps pacing) run free-running
                                // and unsynchronized - a plausible source of visible judder despite
                                // every output frame being unique and perfectly PTS-spaced.
                                // Only advanced when this present's pixels actually
                                // went somewhere: a present skipped by the crop-copy
                                // gate leaves croppedTexture holding slightly older
                                // content, and claiming otherwise here would hide
                                // exactly the staleness this metric exists to bound.
                                if (contentAdvanced)
                                {
                                    lastFrameContentCapturedUtc = MonotonicClock.UtcNow;
                                    framesProcessedSinceLog++;
                                }
                                // Unconditional, unlike the above: this is the "capture
                                // is alive" heartbeat the stall watchdog and the UI read,
                                // and a present the crop gate declined still proves the
                                // source is presenting.
                                Volatile.Write(ref _lastRealContentTicks, MonotonicClock.UtcNow.Ticks);
                                // Set on both the GPU-scale and CPU-copy paths, unlike
                                // croppedDirty which only the GPU one uses - the pacing
                                // gate needs to know "is the next scheduled frame a pad"
                                // regardless of which path produced the content.
                                // Also set on a skipped crop copy, which is correct: a
                                // skip only happens when croppedTexture already holds
                                // unconsumed content, so the next tick is not a pad.
                                freshContentSinceLastEncode = true;
                            }
                            // else: occluded - frame->data still holds the last successfully
                            // scaled content, re-encoded unchanged below (visual freeze).
                        }
                        finally
                        {
                            duplication.ReleaseFrame();
                            desktopResource.Dispose();
                        }
                    }
                }
                else
                {
                    desktopResource?.Dispose();
                    if (acquireResult.Code == ResultCode.AccessLost.Code)
                    {
                        AppLog.Info("Native capture: DXGI duplication access lost, recreating.");
                        duplication.Dispose();
                        duplication = null;
                        try
                        {
                        duplication = CreateDuplicationFor(device, targetHandle, config, out desktopBounds);
                        }
                        catch (Exception error)
                        {
                            AppLog.Error("Native capture: failed to recreate DXGI duplication after access loss.", error);
                            Thread.Sleep(200);
                        }
                    }
                    else if (acquireResult.Code != ResultCode.WaitTimeout.Code)
                    {
                        // Transient failure (e.g. desktop switch) - brief backoff, retry.
                        // Counted and logged, because "transient" turned out to be
                        // an assumption: the same non-AccessLost code can repeat
                        // forever, and this branch used to swallow it silently
                        // while the watchdog below had nothing to go on. Rate-limited
                        // so a permanent failure doesn't write 20 lines a second.
                        consecutiveAcquireFailures++;
                        if (stopwatch.Elapsed - lastAcquireFailureLog >= TimeSpan.FromSeconds(5))
                        {
                            lastAcquireFailureLog = stopwatch.Elapsed;
                            AppLog.Info($"Native capture: AcquireNextFrame failed with 0x{acquireResult.Code:X8} ({consecutiveAcquireFailures} in a row).");
                        }
                        Thread.Sleep(50);
                    }
                    // WaitTimeout: genuinely nothing new from the source yet this
                    // cycle - fall through to the pacing gate below exactly like a
                    // successful-but-occluded frame would, so frame->data's last
                    // real content still gets duplicate-encoded on schedule instead
                    // of the encoded frame rate just falling behind.
                }

                // Stall watchdog. Two ways in: AcquireNextFrame erroring solidly
                // (unambiguous - something is broken), or simply no new desktop
                // content for a long time while the game window IS foreground.
                // The second is a soft signal, since a genuinely static screen
                // looks identical from here, so it only ever costs a duplication
                // recreate - a few milliseconds, and harmless if it wasn't needed.
                if (hasCapturedRealFrame && !occluded &&
                    (consecutiveAcquireFailures >= acquireFailureRecreateThreshold ||
                     stopwatch.Elapsed - lastRealFrameElapsed >= stallRecreateAfter) &&
                    stopwatch.Elapsed - lastRecoveryAttempt >= recoveryRetryInterval)
                {
                    var stalledSeconds = (stopwatch.Elapsed - lastRealFrameElapsed).TotalSeconds;
                    if (!isStalled)
                    {
                        isStalled = true;
                        AppLog.Info($"Native capture: no new frames for {stalledSeconds:0.#}s (acquire failures={consecutiveAcquireFailures}) - recovering.");
                    }

                    lastRecoveryAttempt = stopwatch.Elapsed;
                    recoveryAttempts++;

                    // Past a few failed recreates the duplication isn't the
                    // problem - the device under it is. Rebuild that too, along
                    // with everything bound to it. The encoder, ring buffer and
                    // Full Session writer are deliberately left alone: they hold
                    // the recording's history, and none of them depend on D3D.
                    var rebuildDevice = recoveryAttempts > recoveryAttemptsBeforeDeviceRebuild;
                    try
                    {
                        if (rebuildDevice)
                        {
                            var newDevice = CreateD3D11Device();
                            IDXGIOutputDuplication? newDuplication = null;
                            ID3D11Texture2D? newStaging = null;
                            try
                            {
                                newDuplication = CreateDuplicationFor(newDevice, targetHandle, config, out desktopBounds);
                                newStaging = CreateStagingTexture(newDevice, captureWidth, captureHeight);
                            }
                            catch
                            {
                                newStaging?.Dispose();
                                newDuplication?.Dispose();
                                newDevice.Dispose();
                                throw;
                            }

                            duplication?.Dispose();
                            staging?.Dispose();
                            // Keyed by texture pointer and owned by the device
                            // going away here - a stale entry would hand the
                            // rebuilt pipeline a view over a dead resource.
                            foreach (var view in desktopInputViews.Values) view.Dispose();
                            desktopInputViews.Clear();
                            nv12Ready = false;
                            inputView?.Dispose();
                            croppedTexture?.Dispose();
                            outputView?.Dispose();
                            if (nv12StagingRing is not null) foreach (var t in nv12StagingRing) t.Dispose();
                            nv12Output?.Dispose();
                            videoProcessor?.Dispose();
                            vpEnumerator?.Dispose();
                            videoContext?.Dispose();
                            videoDevice?.Dispose();
                            device.Dispose();

                            inputView = null;
                            croppedTexture = null;
                            outputView = null;
                            nv12StagingRing = null;
                            nv12Output = null;
                            videoProcessor = null;
                            vpEnumerator = null;
                            videoContext = null;
                            videoDevice = null;

                            device = newDevice;
                            duplication = newDuplication;
                            staging = newStaging;

                            // Same best-effort as the initial setup: if the GPU
                            // path can't be rebuilt, the CPU sws_scale fallback
                            // below still works off `staging` alone.
                            try
                            {
                                // No CaptureCursor exclusion here any more: the cursor
                                // is composited into NV12 after the scale (see
                                // DrawDesktopCursorNv12), so it no longer requires the
                                // CPU path. This copy of the old restriction outlived
                                // the initial-setup one and quietly downgraded every
                                // cursor-enabled capture to the 10-13ms/frame
                                // sws_scale path the moment a stall triggered a device
                                // rebuild.
                                (videoDevice, videoContext, vpEnumerator, videoProcessor, nv12Output, nv12StagingRing, outputView) =
                                    CreateGpuScaler(device, captureWidth, captureHeight, outputWidth, outputHeight, config.FrameRate);
                                (croppedTexture, inputView) = CreateGpuCropInputView(device, videoDevice, vpEnumerator, captureWidth, captureHeight);
                                useGpuScale = true;
                            }
                            catch (Exception error)
                            {
                                AppLog.Info($"Native capture: GPU downscale unavailable after device rebuild, falling back to CPU scale: {error.Message}");
                                useGpuScale = false;
                            }
                            // The zero-copy pool's textures belong to the device
                            // that was just destroyed, and the running encoder
                            // is bound to that pool for life - hw_frames_ctx is
                            // fixed at avcodec_open2. So both are rebuilt on the
                            // new device and the encoder is swapped for one that
                            // reads from the new pool, mid-session, without
                            // touching the ring buffer or the Full Session
                            // writer. If anything in that chain fails, the swap
                            // still happens - to a system-memory encoder - so
                            // capture continues either way.
                            if (hwFramesRef != 0)
                            {
                                // Every pool texture belonged to the device that
                                // was just destroyed, and nv12Output with them -
                                // there is nothing to repeat until the rebuilt
                                // scaler has written a frame.
                                hasHardwareContent = false;
                                hardwarePoolTextures.Clear();
                                ReleaseHardwareFrames(ref hwDeviceRef, ref hwFramesRef);

                                if (useGpuScale && !config.CaptureCursor)
                                {
                                    (hwDeviceRef, hwFramesRef) = TryCreateD3D11EncodeFrames(
                                        device, outputWidth, outputHeight, encodeQueueCapacity + HardwareFramePoolHeadroom);
                                }

                                var replacement = CreateEncoder(config, outputWidth, outputHeight, hwFramesRef, out _, out var rebuiltEncoderName, out var rebuiltHardware);
                                if (!rebuiltHardware) ReleaseHardwareFrames(ref hwDeviceRef, ref hwFramesRef);

                                var swapped = new ManualResetEventSlim(false);
                                encodeQueue!.Add(new EncodeJob(0, DateTime.UtcNow, (nint)replacement, swapped));
                                // Bounded: if the encode thread were wedged, waiting
                                // forever here would take the capture thread down with
                                // it. The old context is retired either way - it is
                                // only freed after the thread has joined.
                                if (!swapped.Wait(TimeSpan.FromSeconds(5)))
                                {
                                    AppLog.Error("Native capture: encoder swap after device rebuild did not complete in 5s.", new TimeoutException());
                                }

                                swapped.Dispose();
                                retiredCodecContexts.Add((nint)codecContext);
                                codecContext = replacement;
                                AppLog.Info($"Native capture: encoder rebound after device rebuild ({rebuiltEncoderName}, zeroCopy={rebuiltHardware}).");
                            }

                            nv12StagingIndex = 0;
                            nv12RingWritten = 0;
                            // Every texture the pending crop referred to belongs to the
                            // old device and has just been replaced.
                            croppedDirty = false;

                            AppLog.Info("Native capture: D3D device rebuilt after a stalled duplication.");
                        }
                        else
                        {
                            duplication?.Dispose();
                            duplication = null;
                            duplication = CreateDuplicationFor(device, targetHandle, config, out desktopBounds);
                            AppLog.Info($"Native capture: DXGI duplication recreated after a stall (attempt {recoveryAttempts}).");
                        }

                        consecutiveAcquireFailures = 0;
                    }
                    catch (Exception error)
                    {
                        AppLog.Error($"Native capture: stall recovery failed (attempt {recoveryAttempts}, rebuildDevice={rebuildDevice}).", error);
                    }

                    // The recreate above can leave `duplication` null on failure;
                    // the null-guard at the top of the loop retries it, and
                    // dereferencing it below would crash the whole session.
                    if (duplication is null) continue;
                }

                if (occluded != isPaused)
                {
                    isPaused = occluded;
                    lock (_bufferLock) _pauseEvents.Add(new PauseEvent(MonotonicClock.UtcNow, isPaused));
                    AppLog.Info($"Native capture: recording {(isPaused ? "paused (window not foreground)" : "resumed")}.");
                }

                if (!occluded && !hasCapturedRealFrame)
                {
                    hasCapturedRealFrame = true;
                    // Start the stall watchdog's clock here, not at loop start -
                    // this flips the moment the window first has focus, which can
                    // be well before the first real frame lands, and the watchdog
                    // would otherwise read that whole wait as a stall.
                    lastRealFrameElapsed = stopwatch.Elapsed;
                    // lastEncodedAt is still its initial/stale value from however
                    // long the buffer sat waiting for focus - reset it to now so
                    // the catch-up gate below doesn't treat that entire wait as a
                    // pacing gap to fill with duplicate frames.
                    lastEncodedAt = stopwatch.Elapsed;
                    if (fullSessionFormatContext is not null)
                    {
                        // Full Session's muxed audio window is requested starting
                        // at fullSessionStartUtc (see FinalizeFullSessionRecording)
                        // - it was set at buffer-arm time above, before the window
                        // ever had focus. Re-anchor it to this, the actual first
                        // recorded video frame, so audio isn't muxed several
                        // seconds ahead of where the video track now starts.
                        fullSessionStartUtc = MonotonicClock.UtcNow;
                        fullSessionStartWallUtc = DateTime.UtcNow;
                    }
                    AppLog.Info("Native capture: first foreground frame captured, recording started.");
                }

                // Pacing/encode gate - now evaluated every iteration regardless of
                // whether this exact cycle produced fresh content, instead of only
                // running inside the successful-real-frame branch. Previously the
                // encoded frame RATE was capped by however fast the SOURCE happened
                // to deliver genuinely new presents (measured via LastPresentTime:
                // averaging ~45fps with real bursts past 150fps and lulls near
                // 40fps, while avgAccumulatedFrames stayed ~1 the whole time -
                // proof this loop was never actually falling behind the source, it
                // was just refusing to pad for it). Every other capture tool pads
                // with a duplicate of the last frame when nothing new has arrived
                // in time to keep actual encoded fps locked to the target; this now
                // does the same, reusing frame->data unchanged (identical mechanism
                // to the existing occlusion freeze) whenever nothing fresh landed.
                //
                // A `while` here (not `if`) instead of jumping lastEncodedAt to
                // "now" catches up with MULTIPLE duplicate-encoded frames if a
                // real stall (e.g. an AccessLost recreation, a Thread.Sleep(50)
                // backoff) ever eats more than one interval's worth of real time,
                // so the declared/ideal timeline below never silently falls behind
                // real elapsed time.
                //
                // Capped, though - a genuine multi-minute stall (seen under heavy
                // GPU load/driver hiccups) would otherwise make this loop pad
                // through the ENTIRE gap as thousands of duplicate-encoded copies
                // of one frozen frame, ballooning both encoded frame count and the
                // clip's own PTS-derived duration far past what was requested (a
                // "1 minute" replay length saving a 7+ minute, almost entirely
                // static clip). Past a couple of seconds' worth of padding, snap
                // the ideal timeline forward to now instead of mechanically
                // filling every missed slot.
                // Skipped entirely until the target window has been in the
                // foreground at least once - see hasCapturedRealFrame's
                // declaration above for why (avoids ever writing the
                // FillFrameBlack placeholder into the ring buffer/full
                // session as real recorded content).
                // Shared tail of both pacing modes below: force a keyframe on
                // schedule, clone frame (already carrying whatever pts/pict_type
                // the caller just set) and hand it to EncodeLoop. Factored out
                // since the fixed-rate and adaptive-rate branches only differ in
                // WHEN/how they decide to call this, not in what encoding a
                // scheduled frame actually does.
                // Zero-copy twin of EncodeScheduledFrame below: the scaled NV12
                // surface goes straight into one of the encoder's own D3D11
                // pool textures and that texture is what gets queued. No
                // staging copy, no Map, no plane memcpy, and nothing for
                // ffmpeg to upload again on the other side - see
                // TryCreateD3D11EncodeFrames for the measurements that motivate
                // it. `frame` still carries the pts/pict_type the pacing loop
                // just set; it is only ever a carrier on this path.
                unsafe void EncodeScheduledFrameHardware()
                {
                    stageStopwatch.Restart();
                    // Only a tick with new content owes a Blt. Everything after
                    // it runs for pads too: nv12Output still holds the last
                    // scaled frame, so a pad is the same copy from the same
                    // source, just without re-scaling it.
                    if (croppedDirty)
                    {
                        if (!nv12Ready)
                        {
                            bltStreams[0].Enable = true;
                            bltStreams[0].InputSurface = inputView;
                            videoContext!.VideoProcessorBlt(videoProcessor, outputView, 0, 1, bltStreams);
                        }

                        nv12Ready = false;
                        croppedDirty = false;
                        hasHardwareContent = true;
                    }

                    // Nothing has ever been scaled into nv12Output - there is no
                    // texture to send, and the system-memory path's black
                    // placeholder has no equivalent here. The pacing loop has
                    // already advanced its timeline, so this reads as a dropped
                    // frame rather than a slower clip.
                    if (!hasHardwareContent) return;

                    // EVERY frame gets its own pool surface, pads included.
                    // Re-sending the previous frame's texture (which is what a
                    // pad used to do, as a ref-counted clone) hands the encoder
                    // a surface it may still have mapped from the frame before
                    // it - NVENC then consumed the frame without emitting a
                    // packet, silently. Measured: a 60s clip with 709 missing
                    // ticks landing at 48fps, which is the SOURCE's present
                    // rate, because effectively only real frames survived.
                    // A pad now costs one GPU-local copy, and nothing is ever
                    // in flight twice.
                    var pooled = ffmpeg.av_frame_alloc();
                    if (pooled is null)
                    {
                        Interlocked.Increment(ref _encodeDroppedCount);
                        Interlocked.Increment(ref _totalDroppedFrames);
                        scaleMs += stageStopwatch.Elapsed.TotalMilliseconds;
                        return;
                    }

                    // Pool exhausted means every surface is still held by a
                    // frame in the queue - the same condition a full queue
                    // describes, and handled the same way.
                    if (ffmpeg.av_hwframe_get_buffer((AVBufferRef*)hwFramesRef, pooled, 0) < 0)
                    {
                        ffmpeg.av_frame_free(&pooled);
                        Interlocked.Increment(ref _encodeDroppedCount);
                        Interlocked.Increment(ref _totalDroppedFrames);
                        scaleMs += stageStopwatch.Elapsed.TotalMilliseconds;
                        return;
                    }

                    // d3d11 frames carry the texture in data[0] and the slice
                    // index in data[1] - which is the destination subresource.
                    var texturePointer = (nint)pooled->data[0];
                    var arraySlice = (uint)(nint)pooled->data[1];
                    if (!hardwarePoolTextures.TryGetValue(texturePointer, out var poolTexture))
                    {
                        poolTexture = new ID3D11Texture2D(texturePointer);
                        hardwarePoolTextures[texturePointer] = poolTexture;
                    }

                    device.ImmediateContext.CopySubresourceRegion(poolTexture, arraySlice, 0, 0, 0, nv12Output!, 0);
                    scaleMs += stageStopwatch.Elapsed.TotalMilliseconds;

                    if (stopwatch.Elapsed - lastForcedKeyframe >= TimeSpan.FromSeconds(2))
                    {
                        pooled->pict_type = AVPictureType.AV_PICTURE_TYPE_I;
                        lastForcedKeyframe = stopwatch.Elapsed;
                    }
                    else
                    {
                        pooled->pict_type = AVPictureType.AV_PICTURE_TYPE_NONE;
                    }

                    var staleness = (MonotonicClock.UtcNow - lastFrameContentCapturedUtc).TotalMilliseconds;
                    frameStalenessMs += staleness;
                    if (staleness > frameStalenessMaxMs) frameStalenessMaxMs = staleness;
                    frameStalenessCount++;

                    stageStopwatch.Restart();
                    // `frame` never carries pixels on this path - the pacing
                    // loop just set the pts on it, and this is where that pts
                    // meets the texture it belongs to.
                    pooled->pts = frame->pts;
                    if (!encodeQueue!.TryAdd(new EncodeJob((nint)pooled, MonotonicClock.UtcNow)))
                    {
                        var droppedFrame = pooled;
                        ffmpeg.av_frame_free(&droppedFrame);
                        Interlocked.Increment(ref _encodeDroppedCount);
                        Interlocked.Increment(ref _totalDroppedFrames);
                    }
                    else
                    {
                        framesEncoded++;
                        framesEncodedSinceLog++;
                    }

                    encodeMs += stageStopwatch.Elapsed.TotalMilliseconds;
                }

                unsafe void EncodeScheduledFrame()
                {
                    if (hwFramesRef != 0)
                    {
                        EncodeScheduledFrameHardware();
                        return;
                    }

                    // Convert whatever the latest present cropped, but only now that a
                    // frame is actually being encoded - see the crop block above for why
                    // this moved off the per-present path. Nothing new since the last
                    // encode means frame->data still holds the right content and this is
                    // a deliberate duplicate, exactly like the occlusion freeze.
                    //
                    // Reads back with DoNotWait, newest ready slot first, and never
                    // blocks. A fixed "map the slot from N ticks ago" pairing has to
                    // pick one number for two things it cannot satisfy at once: small
                    // enough that frame->data isn't needlessly stale, large enough that
                    // the copy has finished even when the GPU is running late. Two slots
                    // gave the copy exactly one encode interval, and a 4K game spiking
                    // blew straight through that - the Map then blocked, which is what
                    // turned a brief GPU spike into avgScaleMs of 25-92ms, a full encode
                    // queue and ~100 dropped frames per 2s window.
                    //
                    // Asking instead means staleness stays at one interval whenever the
                    // GPU is keeping up, and degrades to two or three only while it
                    // isn't, rather than the whole pipeline stalling. If nothing is
                    // ready at all, frame->data keeps its previous content and this tick
                    // encodes a duplicate - the same graceful outcome as the occlusion
                    // freeze, and far cheaper than waiting.
                    if (useGpuScale && croppedDirty)
                    {
                        stageStopwatch.Restart();
                        // Reused rather than allocated per frame - 60/sec of these
                        // into a loop whose own diagnostics already blame blocking
                        // GCs for multi-hundred-millisecond capture gaps. inputView
                        // is refreshed in place because a crop resize or device
                        // rebuild replaces the view object underneath us.
                        // Already produced at present time straight off the
                        // duplication texture - see directBltAvailable. Only the
                        // fallback copy path still owes a Blt here.
                        if (!nv12Ready)
                        {
                            bltStreams[0].Enable = true;
                            bltStreams[0].InputSurface = inputView;
                            videoContext!.VideoProcessorBlt(videoProcessor, outputView, 0, 1, bltStreams);
                        }

                        nv12Ready = false;
                        var ringLength = nv12StagingRing!.Length;
                        var currentRingIndex = nv12StagingIndex;
                        device.ImmediateContext.CopyResource(nv12StagingRing[currentRingIndex], nv12Output);
                        if (nv12RingWritten < ringLength) nv12RingWritten++;
                        copyMapMs += stageStopwatch.Elapsed.TotalMilliseconds;

                        stageStopwatch.Restart();
                        // k=1 is the slot written on the previous tick (freshest of the
                        // finished ones), counting back to the oldest still held.
                        for (var k = 1; k < nv12RingWritten; k++)
                        {
                            var candidate = ((currentRingIndex - k) % ringLength + ringLength) % ringLength;
                            var mapResult = device.ImmediateContext.Map(
                                nv12StagingRing[candidate], 0u, MapMode.Read, MapFlags.DoNotWait, out var mapped);
                            // DXGI_ERROR_WAS_STILL_DRAWING - this slot's copy has not
                            // landed yet, so try an older one rather than wait on it.
                            if (mapResult.Failure) continue;

                            try
                            {
                                ffmpeg.av_frame_make_writable(frame);
                                CopyNv12PlanesToFrame(mapped, outputWidth, outputHeight, frame);
                                if (cursorOutputX != int.MinValue)
                                {
                                    DrawDesktopCursorNv12(frame, outputWidth, outputHeight, cursorOutputX, cursorOutputY);
                                }
                            }
                            finally
                            {
                                device.ImmediateContext.Unmap(nv12StagingRing[candidate], 0);
                            }

                            break;
                        }
                        scaleMs += stageStopwatch.Elapsed.TotalMilliseconds;

                        nv12StagingIndex = (currentRingIndex + 1) % ringLength;
                        croppedDirty = false;
                    }

                    // Force a keyframe periodically so the ring buffer always has a nearby
                    // point to start a save-window at without waiting on the encoder's own
                    // GOP schedule.
                    if (stopwatch.Elapsed - lastForcedKeyframe >= TimeSpan.FromSeconds(2))
                    {
                        frame->pict_type = AVPictureType.AV_PICTURE_TYPE_I;
                        lastForcedKeyframe = stopwatch.Elapsed;
                    }
                    else
                    {
                        frame->pict_type = AVPictureType.AV_PICTURE_TYPE_NONE;
                    }

                    // avcodec_send_frame/receive_packet themselves now run on
                    // EncodeLoop's own thread (see its declaration above) - a slow
                    // NVENC call under real GPU contention used to block THIS
                    // thread, stalling AcquireNextFrame right along with it (the
                    // capture freeze this whole diagnostic trail was chasing:
                    // avgEncodeMs spiking 20x+ with frames backing up hundreds
                    // deep). av_frame_clone is a cheap ref-counted handle, not a
                    // pixel copy - av_frame_make_writable up above already treats
                    // "something else still references this buffer" as
                    // copy-on-write, so the encode thread holding this clone a
                    // while longer just means the NEXT capture-thread frame gets
                    // a fresh buffer instead of racing this one, exactly the
                    // mechanism that comment already relies on.
                    // Diagnostic only (see lastFrameContentCapturedUtc's declaration) -
                    // how old frame->data's content already was at the moment this
                    // output tick decided to encode it.
                    var staleness = (MonotonicClock.UtcNow - lastFrameContentCapturedUtc).TotalMilliseconds;
                    frameStalenessMs += staleness;
                    if (staleness > frameStalenessMaxMs) frameStalenessMaxMs = staleness;
                    frameStalenessCount++;

                    stageStopwatch.Restart();
                    var clonedFrame = ffmpeg.av_frame_clone(frame);
                    if (clonedFrame is null)
                    {
                        AppLog.Error("Native capture: av_frame_clone failed, dropping a frame.");
                        Interlocked.Increment(ref _encodeDroppedCount);
                        Interlocked.Increment(ref _totalDroppedFrames);
                    }
                    else if (!encodeQueue.TryAdd(new EncodeJob((nint)clonedFrame, MonotonicClock.UtcNow)))
                    {
                        // Queue's genuinely full - the encoder can't keep pace even
                        // decoupled, not just a transient stall. Drop rather than
                        // block (defeats the whole point) or grow unbounded.
                        var droppedFrame = clonedFrame;
                        ffmpeg.av_frame_free(&droppedFrame);
                        Interlocked.Increment(ref _encodeDroppedCount);
                        Interlocked.Increment(ref _totalDroppedFrames);
                    }
                    else
                    {
                        // PTS advances above once per scheduled frame. It must not
                        // advance again here: doing both doubles every successful
                        // frame's duration and turns a 60 FPS clip into ~30 FPS.
                        framesEncoded++;
                        framesEncodedSinceLog++;
                    }
                    encodeMs += stageStopwatch.Elapsed.TotalMilliseconds;
                }

                // Picked up here rather than only at session start: when the
                // encoder cannot sustain the configured rate even at the
                // cheapest preset, EncoderTuningService lowers it live instead
                // of letting a third of the frames go in the bin. Only the
                // pacing interval changes - the encoder, the scaler and the
                // stream parameters are all untouched, so packets from before
                // and after the change still mux into one file.
                var requestedFrameRate = Volatile.Read(ref _requestedFrameRate);
                if (requestedFrameRate != activeFrameRate && requestedFrameRate > 0)
                {
                    activeFrameRate = requestedFrameRate;
                    targetFrameInterval = TimeSpan.FromSeconds(1.0 / activeFrameRate);
                    idealFrameIntervalMicroseconds = 1_000_000.0 / activeFrameRate;
                    acquireTimeoutMs = acquireTimeoutForcedMs > 0
                        ? (uint)acquireTimeoutForcedMs
                        : (uint)Math.Clamp((int)Math.Round(targetFrameInterval.TotalMilliseconds * 2), 1, 33);
                    SetHealth(_health with { TargetFrameRate = activeFrameRate, UpdatedUtc = DateTime.UtcNow });
                    AppLog.Info($"Native capture: target frame rate now {activeFrameRate} fps.");
                }

                if (hasCapturedRealFrame)
                {
                // At most 250 ms of duplicate work after a stall. Longer bursts
                // refill a saturated queue with stale copies and make recovery
                // worse than dropping the missed interval.
                var catchUpFramesRemaining = Math.Clamp(activeFrameRate / 4, 4, 60);
                while (stopwatch.Elapsed - lastEncodedAt >= targetFrameInterval)
                {
                    if (catchUpFramesRemaining-- <= 0)
                    {
                        AppLog.Info($"Native capture: pacing gap of {(stopwatch.Elapsed - lastEncodedAt).TotalSeconds:0.0}s exceeded catch-up cap - snapping timeline forward instead of padding with duplicate frames.");
                        lastEncodedAt = stopwatch.Elapsed;
                        break;
                    }
                    lastEncodedAt += targetFrameInterval;

                    // An ideal, constant-rate timestamp (frame index * the exact
                    // target interval) instead of real elapsed time - the file's
                    // computed average frame rate (what File Explorer/players
                    // show) is then EXACTLY the configured target by construction,
                    // instead of a close-but-jittery approximation from real
                    // scheduler timing. Audio alignment doesn't use this - it gets
                    // its own real wall-clock timestamp below, specifically so
                    // idealizing video's timeline can't reintroduce the audio-sync
                    // bug that was just fixed.
                    frame->pts = (long)Math.Round(nextPtsMicroseconds);
                    nextPtsMicroseconds += idealFrameIntervalMicroseconds;

                    // A pad (nothing new since the last encode) is a byte-identical
                    // copy of the frame before it. Encoding one costs a full pass
                    // through the encoder for zero information, and whenever the
                    // source presents slower than the target it is not a rare event:
                    // a 43fps source into a 60fps target made a quarter of all
                    // encoded frames pads on one measured Iris Xe laptop, where
                    // encode runs on the same execution units the game renders with.
                    // On a backed-up queue it is worse still - the pad also pushes
                    // the real frame behind it further back (the same logs showed
                    // framesEncoded at 2.7x framesSeen with the queue pinned at
                    // 30/30 and output collapsed to 18.8fps).
                    //
                    // Suppressing pads unconditionally was tried and reverted. It
                    // did cut encoder work, but it also made the output rate the
                    // SOURCE's rate rather than the configured one: a game whose
                    // presentation timing is merely uneven (maxPresentGapMs of
                    // 36-41ms against a 16.7ms target, measured on a 4070 Ti at a
                    // steady ~85fps average) leaves real holes in the tick grid,
                    // and a clip that should read 60fps came out at 53.6. Exact
                    // constant frame rate is the requirement; the pads that buys
                    // are cheap on a working encoder (avgEncodeMs 0.46 on NVENC in
                    // the same logs) and the GPU savings came from the crop-copy
                    // limiter and the encoder settings, not from here.
                    //
                    // So this stays what it was: a safety valve, not a policy. The
                    // timeline advances above either way (lastEncodedAt and
                    // nextPtsMicroseconds both moved before this check), so a
                    // skipped pad reads as a dropped frame in a correctly-spaced
                    // timeline rather than a slower clip - the same shape as the
                    // catch-up cap right above, which already decided that under
                    // stall conditions honesty beats padding. Only ever skipped
                    // while the queue is genuinely deep, so steady-state output is
                    // exactly the configured frame rate.
                    // Arms only when the queue is about to overflow, not at half
                    // depth. Half was too eager by a wide margin: a 30-deep
                    // queue routinely sits at 16 during normal gameplay (the
                    // whole point of having a queue is absorbing exactly that),
                    // and every tick spent above 15 with nothing newly presented
                    // punched a hole in the timeline. Measured result was clips
                    // reading 49-51fps on a 60fps capture whose encoder was
                    // keeping up - framesEncoded in the diag showed 60.5/s while
                    // padsSkipped ran 13 per 2s window. A pad is also far
                    // cheaper than it used to be: on the zero-copy path it
                    // re-sends the SAME texture, so it costs a frame clone here
                    // and a near-empty P-frame in the encoder.
                    if (!freshContentSinceLastEncode && encodeQueue.Count + 2 >= encodeQueueCapacity)
                    {
                        padsSkippedSinceLog++;
                        continue;
                    }

                    EncodeScheduledFrame();
                    freshContentSinceLastEncode = false;
                }
                }

                if (stopwatch.Elapsed - lastRingTrim >= TimeSpan.FromSeconds(1))
                {
                    lastRingTrim = stopwatch.Elapsed;
                    TrimRingBuffer(fullSessionFormatContext is not null ? fullSessionStartUtc : (DateTime?)null);
                    // Audio captures ended mid-session (e.g. a route change when the
                    // game/chat app/mic changes - see AudioCapturePipeline.
                    // StopStaleAudioCaptures) only get their file handle closed, not
                    // deleted; without this the raw WAV files pile up on disk for the
                    // entire lifetime of a long-running session instead of being
                    // cleaned up as soon as they're no longer needed. While a full
                    // session is recording, never prune past its start - its finalize
                    // muxes audio from session start, including captures that ended
                    // mid-session (4GiB WAV rollovers, route changes).
                    var audioCutoffUtc = MonotonicClock.UtcNow - Duration - TimeSpan.FromSeconds(5);
                    if (fullSessionFormatContext is not null && fullSessionStartUtc < audioCutoffUtc) audioCutoffUtc = fullSessionStartUtc;
                    _audio.PruneOlderThan(audioCutoffUtc);
                }
            }

            // Stop accepting new jobs and wait for EncodeLoop to drain everything
            // already queued (including its own final flush of whatever's still
            // buffered inside the encoder) - the finally block below also does
            // this on any exception path, so this is a no-op there, not a
            // duplicate drain.
            encodeQueue.CompleteAdding();
            encodeThread.Join();
        }
        catch (Exception error)
        {
            AppLog.Error("Native capture loop failed.", error);
            SetHealth(_health with { State = ReplayCaptureState.Failed, LastFailure = error.Message, UpdatedUtc = DateTime.UtcNow });
            ready.TrySetException(error);
            _sessionActive = false;

            // The session is dead, so nothing will ever consume these captures.
            // StopAsync can't do this cleanup on our behalf - it early-returns
            // on !_sessionActive, which we just cleared - so without stopping
            // here the audio pipeline kept capturing (and re-resolving its
            // route on every device change) indefinitely behind a capture loop
            // that no longer exists.
            try
            {
                _audio.Stop(deleteCaptureFiles: true);
            }
            catch (Exception audioError)
            {
                AppLog.Error("Native capture: audio shutdown after capture-loop failure failed.", audioError);
            }

            RecordingStopped?.Invoke(this, EventArgs.Empty);
        }
        finally
        {
            // Guarantee the encode thread is fully stopped (and has released its
            // last cloned frame) before codecContext/packet get freed below, on
            // EVERY exit path - the happy path above already did this, so this is
            // a no-op there; an exception thrown mid-loop is the path that
            // actually needs it here.
            try
            {
                encodeQueue?.CompleteAdding();
                encodeThread?.Join();
            }
            catch (Exception error)
            {
                AppLog.Error("Native capture: encode thread shutdown failed.", error);
            }
            encodeQueue?.Dispose();

            if (frame is not null) { var f = frame; ffmpeg.av_frame_free(&f); }
            if (packet is not null) { var p = packet; ffmpeg.av_packet_free(&p); }
            if (swsContext is not null) ffmpeg.sws_freeContext(swsContext);
            FinalizeFullSessionWriter(fullSessionFormatContext);
            if (!string.IsNullOrEmpty(fullSessionTempVideoPath))
            {
                var finalizeConfig = _configProvider();
                if (finalizeConfig.FullSessionBackgroundFinalize)
                {
                    // Snapshot the capture set NOW - StopAsync clears the live
                    // list (without deleting files, see its comment) the
                    // moment this loop returns.
                    var captureSnapshot = _audio.SnapshotCaptures();
                    var startUtc = fullSessionStartUtc;
                    var startWallUtc = fullSessionStartWallUtc;
                    var tempPath = fullSessionTempVideoPath;
                    var finalPath = fullSessionFinalOutputPath;
                    var gameName = fullSessionGameDisplayName;

                    // Make the session visible IMMEDIATELY as a video-only
                    // file; the background job then muxes audio into it via a
                    // swap. If the move fails (cross-volume oddity), the job
                    // just works from the temp file like the synchronous path.
                    var videoPath = tempPath;
                    try
                    {
                        File.Move(tempPath, finalPath);
                        videoPath = finalPath;
                        var immediateGameName = !string.IsNullOrWhiteSpace(gameName) && !string.Equals(gameName, "No game detected", StringComparison.OrdinalIgnoreCase)
                            ? gameName
                            : finalizeConfig.GameDisplayName;
                        ClipInfoSidecar.Save(finalizeConfig.LibraryFolder, finalPath, new ClipInfo(immediateGameName, null, $"Session - {immediateGameName}", startWallUtc, CaptureSource: finalizeConfig.CaptureSource));
                        AppLog.Info($"Full session video available immediately (audio attaching in background): {finalPath}.");
                    }
                    catch (Exception error)
                    {
                        AppLog.Error("Full session immediate video move failed; background finalize will produce the file instead.", error);
                    }

                    Interlocked.Increment(ref _activeBackgroundFinalizes);
                    var capturedVideoPath = videoPath;
                    _backgroundFinalize = Task.Run(() =>
                    {
                        try
                        {
                            FinalizeFullSessionRecording(finalizeConfig, startUtc, startWallUtc, capturedVideoPath, finalPath, gameName, captureSnapshot);
                        }
                        finally
                        {
                            foreach (var path in captureSnapshot.FilePaths) AudioCapturePipeline.TryDelete(path);
                            Interlocked.Decrement(ref _activeBackgroundFinalizes);
                            AppLog.Info($"Full session background finalize complete: final={finalPath}.");
                        }
                    });
                }
                else
                {
                    FinalizeFullSessionRecording(finalizeConfig, fullSessionStartUtc, fullSessionStartWallUtc, fullSessionTempVideoPath, fullSessionFinalOutputPath, fullSessionGameDisplayName);
                }
            }
            if (codecContext is not null) { var c = codecContext; ffmpeg.avcodec_free_context(&c); }
            // Encoders replaced mid-session (device rebuild). Safe only here:
            // the encode thread has already been joined above, so nothing can
            // still be inside one.
            foreach (var retired in retiredCodecContexts)
            {
                var context = (AVCodecContext*)retired;
                ffmpeg.avcodec_free_context(&context);
            }
            retiredCodecContexts.Clear();
            hasHardwareContent = false;
            // Never disposed, only dropped - these wrappers were built over
            // pointers the pool owns and hold no reference of their own.
            hardwarePoolTextures.Clear();
            ReleaseHardwareFrames(ref hwDeviceRef, ref hwFramesRef);
            duplication?.Dispose();
            staging?.Dispose();
            foreach (var view in desktopInputViews.Values) view.Dispose();
            desktopInputViews.Clear();
            inputView?.Dispose();
            croppedTexture?.Dispose();
            outputView?.Dispose();
            if (nv12StagingRing is not null) foreach (var t in nv12StagingRing) t.Dispose();
            nv12Output?.Dispose();
            videoProcessor?.Dispose();
            vpEnumerator?.Dispose();
            videoContext?.Dispose();
            videoDevice?.Dispose();
            device?.Dispose();
            if (timerResolutionRaised) TimeEndPeriod(1);
            if (_health.State != ReplayCaptureState.Failed)
            {
                SetHealth(_health with { State = ReplayCaptureState.Stopped, UpdatedUtc = DateTime.UtcNow });
            }
        }
    }

    private static unsafe void FillFrameBlack(AVFrame* frame, int height)
    {
        // Y=16, matching the limited-range NV12 both scalers now produce (see
        // CreateEncoder's AVCOL_RANGE_MPEG). 0 is below-black there and would
        // be a slightly different black from the rest of the recording.
        var ySize = (uint)(frame->linesize[0] * height);
        System.Runtime.CompilerServices.Unsafe.InitBlockUnaligned((void*)frame->data[0], 16, ySize);

        var uvHeight = (height + 1) / 2;
        var uvSize = (uint)(frame->linesize[1] * uvHeight);
        System.Runtime.CompilerServices.Unsafe.InitBlockUnaligned((void*)frame->data[1], 128, uvSize);
    }

    private static unsafe SwsContext* CreateScaler(int sourceWidth, int sourceHeight, int outputWidth, int outputHeight)
    {
        var swsContext = ffmpeg.sws_getContext(
            sourceWidth, sourceHeight, AVPixelFormat.AV_PIX_FMT_BGRA,
            outputWidth, outputHeight, AVPixelFormat.AV_PIX_FMT_NV12,
            // Lanczos, not bilinear. Measured on a synthetic 2:1 downscale of
            // real capture content, bilinear scores SSIM 0.9952 (23.2dB) against
            // lanczos' 0.9987 (28.8dB) - a 5.6dB detail loss purely from the
            // resampling kernel, which is exactly the "looks like a lower
            // resolution" symptom. ACCURATE_RND drops the rounding shortcuts
            // that would otherwise give some of that back.
            (int)(SwsFlags.SWS_LANCZOS | SwsFlags.SWS_ACCURATE_RND), null, null, null);
        if (swsContext is null) throw new InvalidOperationException("sws_getContext failed.");

        // Desktop Duplication's BGRA capture is full-range (0-255), so the
        // source side is 1; the NV12 output side is 0, studio/limited range
        // (16-235), because that is what H.264 consumers assume when they
        // don't read the VUI flag - see CreateEncoder for why we no longer
        // rely on that flag being honoured. BT.709 coefficients (not swscale's
        // BT.601 default) because that's what the encoder tags the output as,
        // and what any player assumes for HD regardless - converting with 601
        // and being decoded as 709 is a real colour shift.
        var coefficientsPtr = ffmpeg.sws_getCoefficients(ffmpeg.SWS_CS_ITU709);
        var coefficients = new int_array4();
        coefficients.UpdateFrom(new[] { coefficientsPtr[0], coefficientsPtr[1], coefficientsPtr[2], coefficientsPtr[3] });
        ffmpeg.sws_setColorspaceDetails(swsContext, in coefficients, 1 /* full source */, in coefficients, 0 /* limited output */, 0, 1 << 16, 1 << 16);

        return swsContext;
    }

    private static ID3D11Texture2D CreateStagingTexture(ID3D11Device device, int width, int height)
    {
        return device.CreateTexture2D(new Texture2DDescription
        {
            Width = (uint)Math.Max(1, width),
            Height = (uint)Math.Max(1, height),
            MipLevels = 1,
            ArraySize = 1,
            Format = Format.B8G8R8A8_UNorm,
            SampleDescription = new SampleDescription(1, 0),
            Usage = ResourceUsage.Staging,
            CPUAccessFlags = CpuAccessFlags.Read,
            BindFlags = BindFlags.None
        });
    }

    // Sets up the D3D11 Video Processor to do the crop->NV12-downscale that
    // sws_scale otherwise does on the CPU, entirely on the GPU. Only the
    // final small output-resolution NV12 texture ever gets read back to the
    // CPU afterward (via nv12Staging), instead of the full captured crop
    // (often 4K) every single frame. Throws on any failure - caller treats
    // that as "not supported on this hardware/driver" and falls back to CPU
    // scale, so nothing here needs to be defensive beyond that.
    private static (ID3D11VideoDevice VideoDevice, ID3D11VideoContext VideoContext, ID3D11VideoProcessorEnumerator Enumerator, ID3D11VideoProcessor Processor, ID3D11Texture2D Nv12Output, ID3D11Texture2D[] Nv12StagingRing, ID3D11VideoProcessorOutputView OutputView)
        CreateGpuScaler(ID3D11Device device, int captureWidth, int captureHeight, int outputWidth, int outputHeight, int frameRate)
    {
        var videoDevice = device.QueryInterface<ID3D11VideoDevice>();
        var videoContext = device.ImmediateContext.QueryInterface<ID3D11VideoContext>();

        var contentDescription = new VideoProcessorContentDescription
        {
            InputFrameFormat = VideoFrameFormat.Progressive,
            InputWidth = (uint)captureWidth,
            InputHeight = (uint)captureHeight,
            OutputWidth = (uint)outputWidth,
            OutputHeight = (uint)outputHeight,
            InputFrameRate = new Rational((uint)Math.Clamp(frameRate, 15, 240), 1),
            OutputFrameRate = new Rational((uint)Math.Clamp(frameRate, 15, 240), 1),
            // Capture runs continuously at the target frame rate, unlike a
            // one-off export. OptimalQuality can make the driver spend more
            // 3D time on every crop/scale/colour-conversion pass, competing
            // with the game for the same GPU. The output is immediately fed
            // to NVENC, so throughput matters more than a premium resize
            // kernel here. OptimalSpeed keeps the conversion GPU-side while
            // avoiding that quality-biased per-frame cost.
            Usage = VideoUsage.OptimalSpeed
        };
        ID3D11VideoProcessorEnumerator enumerator;
        try
        {
            enumerator = videoDevice.CreateVideoProcessorEnumerator(contentDescription);
        }
        catch (Exception error)
        {
            throw new InvalidOperationException($"CreateVideoProcessorEnumerator failed: {error.Message}", error);
        }

        ID3D11VideoProcessor processor;
        try
        {
            processor = videoDevice.CreateVideoProcessor(enumerator, 0);
        }
        catch (Exception error)
        {
            throw new InvalidOperationException($"CreateVideoProcessor failed: {error.Message}", error);
        }

        // The two ends of this conversion are deliberately different ranges, so
        // they get their own structs. D3D11_VIDEO_PROCESSOR_NOMINAL_RANGE is
        // UNDEFINED=0, 16_235=1, 0_255=2.
        //
        // Input: Desktop Duplication hands over full-range (0-255) RGB, so the
        // stream colour space says Range_0_255 - anything else has the driver
        // mis-reading the captured pixels before it converts them.
        //
        // Output: limited range (16-235) NV12, matching the CPU sws_scale path
        // in CreateScaler and what CreateEncoder now tags the bitstream as.
        // Both are 1 vs 2 flips away from full-range, which is what this used
        // to be - see CreateEncoder for why signalling full range in the VUI
        // turned out not to be enough to get it read back that way.
        //
        // YCbCr_Matrix=1 is BT.709, matching the encoder's tag rather than the
        // BT.601 default that silently disagreed with how every player reads an
        // HD stream.
        var inputColorSpace = new VideoProcessorColorSpace
        {
            Nominal_Range = (uint)VideoProcessorNominalRange.Range_0_255,
            YCbCr_Matrix = 1
        };
        var outputColorSpace = new VideoProcessorColorSpace
        {
            Nominal_Range = (uint)VideoProcessorNominalRange.Range_16_235,
            YCbCr_Matrix = 1
        };
        videoContext.VideoProcessorSetStreamColorSpace(processor, 0, inputColorSpace);
        videoContext.VideoProcessorSetOutputColorSpace(processor, outputColorSpace);

        // Auto processing is ON by default, and Usage=OptimalSpeed above does
        // NOT turn it off - they are separate switches. Left enabled, the
        // driver is free to run its own enhancement pass on every frame
        // (NVIDIA's does: denoise, edge/detail enhancement, a higher-quality
        // resize kernel), which is exactly the "extra video processing" this
        // call exists to refuse.
        //
        // Measured on an RTX 4070 Ti capturing 3840x2160 -> 1920x1080 at 60fps:
        // ClypDat's own 3D-engine share sat at 8-17% with the window closed to
        // the tray and nothing at all being drawn, while the video-encode
        // engine held flat at ~6%. With the UI proven not to be the source
        // (one InvalidateVisual call site in the whole app, no infinite
        // animations, and hiding the window changed nothing), a per-frame
        // 4K enhancement pass is what is left holding that floor.
        //
        // Frame-rate conversion is refused for the same reason: the encoder
        // wants one output frame per input frame, and letting the driver
        // interpolate toward some other rate is work nobody asked for.
        try
        {
            videoContext.VideoProcessorSetStreamAutoProcessingMode(processor, 0, false);
            videoContext.VideoProcessorSetStreamOutputRate(
                processor, 0, VideoProcessorOutputRate.Normal, false, null);
        }
        catch (Exception error)
        {
            // Both are hints a driver is allowed to ignore. A driver that
            // rejects them outright still scales correctly, just at whatever
            // cost it prefers - not a reason to fail the capture.
            AppLog.Info($"Native capture: video processor tuning rejected by the driver ({error.Message}) - scaling continues at driver defaults.");
        }

        // Many D3D11 video processing samples create the VP output resource
        // with BindFlags.RenderTarget even though nothing ever binds it as
        // one - some drivers reject CreateVideoProcessorOutputView with
        // E_INVALIDARG on a plain BindFlags.None Default texture otherwise.
        ID3D11Texture2D nv12Output;
        try
        {
            nv12Output = device.CreateTexture2D(new Texture2DDescription
            {
                Width = (uint)outputWidth,
                Height = (uint)outputHeight,
                MipLevels = 1,
                ArraySize = 1,
                Format = Format.NV12,
                SampleDescription = new SampleDescription(1, 0),
                Usage = ResourceUsage.Default,
                CPUAccessFlags = CpuAccessFlags.None,
                BindFlags = BindFlags.RenderTarget
            });
        }
        catch (Exception error)
        {
            throw new InvalidOperationException($"CreateTexture2D (nv12Output) failed: {error.Message}", error);
        }

        ID3D11VideoProcessorOutputView outputView;
        try
        {
            outputView = videoDevice.CreateVideoProcessorOutputView(nv12Output, enumerator, new VideoProcessorOutputViewDescription
            {
                ViewDimension = VideoProcessorOutputViewDimension.Texture2D
            });
        }
        catch (Exception error)
        {
            throw new InvalidOperationException($"CreateVideoProcessorOutputView failed: {error.Message}", error);
        }

        // Four slots, not two. Depth here buys tolerance for the GPU running
        // late: the readback of a slot can only be picked up once its copy has
        // actually finished, and under real contention (a 4K game spiking) that
        // took longer than the single encode interval two slots allow, which
        // showed up as the readback blocking and cascading into dropped frames.
        // Costs three extra NV12 surfaces - a few MB at 1080p.
        var nv12StagingRing = new[]
        {
            CreateNv12StagingTexture(device, outputWidth, outputHeight),
            CreateNv12StagingTexture(device, outputWidth, outputHeight),
            CreateNv12StagingTexture(device, outputWidth, outputHeight),
            CreateNv12StagingTexture(device, outputWidth, outputHeight)
        };

        return (videoDevice, videoContext, enumerator, processor, nv12Output, nv12StagingRing, outputView);
    }

    private static ID3D11Texture2D CreateNv12StagingTexture(ID3D11Device device, int width, int height)
    {
        return device.CreateTexture2D(new Texture2DDescription
        {
            Width = (uint)width,
            Height = (uint)height,
            MipLevels = 1,
            ArraySize = 1,
            Format = Format.NV12,
            SampleDescription = new SampleDescription(1, 0),
            Usage = ResourceUsage.Staging,
            CPUAccessFlags = CpuAccessFlags.Read,
            BindFlags = BindFlags.None
        });
    }

    // The crop-sized GPU-only source texture the Video Processor reads from,
    // and its input view - rebuilt whenever the crop size changes (window
    // resize), same as the CPU path's staging texture/swsContext.
    private static (ID3D11Texture2D CroppedTexture, ID3D11VideoProcessorInputView InputView) CreateGpuCropInputView(
        ID3D11Device device, ID3D11VideoDevice videoDevice, ID3D11VideoProcessorEnumerator enumerator, int width, int height)
    {
        var croppedTexture = device.CreateTexture2D(new Texture2DDescription
        {
            Width = (uint)Math.Max(1, width),
            Height = (uint)Math.Max(1, height),
            MipLevels = 1,
            ArraySize = 1,
            Format = Format.B8G8R8A8_UNorm,
            SampleDescription = new SampleDescription(1, 0),
            Usage = ResourceUsage.Default,
            CPUAccessFlags = CpuAccessFlags.None,
            BindFlags = BindFlags.None
        });

        var inputView = videoDevice.CreateVideoProcessorInputView(croppedTexture, enumerator, new VideoProcessorInputViewDescription
        {
            FourCC = 0,
            ViewDimension = VideoProcessorInputViewDimension.Texture2D,
            Texture2D = new Texture2DVideoProcessorInputView { MipSlice = 0, ArraySlice = 0 }
        });

        return (croppedTexture, inputView);
    }

    // D3D11's NV12 textures map as a single contiguous surface: the luma (Y)
    // plane's rows first, then the chroma (interleaved UV) plane's rows
    // immediately after, both using the SAME row pitch - not two separate
    // Map calls. Copied per-row since the D3D11 stride (mapped.RowPitch) and
    // ffmpeg's own allocated stride (frame->linesize) are aligned
    // differently and can't just be bulk-memcpy'd as one block.
    private static unsafe void CopyNv12PlanesToFrame(MappedSubresource mapped, int width, int height, AVFrame* frame)
    {
        var srcStride = (int)mapped.RowPitch;
        var ySrc = (byte*)mapped.DataPointer;
        var yDst = frame->data[0];
        var yDstStride = frame->linesize[0];
        // The two strides do often agree in practice (D3D11's NV12 pitch and
        // ffmpeg's align-32 linesize both land on 1920 at 1080p, for one), and
        // when they do the whole plane is one contiguous block - 1080 separate
        // memcpy calls collapse into one. The per-row path stays for when they
        // don't, which is the only case the original comment above covers.
        if (srcStride == yDstStride)
        {
            Buffer.MemoryCopy(ySrc, yDst, (long)yDstStride * height, (long)srcStride * height);
        }
        else
        {
            for (var row = 0; row < height; row++)
            {
                Buffer.MemoryCopy(ySrc + row * srcStride, yDst + row * yDstStride, yDstStride, width);
            }
        }

        var uvHeight = (height + 1) / 2;
        var uvSrc = ySrc + srcStride * height;
        var uvDst = frame->data[1];
        var uvDstStride = frame->linesize[1];
        if (srcStride == uvDstStride)
        {
            Buffer.MemoryCopy(uvSrc, uvDst, (long)uvDstStride * uvHeight, (long)srcStride * uvHeight);
        }
        else
        {
            for (var row = 0; row < uvHeight; row++)
            {
                Buffer.MemoryCopy(uvSrc + row * srcStride, uvDst + row * uvDstStride, uvDstStride, width);
            }
        }
    }

    // Finds the DXGI output covering whichever monitor the target window (or the
    // primary monitor, in monitor-capture mode) is on and duplicates it.
    // DesktopCoordinates comes back out so the caller can convert the window's
    // screen-space rect into texture-local crop coordinates every frame.
    // The output a given target resolves to. Kept separate from
    // CreateDuplicationFor so the capture loop can ask "would this target need a
    // different duplication?" without building one to find out.
    private static nint ResolveTargetMonitor(nint targetHandle, ReplayBufferConfig config)
    {
        if (targetHandle != 0) return MonitorFromWindow(targetHandle, MONITOR_DEFAULTTONEAREST);
        if (string.Equals(config.CaptureSource, "Desktop", StringComparison.OrdinalIgnoreCase))
        {
            var monitor = DesktopMonitorService.Resolve(config.CaptureMonitorDeviceName);
            return MonitorFromPoint(new PointStruct { X = monitor.X + Math.Max(1, monitor.Width / 2), Y = monitor.Y + Math.Max(1, monitor.Height / 2) }, MONITOR_DEFAULTTONEAREST);
        }
        return GetPrimaryMonitorHandle();
    }

    // Desktop Duplication reports pointer movement separately from pixels. Keep
    // native desktop captures truthful with a small high-contrast arrow on the
    // CPU fallback path; game capture intentionally never enters this path.
    private static unsafe void DrawDesktopCursor(byte* pixels, int stride, int width, int height, int x, int y)
    {
        for (var row = 0; row < 15; row++)
        {
            for (var column = 0; column < 12; column++)
            {
                var inside = column <= row / 2 || (row > 7 && column >= 3 && column <= 6 && row - 7 <= column - 2);
                if (!inside) continue;
                var px = x + column;
                var py = y + row;
                if (px < 0 || py < 0 || px >= width || py >= height) continue;
                var edge = column == 0 || column == row / 2 || row == 0;
                var target = pixels + py * stride + px * 4;
                target[0] = edge ? (byte)0 : (byte)245;
                target[1] = edge ? (byte)0 : (byte)245;
                target[2] = edge ? (byte)0 : (byte)245;
                target[3] = 255;
            }
        }
    }

    // Same arrow as DrawDesktopCursor, drawn straight into the already-scaled NV12
    // frame so the GPU scaler path can keep the cursor without falling back to a
    // full-resolution CPU sws_scale. The shape is deliberately kept identical to the
    // CPU path's so a recording looks the same whichever path a machine takes.
    //
    // The arrow is greyscale, so only the Y plane carries it and the chroma pair is
    // pushed to neutral - no colour conversion is involved. Y uses limited range (16
    // = black, 235 = white) to match what the video processor writes for the rest of
    // the frame; using 0/255 here would make the arrow clip against it.
    private static unsafe void DrawDesktopCursorNv12(AVFrame* frame, int width, int height, int x, int y)
    {
        var yPlane = frame->data[0];
        var yStride = frame->linesize[0];
        var uvPlane = frame->data[1];
        var uvStride = frame->linesize[1];

        for (var row = 0; row < 15; row++)
        {
            for (var column = 0; column < 12; column++)
            {
                var inside = column <= row / 2 || (row > 7 && column >= 3 && column <= 6 && row - 7 <= column - 2);
                if (!inside) continue;
                var px = x + column;
                var py = y + row;
                if (px < 0 || py < 0 || px >= width || py >= height) continue;
                var edge = column == 0 || column == row / 2 || row == 0;
                yPlane[py * yStride + px] = edge ? (byte)16 : (byte)235;

                // One chroma sample covers a 2x2 luma block, so this writes the same
                // neutral pair repeatedly for interior pixels. Cheaper than tracking
                // which blocks were already touched, at 180 pixels a frame.
                var uv = uvPlane + (py / 2) * uvStride + (px / 2) * 2;
                uv[0] = 128;
                uv[1] = 128;
            }
        }
    }

    // The GPU's own name ("NVIDIA GeForce RTX 4070 Ti"), for diagnostics and for
    // keying any learned per-machine encoder tuning - a learned value has no
    // business following the user onto different hardware. Best-effort: this is
    // reporting only, so a failure here must never take capture down with it.
    private static string DescribeAdapter(ID3D11Device device)
    {
        try
        {
            using var dxgiDevice = device.QueryInterface<IDXGIDevice>();
            using var adapter = dxgiDevice.GetParent<IDXGIAdapter>();
            var description = adapter.Description.Description;
            return string.IsNullOrWhiteSpace(description) ? "Unknown adapter" : description.Trim();
        }
        catch (Exception error)
        {
            AppLog.Info($"Native capture: could not read adapter description ({error.Message}).");
            return "Unknown adapter";
        }
    }

    private static IDXGIOutputDuplication CreateDuplicationFor(ID3D11Device device, nint targetHandle, ReplayBufferConfig config, out Vortice.RawRect desktopBounds)
    {
        var monitorHandle = ResolveTargetMonitor(targetHandle, config);

        using var dxgiDevice = device.QueryInterface<IDXGIDevice>();
        using var adapter = dxgiDevice.GetParent<IDXGIAdapter>();

        for (uint i = 0; ; i++)
        {
            var enumResult = adapter.EnumOutputs(i, out var output);
            if (enumResult.Failure) break;

            using (output)
            {
                if (output.Description.Monitor != monitorHandle) continue;

                using var output1 = output.QueryInterface<IDXGIOutput1>();
                desktopBounds = output.Description.DesktopCoordinates;
                return output1.DuplicateOutput(device);
            }
        }

        throw new InvalidOperationException("No DXGI output found for the target monitor.");
    }

    // Only used once at loop startup (the crop rect is recomputed every frame
    // afterward) - falls back to the full monitor if the window rect can't be
    // read yet (e.g. window still opening), self-corrects on the next frame.
    private static (int Width, int Height) GetInitialCropSize(nint targetHandle, Vortice.RawRect desktopBounds)
    {
        if (TryGetWindowCropRect(targetHandle, desktopBounds, out _, out _, out var width, out var height))
        {
            return (width, height);
        }

        return (desktopBounds.Right - desktopBounds.Left, desktopBounds.Bottom - desktopBounds.Top);
    }

    // Converts the target window's current screen-space rect into coordinates
    // local to the duplicated desktop texture (which starts at desktopBounds'
    // top-left, not (0,0) on multi-monitor setups), clipped to the monitor.
    // DWMWA_EXTENDED_FRAME_BOUNDS is the window's actual visible bounds
    // (excludes the invisible resize-shadow margin GetWindowRect includes on
    // Windows 10/11), falling back to GetWindowRect if unavailable.
    private static bool TryGetWindowCropRect(nint handle, Vortice.RawRect desktopBounds, out int left, out int top, out int width, out int height)
    {
        left = top = width = height = 0;
        if (!IsWindow(handle) || IsIconic(handle)) return false;

        if (DwmGetWindowAttribute(handle, DWMWA_EXTENDED_FRAME_BOUNDS, out var rect, Marshal.SizeOf<RectStruct>()) != 0)
        {
            if (!GetWindowRect(handle, out rect)) return false;
        }

        var clipLeft = Math.Max(rect.Left, desktopBounds.Left);
        var clipTop = Math.Max(rect.Top, desktopBounds.Top);
        var clipRight = Math.Min(rect.Right, desktopBounds.Right);
        var clipBottom = Math.Min(rect.Bottom, desktopBounds.Bottom);
        if (clipRight <= clipLeft || clipBottom <= clipTop) return false;

        left = clipLeft - desktopBounds.Left;
        top = clipTop - desktopBounds.Top;
        width = clipRight - clipLeft;
        height = clipBottom - clipTop;
        return true;
    }

    // "Foreground" (not just "visible") is the deliberate bar here - a window
    // can be fully visible on a second monitor while the user is alt-tabbed
    // into something covering the game's own monitor, and DXGI Desktop
    // Duplication captures per-monitor composited output either way, so
    // foreground is the only reliable "nothing else could be leaking into this
    // frame" signal available without walking the full z-order.
    private static bool IsWindowForegroundAndVisible(nint handle) =>
        IsWindow(handle) && !IsIconic(handle) && GetForegroundWindow() == handle;

    private static nint ResolveTargetWindow(ReplayBufferConfig config)
    {
        return config.GameWindowHandle != 0 && IsWindow(config.GameWindowHandle) ? config.GameWindowHandle : 0;
    }

    private static nint GetPrimaryMonitorHandle()
    {
        const uint MONITOR_DEFAULTTOPRIMARY = 1;
        return MonitorFromPoint(default, MONITOR_DEFAULTTOPRIMARY);
    }

    // FramePtr is an AVFrame* smuggled across the thread boundary as nint -
    // pointer types can't be used as generic type arguments (BlockingCollection<T>
    // here) or captured by a lambda closure, both of which this needs. Owned by
    // whichever side currently holds it: the capture thread until TryAdd
    // succeeds, EncodeLoop from then on (which is responsible for freeing it).
    // FramePtr == 0 marks a control job: flush the current encoder and continue
    // on SwapCodecContext instead. Sent through the queue rather than applied
    // directly so the switch happens between two frames, with everything
    // already queued encoded on the encoder that produced its timeline - see
    // its use in the D3D device rebuild, which invalidates the D3D11 texture
    // pool the old encoder was reading from.
    private readonly record struct EncodeJob(
        nint FramePtr,
        DateTime WallClockUtc,
        nint SwapCodecContext = 0,
        ManualResetEventSlim? SwapCompleted = null);

    // Runs avcodec_send_frame/receive_packet (and so DrainToRingBuffer, and the
    // full-session mux write inside it) on its own thread, decoupled from
    // CaptureLoop's AcquireNextFrame loop. Existed as a single synchronous call
    // inline in CaptureLoop originally - fine when NVENC keeps up, but a real
    // GPU-contention stall there (confirmed via avgEncodeMs spiking 20x+ baseline
    // with frames backing up hundreds deep in Native capture diag) blocked
    // AcquireNextFrame right along with it, since it was the same thread. This
    // loop owns codecContext/packet/pendingFrameWallClocks exclusively from here
    // on - CaptureLoop never touches them again after starting this thread.
    private unsafe void EncodeLoop(BlockingCollection<EncodeJob> queue, nint codecContextPtr, nint packetPtr, nint fullSessionFormatContextPtr, nint fullSessionStreamPtr)
    {
        var codecContext = (AVCodecContext*)codecContextPtr;
        var packet = (AVPacket*)packetPtr;
        var fullSessionFormatContext = (AVFormatContext*)fullSessionFormatContextPtr;
        var fullSessionStream = (AVStream*)fullSessionStreamPtr;
        // Same FIFO purpose as the original inline version - see DrainToRingBuffer's
        // dequeue site - just living here now since send_frame moved here with it.
        var pendingFrameWallClocks = new LinkedList<DateTime>();
        try
        {
            foreach (var job in queue.GetConsumingEnumerable())
            {
                if (job.FramePtr == 0)
                {
                    // Control job - see EncodeJob. Flush what the outgoing
                    // encoder still holds into the ring before switching, so no
                    // already-accepted frame is lost across the swap. The old
                    // context is NOT freed here: CaptureLoop owns every context
                    // it created and frees them once this thread has joined,
                    // which is the only point at which nothing can still be
                    // inside one.
                    if (job.SwapCodecContext != 0)
                    {
                        ffmpeg.avcodec_send_frame(codecContext, null);
                        DrainToRingBuffer(codecContext, packet, fullSessionFormatContext, fullSessionStream, pendingFrameWallClocks);
                        codecContext = (AVCodecContext*)job.SwapCodecContext;
                    }

                    job.SwapCompleted?.Set();
                    continue;
                }

                var jobFrame = (AVFrame*)job.FramePtr;
                var sw = System.Diagnostics.Stopwatch.StartNew();
                try
                {
                    // EAGAIN from send_frame is not a failure - it means the
                    // encoder is holding output that has to be collected before
                    // it will take another frame. This used to be treated as
                    // "oh well" and the frame was freed unsent, silently and
                    // uncounted: a saved clip showed 112 missing ticks scattered
                    // across 61s (24 single-frame gaps, 22 double, and so on)
                    // while droppedFrames and padsSkipped both read 0, which is
                    // what a 60fps capture reading 58fps in the file actually
                    // was. Drain and offer the frame again instead.
                    // Appended BEFORE the send, and taken back off only if the
                    // frame is ultimately refused. The drain below (and the one
                    // inside the EAGAIN retry) pops one of these per packet the
                    // encoder releases, and those packets belong to frames sent
                    // several calls ago - so appending after a successful send
                    // starves the queue exactly when the retry path runs, and
                    // every packet drained there falls back to "now" for its
                    // capture timestamp, which is the value audio alignment is
                    // built on.
                    pendingFrameWallClocks.AddLast(job.WallClockUtc);
                    var sendResult = ffmpeg.avcodec_send_frame(codecContext, jobFrame);
                    if (sendResult == ffmpeg.AVERROR(ffmpeg.EAGAIN))
                    {
                        DrainToRingBuffer(codecContext, packet, fullSessionFormatContext, fullSessionStream, pendingFrameWallClocks);
                        sendResult = ffmpeg.avcodec_send_frame(codecContext, jobFrame);
                    }

                    if (sendResult == 0)
                    {
                        DrainToRingBuffer(codecContext, packet, fullSessionFormatContext, fullSessionStream, pendingFrameWallClocks);
                    }
                    else
                    {
                        // Genuinely refused. Counted now, so a clip that comes
                        // up short says so in the diag instead of looking clean.
                        // Its timestamp comes back off too - no packet will ever
                        // correspond to it.
                        pendingFrameWallClocks.RemoveLast();
                        Interlocked.Increment(ref _encodeDroppedCount);
                        Interlocked.Increment(ref _totalDroppedFrames);
                    }
                }
                finally
                {
                    ffmpeg.av_frame_free(&jobFrame);
                }
                Interlocked.Add(ref _encodeMicrosAccum, (long)(sw.Elapsed.TotalMilliseconds * 1000));
                Interlocked.Increment(ref _encodeCountAccum);
            }

            // Queue drained and CompleteAdding was called (CaptureLoop's while
            // loop exited) - flush whatever's still buffered inside the encoder
            // itself, same as the original inline flush used to.
            ffmpeg.avcodec_send_frame(codecContext, null);
            DrainToRingBuffer(codecContext, packet, fullSessionFormatContext, fullSessionStream, pendingFrameWallClocks);
        }
        catch (Exception error)
        {
            // Must not throw unhandled off this thread - an unobserved exception
            // on a plain Thread (unlike Task) crashes the whole process.
            AppLog.Error("Native capture: encode thread failed.", error);
        }
    }

    private unsafe void DrainToRingBuffer(AVCodecContext* codecContext, AVPacket* packet, AVFormatContext* fullSessionFormatContext, AVStream* fullSessionStream, LinkedList<DateTime> pendingFrameWallClocks)
    {
        while (true)
        {
            var receiveResult = ffmpeg.avcodec_receive_packet(codecContext, packet);
            if (receiveResult == ffmpeg.AVERROR(ffmpeg.EAGAIN) || receiveResult == ffmpeg.AVERROR_EOF) break;
            if (receiveResult < 0) break;

            var isKeyframe = (packet->flags & ffmpeg.AV_PKT_FLAG_KEY) != 0;
            // Pooled, not freshly allocated. This runs once per encoded packet -
            // 60 times a second for as long as a buffer is armed - and the
            // arrays live long enough to be promoted before the ring trims them
            // away, which is the worst possible shape for the GC: a real session
            // walked the managed heap from 23MB to 281MB across 20 gen2
            // collections, and one of those collections shows up in the log as a
            // 714ms capture stall.
            var data = ArrayPool<byte>.Shared.Rent(packet->size);
            Marshal.Copy((IntPtr)packet->data, data, 0, packet->size);

            if (_extraData is null && codecContext->extradata_size > 0)
            {
                _extraData = new byte[codecContext->extradata_size];
                Marshal.Copy((IntPtr)codecContext->extradata, _extraData, 0, codecContext->extradata_size);
            }

            // Dequeues the real timestamp of whichever frame THIS packet
            // actually corresponds to (FIFO order, matching the encoder's own
            // in-order output guarantee with max_b_frames=0) - not just "now",
            // since the encoder can hold frames internally for a call or two
            // before releasing output, and packet->pts is now an IDEAL,
            // constant-rate timestamp (see the pacing gate in CaptureLoop) so
            // it can't be used to derive this the way it used to be.
            var realWallClockUtc = MonotonicClock.UtcNow;
            if (pendingFrameWallClocks.First is not null)
            {
                realWallClockUtc = pendingFrameWallClocks.First.Value;
                pendingFrameWallClocks.RemoveFirst();
            }

            lock (_bufferLock)
            {
                _packets.Add(new RingPacket(data, packet->size, packet->pts, isKeyframe, realWallClockUtc));
                _ringBufferBytes += packet->size;
            }

            if (fullSessionFormatContext is not null)
            {
                var clonedPacket = ffmpeg.av_packet_clone(packet);
                if (clonedPacket is not null)
                {
                    clonedPacket->stream_index = fullSessionStream->index;
                    ffmpeg.av_interleaved_write_frame(fullSessionFormatContext, clonedPacket);
                    var cp = clonedPacket;
                    ffmpeg.av_packet_free(&cp);
                }
            }

            ffmpeg.av_packet_unref(packet);
        }
    }

    // Writes to a temp path during the session (not the user's chosen folder directly) -
    // final output only gets the audio-muxed file once the session ends, via
    // FinalizeFullSessionRecording. The video itself is written incrementally as
    // packets arrive (no separate encode pass), same as the ring buffer.
    private static unsafe bool InitFullSessionWriter(ReplayBufferConfig config, AVCodecContext* codecContext, out AVFormatContext* resultFormatContext, out AVStream* resultStream, out string tempVideoPath, out string finalOutputPath)
    {
        resultFormatContext = null;
        resultStream = null;
        tempVideoPath = string.Empty;
        finalOutputPath = string.Empty;
        if (!config.FullSessionRecordingEnabled || string.IsNullOrWhiteSpace(config.FullSessionRecordingFolder)) return false;

        try
        {
            Directory.CreateDirectory(config.FullSessionRecordingFolder);
            var sessionLabel = string.IsNullOrWhiteSpace(config.GameDisplayName) ? "Session" : $"Session - {config.GameDisplayName}";
            finalOutputPath = ClipFileNaming.BuildUniquePath(config.FullSessionRecordingFolder, ClipFileNaming.BuildFileName(sessionLabel, DateTime.Now, "mp4", config.ClipFileNameScheme, config.CustomClipFileNameTemplate, config.GameDisplayName));
            tempVideoPath = Path.Combine(Path.GetTempPath(), $"clypdat-full-session-video-{Guid.NewGuid():N}.mp4");

            AVFormatContext* formatContext = null;
            ffmpeg.avformat_alloc_output_context2(&formatContext, null, "mp4", tempVideoPath);
            if (formatContext is null) return false;

            var stream = ffmpeg.avformat_new_stream(formatContext, null);
            if (ffmpeg.avcodec_parameters_from_context(stream->codecpar, codecContext) < 0)
            {
                ffmpeg.avformat_free_context(formatContext);
                return false;
            }
            stream->time_base = codecContext->time_base;
            // On the zero-copy path the encoder's pix_fmt is AV_PIX_FMT_D3D11 -
            // an opaque hardware handle format that describes how frames get IN,
            // not what the stream contains. Copying it verbatim into the muxed
            // stream would advertise something no reader can make sense of; the
            // pixels are, and always were, NV12.
            if (codecContext->pix_fmt == AVPixelFormat.AV_PIX_FMT_D3D11)
            {
                stream->codecpar->format = (int)AVPixelFormat.AV_PIX_FMT_NV12;
            }

            if ((formatContext->oformat->flags & ffmpeg.AVFMT_NOFILE) == 0)
            {
                AVIOContext* ioContext;
                if (ffmpeg.avio_open(&ioContext, tempVideoPath, ffmpeg.AVIO_FLAG_WRITE) < 0)
                {
                    ffmpeg.avformat_free_context(formatContext);
                    return false;
                }
                formatContext->pb = ioContext;
            }

            if (ffmpeg.avformat_write_header(formatContext, null) < 0)
            {
                ffmpeg.avformat_free_context(formatContext);
                return false;
            }

            AppLog.Info($"Native full session recording started: temp={tempVideoPath}, final={finalOutputPath}.");
            resultFormatContext = formatContext;
            resultStream = stream;
            return true;
        }
        catch (Exception error)
        {
            AppLog.Error("Full session recording init failed", error);
            return false;
        }
    }

    private static unsafe void FinalizeFullSessionWriter(AVFormatContext* formatContext)
    {
        if (formatContext is null) return;
        try
        {
            ffmpeg.av_write_trailer(formatContext);
        }
        catch (Exception error)
        {
            AppLog.Error("Full session recording finalize failed", error);
        }
        finally
        {
            if ((formatContext->oformat->flags & ffmpeg.AVFMT_NOFILE) == 0 && formatContext->pb is not null)
            {
                ffmpeg.avio_closep(&formatContext->pb);
            }

            ffmpeg.avformat_free_context(formatContext);
        }
    }

    // Runs once, after the temp video is fully written and closed - builds Game/Chat/
    // Microphone tracks for the whole session's wall-clock window (same AudioCapturePipeline
    // used for clip saves, already running the whole time regardless) and muxes them
    // against the temp video into the user's chosen folder. -c:v copy keeps this fast
    // even for a multi-hour session.
    // sessionStartUtc is on the MonotonicClock timeline (audio/pause alignment);
    // sessionStartWallUtc is the real wall-clock start, used only for the
    // sidecar's user-facing CreatedAt.
    private void FinalizeFullSessionRecording(ReplayBufferConfig config, DateTime sessionStartUtc, DateTime sessionStartWallUtc, string tempVideoPath, string finalOutputPath, string sessionGameDisplayName = "", AudioCapturePipeline.CaptureSetSnapshot? capturesOverride = null)
    {
        if (string.IsNullOrEmpty(tempVideoPath) || string.IsNullOrEmpty(finalOutputPath)) return;

        // The game the session was RECORDED from, not whatever detection says
        // at finalize time - the session usually ends precisely because the
        // game closed, so the fresh config here reads "No game detected" and
        // that's what the library tile showed. Start-time identity wins;
        // finalize-time only fills in if the session began before any game
        // was detected.
        var gameDisplayName = !string.IsNullOrWhiteSpace(sessionGameDisplayName) && !string.Equals(sessionGameDisplayName, "No game detected", StringComparison.OrdinalIgnoreCase)
            ? sessionGameDisplayName
            : config.GameDisplayName;

        var snapshots = new List<string>();
        try
        {
            var sessionEndUtc = MonotonicClock.UtcNow;
            var durationSeconds = Math.Max(1, (sessionEndUtc - sessionStartUtc).TotalSeconds);
            WritePausedRangesSidecar(config.LibraryFolder, finalOutputPath, ComputePausedRangesSeconds(GetOrderedPauseEvents(), sessionStartUtc, sessionEndUtc));
            // One giant segment spanning the whole session let audio/video clock
            // drift (real hardware sample clocks are never exactly 48000.000000Hz)
            // accumulate uncorrected for the entire recording - fine for the first
            // minute or two, audibly desynced well before a long session ends.
            // Regular replay clips never hit this because WindowsReplayBuffer
            // segments and independently re-anchors audio every ~60s; chunking the
            // session the same way here gets the same periodic resync instead of
            // one uncorrected multi-hour window.
            const double SegmentChunkSeconds = 60;
            var segmentWindows = new List<(DateTime StartUtc, double DurationSeconds)>();
            var chunkStartUtc = sessionStartUtc;
            var remainingSeconds = durationSeconds;
            while (remainingSeconds > 0)
            {
                var chunkSeconds = Math.Min(SegmentChunkSeconds, remainingSeconds);
                // See SaveReplayAsync - a runt tail segment costs an ffmpeg
                // process per track for a fraction of a second of audio.
                if (remainingSeconds - chunkSeconds < SegmentChunkSeconds / 2)
                {
                    chunkSeconds = remainingSeconds;
                }

                segmentWindows.Add((chunkStartUtc, chunkSeconds));
                chunkStartUtc += TimeSpan.FromSeconds(chunkSeconds);
                remainingSeconds -= chunkSeconds;
            }

            var tracks = _audio
                .BuildAlignedTracksAsync(segmentWindows, config, snapshots, CancellationToken.None, capturesOverride)
                .GetAwaiter().GetResult();

            // Background finalize already moved the video-only file onto the
            // final path so the session is visible immediately - ffmpeg can't
            // write its own input, so mux to a sibling temp and swap after.
            var muxInPlace = string.Equals(tempVideoPath, finalOutputPath, StringComparison.OrdinalIgnoreCase);
            var muxOutputPath = muxInPlace ? finalOutputPath + ".muxing.mp4" : finalOutputPath;

            List<string> BuildMuxArgs(string[] videoCodecArgs)
            {
                var muxArgs = new List<string> { "-y", "-i", tempVideoPath };
                foreach (var track in tracks) muxArgs.AddRange(new[] { "-i", track.Path });
                muxArgs.AddRange(new[] { "-map", "0:v" });
                for (var i = 0; i < tracks.Count; i++) muxArgs.AddRange(new[] { "-map", $"{i + 1}:a" });
                muxArgs.AddRange(videoCodecArgs);
                muxArgs.AddRange(new[] { "-c:a", "aac", "-b:a", "192k" });
                for (var i = 0; i < tracks.Count; i++) muxArgs.AddRange(new[] { $"-metadata:s:a:{i}", $"title={tracks[i].Label}" });
                // +faststart moves the moov index to the front of the file.
                // Costs one extra file rewrite at finalize, but without it
                // every later reader (LibVLC, ffmpeg chunk/waveform/thumbnail
                // extraction) must first seek to the END of a multi-GB file to
                // find the index - painless locally, a seek storm over a
                // network drive that made long sessions stutter/fail in the
                // editor while plain VLC (single reader, patient) coped.
                muxArgs.AddRange(new[] { "-movflags", "+faststart" });
                muxArgs.AddRange(new[] { "-metadata", $"comment={ClipMetadataTagger.BuildCommentValue("Native Full Session")}", muxOutputPath });
                return muxArgs;
            }

            // The ring encoder already produced H.264, so H.264 here is a
            // plain stream copy (fast). H.265/AV1 re-encode the whole session
            // through NVENC at finalize time for a much smaller file, falling
            // back to a stream copy (not a CPU encode - a multi-hour software
            // re-encode at finalize would be far worse than a bigger file) if
            // NVENC isn't available.
            var codecArgs = config.FullSessionVideoCodec switch
            {
                "H.265" => new[] { "-c:v", "hevc_nvenc", "-preset", "p5", "-rc", "vbr", "-cq", "24", "-b:v", "0" },
                "AV1" => new[] { "-c:v", "av1_nvenc", "-preset", "p5", "-rc", "vbr", "-cq", "32", "-b:v", "0" },
                _ => new[] { "-c:v", "copy" }
            };
            var result = AudioCapturePipeline.RunProcessAsync("ffmpeg", BuildMuxArgs(codecArgs), CancellationToken.None).GetAwaiter().GetResult();
            if (result.ExitCode != 0 && codecArgs[1] != "copy")
            {
                AppLog.Error($"Full session {config.FullSessionVideoCodec} re-encode failed, retrying as stream copy: {result.Error}");
                result = AudioCapturePipeline.RunProcessAsync("ffmpeg", BuildMuxArgs(new[] { "-c:v", "copy" }), CancellationToken.None).GetAwaiter().GetResult();
            }
            if (result.ExitCode != 0)
            {
                AppLog.Error($"Full session recording final mux failed: {result.Error}{(muxInPlace ? " (video-only session file kept)" : string.Empty)}");
                if (muxInPlace) AudioCapturePipeline.TryDelete(muxOutputPath);
            }
            else
            {
                if (muxInPlace)
                {
                    File.Move(muxOutputPath, finalOutputPath, overwrite: true);
                }
                ClipInfoSidecar.Save(config.LibraryFolder, finalOutputPath, new ClipInfo(gameDisplayName, null, $"Session - {gameDisplayName}", sessionStartWallUtc, CaptureSource: config.CaptureSource));
                AppLog.Info($"Native full session recording saved: path={finalOutputPath}, codec={config.FullSessionVideoCodec}.");
                EnforceFullSessionQuota(config);
            }
        }
        catch (Exception error)
        {
            AppLog.Error("Full session recording finalize/mux failed", error);
        }
        finally
        {
            // In-place mode the "temp" IS the final file - never delete it.
            if (!string.Equals(tempVideoPath, finalOutputPath, StringComparison.OrdinalIgnoreCase))
            {
                AudioCapturePipeline.TryDelete(tempVideoPath);
            }
            foreach (var snapshot in snapshots) AudioCapturePipeline.TryDelete(snapshot);
        }
    }

    // Deletes the oldest ClypDat-recorded session files (identified by their own
    // sidecar's "... Full Session" FileTitle - never touches clips or files
    // ClypDat didn't write) until the library's VODs tree fits the configured
    // quota again. Runs after each successful session save; the just-saved
    // file is always kept even if it alone exceeds the quota.
    private static void EnforceFullSessionQuota(ReplayBufferConfig config)
    {
        if (config.FullSessionQuotaGb <= 0 || string.IsNullOrWhiteSpace(config.LibraryFolder)) return;
        try
        {
            var vodsRoot = LibraryLayout.VodsRoot(config.LibraryFolder);
            if (!Directory.Exists(vodsRoot)) return;

            var sessions = Directory.EnumerateFiles(vodsRoot, "*.*", SearchOption.AllDirectories)
                .Where(MediaProbeService.IsVideoFile)
                .Where(path =>
                {
                    // New sessions title as "Session - {game}"; pre-existing
                    // ones as "{game} Full Session" - quota must keep seeing both.
                    var title = ClipInfoSidecar.Load(config.LibraryFolder, path)?.FileTitle;
                    return title is not null &&
                           (title.StartsWith("Session - ", StringComparison.OrdinalIgnoreCase) ||
                            title.EndsWith("Full Session", StringComparison.OrdinalIgnoreCase));
                })
                .Select(path => new FileInfo(path))
                .OrderBy(info => info.CreationTimeUtc)
                .ToList();

            var quotaBytes = (long)config.FullSessionQuotaGb * 1024 * 1024 * 1024;
            var totalBytes = sessions.Sum(info => info.Length);
            // Index-bounded to Count-1 so the newest session always survives.
            for (var i = 0; totalBytes > quotaBytes && i < sessions.Count - 1; i++)
            {
                var victim = sessions[i];
                try
                {
                    File.Delete(victim.FullName);
                    ClipInfoSidecar.Delete(config.LibraryFolder, victim.FullName);
                    ClipEditSidecar.Delete(config.LibraryFolder, victim.FullName);
                    AudioCapturePipeline.TryDelete(LibraryLayout.SidecarPath(config.LibraryFolder, victim.FullName, ".paused.json"));
                    totalBytes -= victim.Length;
                    AppLog.Info($"Full session quota: deleted oldest session {victim.Name} ({victim.Length / (1024.0 * 1024 * 1024):0.0}GB) to fit {config.FullSessionQuotaGb}GB.");
                }
                catch (Exception error)
                {
                    AppLog.Error($"Full session quota: failed deleting {victim.FullName}", error);
                }
            }
        }
        catch (Exception error)
        {
            AppLog.Error("Full session quota enforcement failed", error);
        }
    }

    // Hands [index, index + count) back to the pool. Callers must hold
    // _bufferLock and must remove the entries themselves - this only releases
    // the payloads and keeps _ringBufferBytes in step.
    //
    // Safe to recycle unconditionally because nothing outside the ring ever
    // holds one of these arrays: SaveReplayAsync takes its own copy of the
    // bytes before it lets go of the lock (see CopyWindowUnderLock).
    private void ReturnPooledPackets(int index, int count)
    {
        for (var i = index; i < index + count; i++)
        {
            var packet = _packets[i];
            _ringBufferBytes -= packet.Length;
            ArrayPool<byte>.Shared.Return(packet.Data);
        }
    }

    // Selects the save window and copies its bytes out of the ring, both under
    // the buffer lock.
    //
    // The copy is what lets the ring pool its payloads at all. TrimRingBuffer
    // keeps running once a second for the entire multi-second life of a save,
    // so handing the remux references into the ring would let it read arrays
    // that had already been recycled into newer packets - garbage in the middle
    // of the clip, and only under load. One transient copy per save (a
    // user-initiated, already-expensive operation) buys back 60 allocations a
    // second in steady state.
    //
    // Selection runs inside the lock rather than off a lock-free snapshot
    // because the copy has to be in there anyway. These are plain index scans
    // over value fields, not the LINQ pipeline that made holding this lock
    // expensive before.
    private RingPacket[] CopyWindowUnderLock(DateTime requestedStartUtc, DateTime requestedEndUtc)
    {
        lock (_bufferLock)
        {
            if (_packets.Count == 0) throw new InvalidOperationException("Replay just started. Try again in a second.");

            // Saving from a keyframe immediately before the requested event
            // keeps the remux playable while still producing an event-sized clip.
            var startIndex = -1;
            for (var i = _packets.Count - 1; i >= 0; i--)
            {
                if (_packets[i].WallClockUtc <= requestedStartUtc && _packets[i].IsKeyframe) { startIndex = i; break; }
            }
            if (startIndex < 0) startIndex = _packets.FindIndex(packet => packet.IsKeyframe);
            if (startIndex < 0) throw new InvalidOperationException("Replay just started. Try again in a second.");

            var endIndex = -1;
            for (var i = _packets.Count - 1; i >= 0; i--)
            {
                if (_packets[i].WallClockUtc <= requestedEndUtc) { endIndex = i; break; }
            }
            if (endIndex < startIndex) throw new InvalidOperationException("The requested replay window is no longer available.");

            var window = new RingPacket[endIndex - startIndex + 1];
            for (var i = 0; i < window.Length; i++)
            {
                var source = _packets[startIndex + i];
                // Exact-sized, so the copy's Data.Length and Length agree and
                // it is never handed back to the pool by mistake.
                var data = new byte[source.Length];
                Array.Copy(source.Data, data, source.Length);
                window[i] = source with { Data = data };
            }

            return window;
        }
    }

    private void TrimRingBuffer(DateTime? fullSessionStartUtc)
    {
        var cutoff = MonotonicClock.UtcNow - Duration - TimeSpan.FromSeconds(5);
        lock (_bufferLock)
        {
            var removeCount = 0;
            while (removeCount < _packets.Count && _packets[removeCount].WallClockUtc < cutoff) removeCount++;
            if (removeCount > 0)
            {
                ReturnPooledPackets(0, removeCount);
                _packets.RemoveRange(0, removeCount);
            }

            // _pauseEvents is shared between ring-buffer clip saves (which only
            // ever need the last Duration worth of history) and a running Full
            // Session recording, which can span hours - trimming to the same
            // Duration-based cutoff used for _packets silently dropped any pause
            // event older than that, so a session-start alt-tab was gone from
            // the sidecar by the time a multi-hour session finished. While a
            // Full Session is active, nothing older than its own start is
            // eligible for trimming.
            var pauseEventCutoff = fullSessionStartUtc is { } sessionStart && sessionStart < cutoff
                ? sessionStart
                : cutoff;

            // Keeps at most one event before the cutoff (needed so
            // ComputePausedRangesSeconds can still tell what state a save
            // window started in) and drops everything older than that.
            var keepFromIndex = 0;
            for (var i = _pauseEvents.Count - 1; i >= 0; i--)
            {
                if (_pauseEvents[i].WallClockUtc < pauseEventCutoff) { keepFromIndex = i; break; }
            }
            if (keepFromIndex > 0) _pauseEvents.RemoveRange(0, keepFromIndex);
        }
    }

    private PauseEvent[] GetOrderedPauseEvents()
    {
        lock (_bufferLock) return _pauseEvents.OrderBy(e => e.WallClockUtc).ToArray();
    }

    // Reconstructs the paused/frozen (game window not foreground during DXGI
    // Desktop Duplication capture - see class summary) time ranges that fall
    // within [windowStartUtc, windowEndUtc), as offsets in seconds from the
    // window's start, for the "Recording Paused" editor overlay to read.
    private static List<(double StartSeconds, double EndSeconds)> ComputePausedRangesSeconds(
        PauseEvent[] orderedEvents, DateTime windowStartUtc, DateTime windowEndUtc)
    {
        var currentlyPaused = false;
        foreach (var e in orderedEvents)
        {
            if (e.WallClockUtc > windowStartUtc) break;
            currentlyPaused = e.IsPaused;
        }

        var ranges = new List<(double, double)>();
        var pauseStartUtc = currentlyPaused ? windowStartUtc : (DateTime?)null;

        foreach (var e in orderedEvents)
        {
            if (e.WallClockUtc <= windowStartUtc || e.WallClockUtc >= windowEndUtc) continue;
            if (e.IsPaused == currentlyPaused) continue;

            if (e.IsPaused)
            {
                pauseStartUtc = e.WallClockUtc;
            }
            else if (pauseStartUtc is not null)
            {
                ranges.Add((
                    Math.Max(0, (pauseStartUtc.Value - windowStartUtc).TotalSeconds),
                    (e.WallClockUtc - windowStartUtc).TotalSeconds));
                pauseStartUtc = null;
            }

            currentlyPaused = e.IsPaused;
        }

        if (currentlyPaused && pauseStartUtc is not null)
        {
            ranges.Add((
                Math.Max(0, (pauseStartUtc.Value - windowStartUtc).TotalSeconds),
                (windowEndUtc - windowStartUtc).TotalSeconds));
        }

        return ranges;
    }

    private static void WritePausedRangesSidecar(string libraryRoot, string outputPath, List<(double StartSeconds, double EndSeconds)> ranges)
    {
        if (ranges.Count == 0) return;
        try
        {
            var payload = ranges.Select(r => new { start = Math.Round(r.StartSeconds, 2), end = Math.Round(r.EndSeconds, 2) }).ToArray();
            var sidecarPath = LibraryLayout.SidecarPath(libraryRoot, outputPath, ".paused.json");
            Directory.CreateDirectory(Path.GetDirectoryName(sidecarPath)!);
            File.WriteAllText(sidecarPath, JsonSerializer.Serialize(payload));
        }
        catch (Exception error)
        {
            AppLog.Error("Failed to write recording-paused sidecar.", error);
        }
    }

    private unsafe void RemuxWindowToMp4(RingPacket[] window, string outputPath)
    {
        AVFormatContext* formatContext = null;
        try
        {
            ffmpeg.avformat_alloc_output_context2(&formatContext, null, "mp4", outputPath);
            if (formatContext is null) throw new InvalidOperationException("avformat_alloc_output_context2 failed.");

            var stream = ffmpeg.avformat_new_stream(formatContext, null);
            stream->time_base = _timeBase;
            stream->codecpar->codec_type = AVMediaType.AVMEDIA_TYPE_VIDEO;
            stream->codecpar->codec_id = AVCodecID.AV_CODEC_ID_H264;
            stream->codecpar->width = _outputWidth;
            stream->codecpar->height = _outputHeight;

            if (_extraData is { Length: > 0 })
            {
                var extraDataPtr = (byte*)ffmpeg.av_mallocz((ulong)_extraData.Length);
                Marshal.Copy(_extraData, 0, (IntPtr)extraDataPtr, _extraData.Length);
                stream->codecpar->extradata = extraDataPtr;
                stream->codecpar->extradata_size = _extraData.Length;
            }

            if ((formatContext->oformat->flags & ffmpeg.AVFMT_NOFILE) == 0)
            {
                AVIOContext* ioContext;
                var openResult = ffmpeg.avio_open(&ioContext, outputPath, ffmpeg.AVIO_FLAG_WRITE);
                if (openResult < 0) throw new InvalidOperationException($"avio_open failed ({openResult}).");
                formatContext->pb = ioContext;
            }

            var headerResult = ffmpeg.avformat_write_header(formatContext, null);
            if (headerResult < 0) throw new InvalidOperationException($"avformat_write_header failed ({headerResult}).");

            var basePts = window[0].PtsMs;
            // Only ever read for the final packet, which has no successor to
            // measure against; every other one overwrites it first.
            long lastPacketDuration = 0;
            var packet = ffmpeg.av_packet_alloc();
            try
            {
                for (var i = 0; i < window.Length; i++)
                {
                    var ringPacket = window[i];
                    // Length, not Data.Length - see RingPacket. These particular
                    // packets came from CopyWindowUnderLock and so are already
                    // exact-sized, but reading the field keeps this correct if
                    // a pooled packet ever reaches here.
                    ffmpeg.av_new_packet(packet, ringPacket.Length);
                    Marshal.Copy(ringPacket.Data, 0, (IntPtr)packet->data, ringPacket.Length);
                    packet->pts = packet->dts = ringPacket.PtsMs - basePts;
                    // Explicit, because the timeline is no longer a uniform grid:
                    // the pacing gate suppresses duplicate frames, so consecutive
                    // packets can be more than one frame interval apart. The muxer
                    // infers each sample's duration from the NEXT packet's DTS,
                    // which leaves the final sample of the window with none at all -
                    // invisible at a constant frame rate, not invisible now. The
                    // last packet reuses the previous gap for want of a successor.
                    packet->duration = i + 1 < window.Length
                        ? window[i + 1].PtsMs - ringPacket.PtsMs
                        : lastPacketDuration;
                    lastPacketDuration = packet->duration;
                    packet->stream_index = stream->index;
                    if (ringPacket.IsKeyframe) packet->flags |= ffmpeg.AV_PKT_FLAG_KEY;
                    ffmpeg.av_interleaved_write_frame(formatContext, packet);
                    ffmpeg.av_packet_unref(packet);
                }
            }
            finally
            {
                ffmpeg.av_packet_free(&packet);
            }

            ffmpeg.av_write_trailer(formatContext);
        }
        finally
        {
            if (formatContext is not null)
            {
                if ((formatContext->oformat->flags & ffmpeg.AVFMT_NOFILE) == 0 && formatContext->pb is not null)
                {
                    ffmpeg.avio_closep(&formatContext->pb);
                }
                ffmpeg.avformat_free_context(formatContext);
            }
        }
    }

    // Tries hardware encoders in order, falling back to the next if the named encoder
    // either isn't present in this ffmpeg build or fails to open (no matching GPU/driver
    // present - e.g. h264_nvenc exists in the binary on any machine, but avcodec_open2
    // only succeeds if an actual NVIDIA GPU/driver answers it). h264_amf is AMD's
    // equivalent via the AMF SDK, h264_qsv is Intel's via Quick Sync; libx264 is the
    // last-resort CPU fallback so capture still works even with no usable hardware
    // encoder at all.
    private static readonly string[] EncoderCandidates = { "h264_nvenc", "h264_amf", "h264_qsv", "libx264" };

    // h264_nvenc's default preset does real per-frame rate-distortion search,
    // which measured a sustained ~59-60ms/frame (vs. ~0.5ms on p1) during
    // actual Dead by Daylight matches specifically, where the same GPU is also
    // under its heaviest rendering load of the whole session - that 100x
    // per-frame cost, back when encode still ran inline on CaptureLoop's own
    // thread, turned "GPU is busy" into sustained near-1fps capture for
    // minutes at a time. p1 was the fix at the time, at the cost of visible
    // motion-compression artifacts under fast camera movement (looks like
    // dropped frames even though every frame is present and correctly timed -
    // confirmed via ffprobe on an actual saved clip: exact expected frame
    // count, zero duplicates, dead-even PTS spacing). Now that encode runs on
    // its own thread (see EncodeLoop) decoupled from AcquireNextFrame, a
    // slower preset can no longer stall AcquireNextFrame itself - but it can
    // still cost real content: p4 measured avgEncodeMs of 16-28ms/frame under
    // real sustained gameplay load (vs. a target budget well under that),
    // which filled EncodeLoop's queue to its cap and started genuinely
    // dropping frames (confirmed via droppedFrames actually incrementing in
    // Native capture diag, e.g. 110 dropped in one 2s window) - real missing
    // content, not just a compression-quality artifact. p2 is the compromise
    // between p1's motion-compression softness and p4's encode cost. Watch
    // queueDepth/droppedFrames after any future preset change - that pair is
    // the actual signal for whether a preset is sustainable under real load,
    // not just a quick idle-desktop test. Applied to priv_data before
    // avcodec_open2 - these are encoder-specific options, not real
    // AVCodecContext fields, so they have to land before open, not after.
    // Best-effort: an unsupported option name just logs and moves on instead
    // of failing the whole encoder open, since exact option support varies by
    // ffmpeg build/driver version.
    // The encoder settings come straight from user input now, so they're
    // clamped here rather than trusted. Bitrate doubles as the ring buffer's
    // memory bound: the buffer lives entirely in RAM, so a 60s 1080p60 buffer
    // costs roughly 125MB at 16Mbps and scales linearly from there. Only
    // Constant bitrate mode takes a user bitrate value now - Constant quality's
    // ceiling is always derived (see MaxBitrate below).
    private static bool IsConstantBitrate(ReplayBufferConfig config) =>
        string.Equals(config.RateControlMode, "Constant bitrate", StringComparison.OrdinalIgnoreCase);

    // 1-51 is the H.264 quantiser's own range, so this is the full span
    // the encoders actually accept rather than an arbitrary narrowing.
    // 0 is excluded deliberately: NVENC reads cq=0 as "auto", which would
    // silently turn constant quality OFF rather than mean "best possible".
    private static int ConstantQualityTarget(ReplayBufferConfig config) => Math.Clamp(config.ConstantQuality, 1, 51);

    // Constant bitrate: the user's field is the ceiling by definition (it IS
    // the target). Constant quality has no user-facing ceiling input anymore -
    // derive a generous one from the same resolution/fps estimate CaptureBitrate
    // uses, so a burst can still borrow bits without an unbounded ring buffer.
    private static long MaxBitrate(ReplayBufferConfig config) =>
        IsConstantBitrate(config)
            ? Math.Clamp(config.MaxBitrateMbps, 5, 1000) * 1_000_000L
            : Math.Clamp(CaptureBitrate(config) * 2L, 8_000_000L, 160_000_000L);

    // "P3" -> "p3". Anything unrecognised falls back to the default rather than
    // being passed through to av_opt_set as-is.
    private static string NvencPreset(ReplayBufferConfig config) => config.EncoderPreset?.ToLowerInvariant() switch
    {
        "p1" or "p2" or "p3" or "p4" or "p5" => config.EncoderPreset!.ToLowerInvariant(),
        _ => "p4"
    };

    private static unsafe void ApplyLowLatencyEncoderOptions(AVCodecContext* codecContext, string candidateName, ReplayBufferConfig config, bool lowPower = false)
    {
        void TrySet(string name, string value)
        {
            var result = ffmpeg.av_opt_set(codecContext->priv_data, name, value, 0);
            if (result < 0)
            {
                AppLog.Info($"Native encoder probe: {candidateName} option {name}={value} not supported (error {result}), skipping.");
            }
        }

        // Every saved clip is cut out of the ring buffer starting at a packet
        // flagged AV_PKT_FLAG_KEY. That flag means "I-frame", which is NOT the
        // same as IDR: without forced-idr, NVENC emits plain I-frames at GOP
        // boundaries, and frames after one may still reference frames before
        // it. A decoder starting cold at that cut therefore has references it
        // never saw. Software decoding conceals the damage, but a hardware
        // decoder shows it as blocky macroblock corruption - which is exactly
        // the symptom that forced editor playback onto software decode
        // (avcodec-hw=none in PlaybackSession) in the first place, and why the
        // same clips looked fine in standalone VLC only after it fell back to
        // software too. Making every keyframe a true IDR means any cut point
        // is a self-contained stream start.
        switch (candidateName)
        {
            case "h264_nvenc":
                // User-selectable now (Settings -> Encoder), defaulting to p4.
                // p1 was the old hardcoded value, chosen back when encode ran
                // inline on the capture thread and gave GPU rendering priority
                // during VSync-off/VRR bursts; encode has since moved to its own
                // thread (see EncodeLoop) and real logs measure it at ~0.5ms
                // against a 16.67ms frame budget, so there is margin to spend on
                // motion quality. The historical warning in the comment above
                // still applies at the top of the range: watch
                // droppedFrames/queueDepth under sustained heavy GPU load.
                TrySet("preset", NvencPreset(config));
                // Main is NVENC's default profile and disables the 8x8
                // transform - a free efficiency loss on precisely the detailed
                // content that was coming out soft.
                TrySet("profile", "high");
                // Spend bits by local complexity rather than uniformly, so flat
                // regions stop stealing budget from detailed ones.
                TrySet("spatial-aq", "1");
                // "hq", NOT "ll". Low-latency tuning pins NVENC's VBV buffer to
                // roughly a single frame's worth of bits, which hard-caps EVERY
                // frame at bitrate/fps (~34KB at 1080p60) no matter how much
                // motion it actually contains - a fast-panning gameplay frame
                // needs several times that, so it gets crushed to fit and comes
                // out soft and blocky while the file's AVERAGE bitrate still
                // reads a healthy ~17Mbps. That constraint buys nothing here:
                // this is a ring buffer being written to memory, not a live
                // stream with a latency budget. "hq" lets the encoder spend bits
                // where the content actually needs them, at the same average
                // bitrate and so the same file size and ring-buffer footprint.
                TrySet("tune", "hq");
                // Constant quality: cq drives the actual bit spend and
                // bit_rate/rc_max_rate (set on the context) are only an average
                // target and a derived hard ceiling. Constant bitrate: the rate
                // itself is the constraint, so no cq at all.
                if (IsConstantBitrate(config))
                {
                    TrySet("rc", "cbr");
                }
                else
                {
                    TrySet("rc", "vbr");
                    TrySet("cq", ConstantQualityTarget(config).ToString());
                }
                TrySet("forced-idr", "1");
                break;
            // AMF/QSV keep their existing usage/preset strings: there's no AMD
            // or Intel hardware here to confirm a change doesn't cost frames,
            // and these paths are untested. They still pick up the context-level
            // improvements (profile, colour tagging, VBV, rc_max_rate) for free.
            case "h264_amf":
                TrySet("usage", "ultralowlatency");
                TrySet("quality", "speed");
                // AMF's equivalent knob is spelled with an underscore, not a
                // dash like NVENC's - this was silently a no-op under the
                // wrong name (TrySet just logs and moves on for an unknown
                // option), so AMD captures weren't actually getting true IDR
                // cut points despite the setting looking present here.
                TrySet("forced_idr", "1");
                break;
            case "h264_qsv":
                TrySet("preset", "veryfast");
                TrySet("forced_idr", "1");
                // Back to QSV's own default of 4. This was pinned to 1 out of
                // the same latency concern as NVENC's "ll" tune above - and
                // that reasoning was already reversed there, on the grounds
                // that this is a ring buffer being written to memory, not a
                // live stream with a latency budget. The identical argument
                // applies: depth 4 means up to ~66ms of frames sit inside the
                // encoder before their packets reach the ring, which against a
                // 60-second buffer is noise. What depth 1 actually bought was a
                // fully serialised encoder - avcodec_send_frame blocking on the
                // previous frame's completion, measured at avgEncodeMs 8.6-10.2
                // at 1080p60 on an Iris Xe laptop.
                TrySet("async_depth", "4");
                // Routes encoding to the fixed-function VDENC block instead of
                // the EU-based path. On an integrated Intel GPU the EU path
                // competes with the game for the very same execution units,
                // which is the difference between "capture costs some GPU" and
                // "the laptop chugs and is hard to control". Not supported in
                // every preset/rate-control/driver combination, and it fails at
                // avcodec_open2 rather than here - see CreateEncoder, which
                // retries QSV without it.
                if (lowPower) TrySet("low_power", "1");
                break;
            case "libx264":
                TrySet("preset", "ultrafast");
                TrySet("tune", "zerolatency");
                // x264's own constant-quality knob, so the CPU fallback tracks
                // the same user setting the hardware encoders do. In
                // constant-bitrate mode it's left off and bit_rate governs.
                if (!IsConstantBitrate(config)) TrySet("crf", ConstantQualityTarget(config).ToString());
                break;
        }
    }

    // Bind flags to try for the encoder's D3D11 frame pool, in order. 0 leaves
    // ffmpeg's own default (D3D11_BIND_DECODER), which is what its decoder ->
    // NVENC chain uses and so the best-supported combination; the others are
    // there only for a driver that refuses it.
    //
    // Not a free choice: the pool is one NV12 texture ARRAY, and a bind flag
    // the driver won't accept for an array of that format fails the whole
    // CreateTexture2D. D3D11_BIND_RENDER_TARGET (0x20) was tried first here and
    // failed exactly that way on an RTX 4070 Ti - "D3D11 encode frame pool init
    // failed (error -1313558101)", AVERROR_UNKNOWN, which is what
    // hwcontext_d3d11va reports when the texture cannot be created.
    private static readonly uint[] EncodeFramePoolBindFlags = { 0, 0x20, 0x8 };

    // Pool slots beyond the encode queue's own capacity: one for the frame
    // being filled, one held back for padding ticks, and two of slack so a
    // frame the encoder has finished with but not yet unreferenced can't
    // starve the next capture tick.
    private const int HardwareFramePoolHeadroom = 4;

    private static unsafe void ReleaseHardwareFrames(ref nint deviceRef, ref nint framesRef)
    {
        if (framesRef != 0)
        {
            var frames = (AVBufferRef*)framesRef;
            ffmpeg.av_buffer_unref(&frames);
            framesRef = 0;
        }

        if (deviceRef != 0)
        {
            var device = (AVBufferRef*)deviceRef;
            ffmpeg.av_buffer_unref(&device);
            deviceRef = 0;
        }
    }

    // The encoder's own NV12 frame pool, living on the SAME ID3D11Device the
    // capture and the Video Processor already use, so a scaled frame reaches
    // NVENC without ever leaving the GPU.
    //
    // What this replaces: the scaled NV12 surface was copied to a staging
    // texture, mapped, memcpy'd plane by plane into a system-memory AVFrame,
    // and then handed to ffmpeg - which uploaded those same pixels straight
    // back to the GPU for NVENC. 5.5MB down and 5.5MB back up per frame at
    // 1440p60, on the same GPU a game already owns. Measured cost of that
    // round trip under real load: avgEncodeMs of 65-110ms per frame against a
    // 16.7ms budget (0.9-2.6ms with the GPU idle), which pinned the encode
    // queue at capacity and made the pacing loop skip pad ticks - a clip
    // configured for 60fps came out at 53.5.
    //
    // Best-effort by design: every failure path here returns (0, 0) and the
    // caller stays on the system-memory path, which still works exactly as it
    // did.
    private static unsafe (nint DeviceRef, nint FramesRef) TryCreateD3D11EncodeFrames(
        ID3D11Device device, int width, int height, int poolSize)
    {
        // Pool shape matters as much as the flags. A non-zero initial size makes
        // ffmpeg allocate the whole pool as ONE NV12 texture array, and an RTX
        // 4070 Ti refuses to create that array at any of the bind flags above -
        // CreateTexture2D fails and the init reports AVERROR_UNKNOWN. Size 0
        // switches ffmpeg to allocating a separate single texture per frame,
        // recycled through its own buffer pool, which is the shape the driver
        // does accept. Frames in flight are still bounded by the encode queue,
        // so a dynamic pool is not unbounded VRAM.
        foreach (var size in new[] { poolSize, 0 })
        {
            foreach (var bindFlags in EncodeFramePoolBindFlags)
            {
                var attempt = TryCreateD3D11EncodeFrames(device, width, height, size, bindFlags);
                if (attempt.FramesRef != 0)
                {
                    AppLog.Info($"Native capture: D3D11 encode frame pool ready (poolSize={size}, bindFlags=0x{bindFlags:X}).");
                    return attempt;
                }
            }
        }

        return (0, 0);
    }

    private static unsafe (nint DeviceRef, nint FramesRef) TryCreateD3D11EncodeFrames(
        ID3D11Device device, int width, int height, int poolSize, uint bindFlags)
    {
        AVBufferRef* deviceRef = null;
        AVBufferRef* framesRef = null;
        try
        {
            deviceRef = ffmpeg.av_hwdevice_ctx_alloc(AVHWDeviceType.AV_HWDEVICE_TYPE_D3D11VA);
            if (deviceRef is null) return (0, 0);

            var deviceContext = (AVHWDeviceContext*)deviceRef->data;
            var d3dDevice = (AVD3D11VADeviceContext*)deviceContext->hwctx;
            // ffmpeg takes ownership of one reference and releases it when the
            // context is freed, so this AddRef is the reference it consumes -
            // without it, freeing the hw device would tear down a device the
            // capture loop is still using.
            device.AddRef();
            d3dDevice->device = (FFmpeg.AutoGen.ID3D11Device*)device.NativePointer;
            // device_context/video_device are deliberately left null: ffmpeg's
            // own init fills them in from the device (and marks it
            // multithread-protected, which the encode thread touching it from
            // off the capture thread requires).
            var deviceInit = ffmpeg.av_hwdevice_ctx_init(deviceRef);
            if (deviceInit < 0)
            {
                AppLog.Info($"Native capture: D3D11 encode device init failed (error {deviceInit}), using system-memory frames.");
                ffmpeg.av_buffer_unref(&deviceRef);
                return (0, 0);
            }

            framesRef = ffmpeg.av_hwframe_ctx_alloc(deviceRef);
            if (framesRef is null)
            {
                ffmpeg.av_buffer_unref(&deviceRef);
                return (0, 0);
            }

            var framesContext = (AVHWFramesContext*)framesRef->data;
            framesContext->format = AVPixelFormat.AV_PIX_FMT_D3D11;
            framesContext->sw_format = AVPixelFormat.AV_PIX_FMT_NV12;
            framesContext->width = width;
            framesContext->height = height;
            // A fixed pool, sized just past the encode queue: every frame in
            // flight holds one slot, plus the one held back for padding ticks.
            // Each slot is a real NV12 surface in VRAM (5.5MB at 1440p), so
            // this is not free - a pool the size of the old 30-deep queue would
            // be ~200MB taken away from the game. Exhaustion is handled the
            // same way a full queue is: drop the frame.
            framesContext->initial_pool_size = poolSize;
            var d3dFrames = (AVD3D11VAFramesContext*)framesContext->hwctx;
            if (bindFlags != 0) d3dFrames->BindFlags = bindFlags;

            var framesInit = ffmpeg.av_hwframe_ctx_init(framesRef);
            if (framesInit < 0)
            {
                AppLog.Info($"Native capture: D3D11 encode frame pool init failed (error {framesInit}, bindFlags=0x{bindFlags:X}).");
                ffmpeg.av_buffer_unref(&framesRef);
                ffmpeg.av_buffer_unref(&deviceRef);
                return (0, 0);
            }

            return ((nint)deviceRef, (nint)framesRef);
        }
        catch (Exception error)
        {
            AppLog.Error("Native capture: D3D11 encode frame setup failed, using system-memory frames.", error);
            if (framesRef is not null) ffmpeg.av_buffer_unref(&framesRef);
            if (deviceRef is not null) ffmpeg.av_buffer_unref(&deviceRef);
            return (0, 0);
        }
    }

    // avcodec_open2 succeeding is not proof the encoder can actually take these
    // textures - registering a D3D11 resource with NVENC happens on the first
    // send_frame, and that is where a driver/bind-flag mismatch surfaces. A
    // throwaway frame through a throwaway context is what turns "this build and
    // driver really will encode from VRAM" into something known before the
    // session starts, rather than a capture that opens fine and then encodes
    // nothing.
    private static unsafe bool ProbeHardwareEncode(AVCodecContext* codecContext, AVBufferRef* framesRef)
    {
        var probeFrame = ffmpeg.av_frame_alloc();
        AVPacket* probePacket = null;
        try
        {
            if (probeFrame is null) return false;
            if (ffmpeg.av_hwframe_get_buffer(framesRef, probeFrame, 0) < 0) return false;

            probeFrame->pts = 0;
            if (ffmpeg.avcodec_send_frame(codecContext, probeFrame) < 0) return false;

            probePacket = ffmpeg.av_packet_alloc();
            if (probePacket is null) return false;
            while (ffmpeg.avcodec_receive_packet(codecContext, probePacket) >= 0)
            {
                ffmpeg.av_packet_unref(probePacket);
            }

            return true;
        }
        catch
        {
            return false;
        }
        finally
        {
            if (probePacket is not null) ffmpeg.av_packet_free(&probePacket);
            if (probeFrame is not null) ffmpeg.av_frame_free(&probeFrame);
        }
    }

    private static unsafe AVCodecContext* CreateEncoder(ReplayBufferConfig config, int width, int height, out AVRational timeBase, out string encoderName)
        => CreateEncoder(config, width, height, 0, out timeBase, out encoderName, out _);

    private static unsafe AVCodecContext* CreateEncoder(
        ReplayBufferConfig config,
        int width,
        int height,
        nint hardwareFramesRef,
        out AVRational timeBase,
        out string encoderName,
        out bool usingHardwareFrames)
    {
        usingHardwareFrames = false;
        // Local copy because a local function cannot capture an out parameter.
        var encoderTimeBase = new AVRational { num = 1, den = 1_000_000 };
        timeBase = encoderTimeBase;
        encoderName = string.Empty;

        // One full alloc/configure/open attempt. Returns null if the open
        // fails - which is the only way some options can report unsupported
        // (low_power in particular is accepted by av_opt_set and rejected by
        // avcodec_open2), and a context whose open failed is unusable, so
        // every attempt has to start from a fresh allocation rather than
        // re-opening this one.
        AVCodecContext* TryOpen(AVCodec* candidateCodec, string candidateName, bool lowPower, bool hardwareFrames)
        {
            var codecContext = ffmpeg.avcodec_alloc_context3(candidateCodec);
            codecContext->width = width;
            codecContext->height = height;
            codecContext->time_base = encoderTimeBase;
            codecContext->framerate = new AVRational { num = Math.Clamp(config.FrameRate, 15, 240), den = 1 };
            codecContext->pix_fmt = AVPixelFormat.AV_PIX_FMT_NV12;
            // Limited/studio range (16-235), matching what the scalers
            // (CreateScaler/CreateGpuScaler) now write, and tagged as such.
            //
            // This was AVCOL_RANGE_JPEG with full-range pixels to match, which
            // is internally consistent and correct by the spec - and still came
            // out visibly dark everywhere outside this app. Consumers built on
            // Media Foundation (Explorer's thumbnails and preview, Photos,
            // Films & TV) ignore H.264's video_full_range_flag, assume limited,
            // and expand 16-235 -> 0-255 on content that already occupies the
            // full range: blacks clip to 0 and the whole picture crushes down.
            // LibVLC, and so the editor's own playback, makes the same
            // assumption. Emitting limited range makes the flag-ignorers right
            // by construction, and the flag-honourers (ffmpeg, and so every
            // generated thumbnail and filmstrip) read the tag and agree - which
            // is the same reason every other capture tool defaults to limited.
            codecContext->color_range = AVColorRange.AVCOL_RANGE_MPEG;
            // Both scalers convert with BT.709 coefficients, but these tags
            // were never written, leaving files marked "unknown" - which every
            // player then resolves to BT.709 for HD anyway. That accidentally
            // worked only because the conversion side has now been moved to 709
            // to match; tagging it explicitly is what actually makes the two
            // ends agree instead of relying on a coincidence of defaults.
            codecContext->colorspace = AVColorSpace.AVCOL_SPC_BT709;
            codecContext->color_primaries = AVColorPrimaries.AVCOL_PRI_BT709;
            codecContext->color_trc = AVColorTransferCharacteristic.AVCOL_TRC_BT709;
            // Constant bitrate means the configured rate IS the target, so
            // bit_rate and the ceiling are the same number. Constant quality
            // leaves the resolution/fps-derived estimate as the nominal average
            // and lets a derived ceiling bound how far a burst may exceed it.
            var maxBitrate = MaxBitrate(config);
            codecContext->bit_rate = IsConstantBitrate(config) ? maxBitrate : CaptureBitrate(config);
            // A real VBV to spend against, rather than leaving it unset and
            // letting the encoder fall back to an effectively per-frame budget.
            // One second of buffer lets a burst of hard-to-compress motion
            // borrow bits from the quiet stretch around it, which is the whole
            // mechanism that keeps fast gameplay from going soft. rc_max_rate
            // is the hard ceiling from the user's quality setting - with
            // constant-quality rate control it's this, not bit_rate, that bounds
            // how large a clip (and the in-memory ring buffer) can actually get.
            codecContext->rc_buffer_size = (int)codecContext->bit_rate;
            codecContext->rc_max_rate = maxBitrate;
            codecContext->gop_size = 240;
            codecContext->max_b_frames = 0;
            codecContext->flags |= ffmpeg.AV_CODEC_FLAG_GLOBAL_HEADER;
            // Hardware input: the frames the capture loop hands over ARE D3D11
            // textures, so that is the context's pixel format and the pool it
            // draws them from. sw_format on the pool (NV12) is what actually
            // reaches the encoder, so nothing else in the configuration above
            // changes between the two paths.
            if (hardwareFrames)
            {
                codecContext->pix_fmt = AVPixelFormat.AV_PIX_FMT_D3D11;
                codecContext->hw_frames_ctx = ffmpeg.av_buffer_ref((AVBufferRef*)hardwareFramesRef);
                if (codecContext->hw_frames_ctx is null)
                {
                    var unusableContext = codecContext;
                    ffmpeg.avcodec_free_context(&unusableContext);
                    return null;
                }
            }

            ApplyLowLatencyEncoderOptions(codecContext, candidateName, config, lowPower);

            var openResult = ffmpeg.avcodec_open2(codecContext, candidateCodec, null);
            if (openResult == 0) return codecContext;

            AppLog.Info($"Native encoder probe: {candidateName} failed to open (error {openResult}, lowPower={lowPower}, hardwareFrames={hardwareFrames}).");
            var failedContext = codecContext;
            ffmpeg.avcodec_free_context(&failedContext);
            return null;
        }

        foreach (var candidateName in EncoderCandidates)
        {
            var candidateCodec = ffmpeg.avcodec_find_encoder_by_name(candidateName);
            if (candidateCodec is null)
            {
                AppLog.Info($"Native encoder probe: {candidateName} not present in this ffmpeg build, skipping.");
                continue;
            }

            // QSV gets two shots: the fixed-function VDENC path first, then the
            // ordinary one if this driver/preset combination won't take it. Every
            // other encoder has nothing to retry, so it gets one. An inner loop
            // that exhausts its attempts falls through to the next candidate
            // exactly as a single failed open used to.
            var lowPowerAttempts = candidateName == "h264_qsv"
                ? new[] { true, false }
                : new[] { false };

            // NVENC and AMF both take an AV_PIX_FMT_D3D11 frame from a d3d11va
            // device, so the same pool feeds either - checked against this
            // build: "Supported hardware devices: cuda d3d11va" for NVENC,
            // "d3d11va dxva2 amf" for AMF.
            //
            // QSV is deliberately not here. It accepts only qsv frames from a
            // qsv device, so zero-copy there means deriving a QSV device from
            // the D3D11 one and mapping every captured texture into a qsv frame
            // on the hot path - new per-frame machinery that there is no Intel
            // hardware here to test. QSV keeps the system-memory path it has
            // always used, unchanged.
            //
            // None of this is load-bearing on AMD or Intel regardless: the pool
            // creation, the encoder open and the probe encode below each fall
            // back to system-memory frames on failure, which is exactly what
            // those machines do today.
            if (hardwareFramesRef != 0 && candidateName is "h264_nvenc" or "h264_amf")
            {
                var probeContext = TryOpen(candidateCodec, candidateName, false, hardwareFrames: true);
                if (probeContext is not null)
                {
                    var probed = ProbeHardwareEncode(probeContext, (AVBufferRef*)hardwareFramesRef);
                    // The probe already pushed a frame through this context, so
                    // it can't be the session's encoder - its timeline starts at
                    // pts 0 and the first real frame would be non-monotonic.
                    // Reopening costs a few milliseconds, once, at session start.
                    ffmpeg.avcodec_free_context(&probeContext);

                    if (probed)
                    {
                        var hardwareContext = TryOpen(candidateCodec, candidateName, false, hardwareFrames: true);
                        if (hardwareContext is not null)
                        {
                            encoderName = candidateName;
                            usingHardwareFrames = true;
                            AppLog.Info($"Native encoder probe: {candidateName} opened with D3D11 zero-copy input (no per-frame GPU readback/upload).");
                            return hardwareContext;
                        }
                    }
                    else
                    {
                        AppLog.Info($"Native encoder probe: {candidateName} opened but would not encode a D3D11 texture, falling back to system-memory frames.");
                    }
                }
            }

            foreach (var lowPower in lowPowerAttempts)
            {
                var codecContext = TryOpen(candidateCodec, candidateName, lowPower, hardwareFrames: false);
                if (codecContext is null) continue;

                encoderName = candidateName;
                AppLog.Info($"Native encoder probe: {candidateName} opened successfully (lowPower={lowPower}).");
                return codecContext;
            }

            AppLog.Info($"Native encoder probe: {candidateName} unusable - no matching GPU/driver, trying next.");
        }

        throw new InvalidOperationException("No usable H.264 encoder found (tried NVENC, AMD AMF, Intel QSV, software libx264).");
    }

    private static int CaptureBitrate(ReplayBufferConfig config)
    {
        var height = Math.Clamp(config.MaxHeight, 480, 2160);
        var frameRate = Math.Clamp(config.FrameRate, 15, 240);
        var megapixels = height switch
        {
            >= 2160 => 8.3,
            >= 1440 => 3.7,
            >= 1080 => 2.1,
            >= 720 => 0.9,
            _ => 0.4
        };
        return (int)Math.Clamp(megapixels * frameRate * 130_000, 8_000_000, 80_000_000);
    }

    private static (int Width, int Height) CaptureOutputSize(ReplayBufferConfig config, int sourceWidth, int sourceHeight)
    {
        var height = Math.Clamp(config.MaxHeight, 480, 2160);
        var aspect = sourceWidth / (double)Math.Max(1, sourceHeight);
        var width = MakeEven((int)Math.Round(height * aspect));
        return (width, MakeEven(height));
    }

    private static int MakeEven(int value)
    {
        value = Math.Max(2, value);
        return value % 2 == 0 ? value : value + 1;
    }

    private static ID3D11Device CreateD3D11Device()
    {
        var levels = new[]
        {
            Vortice.Direct3D.FeatureLevel.Level_11_1,
            Vortice.Direct3D.FeatureLevel.Level_11_0,
            Vortice.Direct3D.FeatureLevel.Level_10_1,
        };
        D3D11.D3D11CreateDevice(null, DriverType.Hardware, DeviceCreationFlags.BgraSupport, levels, out var device, out _, out _).CheckError();

        // Microsoft's own WGC samples explicitly mark the D3D11 device
        // multithread-protected when it's touched from both the capture
        // pool's internal thread and a consumer thread (exactly our setup -
        // WGC's own frame production vs. our capture loop's CopyResource/Map
        // calls on the same device). Without it, the driver can apply
        // conservative cross-thread synchronization that serializes/throttles
        // access - a plausible source of a fixed-ish fps ceiling that nothing
        // on the consumption side (buffer depth, event vs. polling, timer
        // resolution) could touch, since none of those affect device-level
        // thread safety. Never tried before now; safe no-op if unsupported.
        TryMarkDeviceMultithreadProtected(device!);
        return device!;
    }

    [System.Runtime.InteropServices.UnmanagedFunctionPointer(System.Runtime.InteropServices.CallingConvention.StdCall)]
    private delegate int SetMultithreadProtectedDelegate(IntPtr self, int bMTProtect);

    // Vortice's ComObject.QueryInterface<T>() requires T to be a SharpGen-generated
    // ComObject, which ID3D10Multithread isn't (Vortice.Direct3D11 doesn't define
    // it) - falls back to a raw COM QueryInterface + manual vtable call instead.
    // ID3D10Multithread's vtable, after IUnknown's QueryInterface/AddRef/Release
    // (indices 0-2): Enter=3, Leave=4, SetMultithreadProtected=5, GetMultithreadProtected=6.
    private static void TryMarkDeviceMultithreadProtected(ID3D11Device device)
    {
        var multithreadIid = new Guid("9B7E4E00-342C-4106-A19F-4F2704F689F0");
        var multithreadPtr = IntPtr.Zero;
        try
        {
            var hr = Marshal.QueryInterface(device.NativePointer, in multithreadIid, out multithreadPtr);
            if (hr != 0 || multithreadPtr == IntPtr.Zero)
            {
                AppLog.Info($"Native capture: D3D11 device does not support ID3D10Multithread (hr={hr}), continuing without it.");
                return;
            }

            var vtable = Marshal.ReadIntPtr(multithreadPtr, 0);
            var setMultithreadProtectedPtr = Marshal.ReadIntPtr(vtable, 5 * IntPtr.Size);
            var setMultithreadProtected = Marshal.GetDelegateForFunctionPointer<SetMultithreadProtectedDelegate>(setMultithreadProtectedPtr);
            setMultithreadProtected(multithreadPtr, 1);
            AppLog.Info("Native capture: D3D11 device marked multithread-protected.");
        }
        catch (Exception error)
        {
            AppLog.Info($"Native capture: could not mark D3D11 device multithread-protected (non-fatal): {error.Message}");
        }
        finally
        {
            if (multithreadPtr != IntPtr.Zero) Marshal.Release(multithreadPtr);
        }
    }

    // Windows' default system timer resolution is ~15.6ms unless a process
    // explicitly requests better - Thread.Sleep(4) in CaptureLoop's empty-poll
    // fallback commonly actually sleeps ~15.6ms on an unraised system (rounds
    // up to the next scheduler tick), not 4ms. That's a hard, resolution-
    // independent ceiling of ~64 loop iterations/sec regardless of encode
    // settings - consistent with what testing showed (same ~35-46fps whether
    // targeting 1080p60 or 1440p120, with the encode/scale/copy stages
    // themselves proven to have headroom for 80fps+). Raising it to 1ms for
    // the life of the capture session is the standard fix for latency-
    // sensitive polling loops on Windows (the same thing games/capture
    // software normally do).
    [DllImport("winmm.dll", EntryPoint = "timeBeginPeriod")]
    private static extern uint TimeBeginPeriod(uint uMilliseconds);

    [DllImport("winmm.dll", EntryPoint = "timeEndPeriod")]
    private static extern uint TimeEndPeriod(uint uMilliseconds);

    [DllImport("user32.dll")]
    private static extern bool IsWindow(nint hWnd);

    [DllImport("user32.dll")]
    private static extern nint MonitorFromPoint(PointStruct pt, uint dwFlags);

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out PointStruct point);

    private const uint MONITOR_DEFAULTTONEAREST = 2;

    [DllImport("user32.dll")]
    private static extern nint MonitorFromWindow(nint hWnd, uint dwFlags);

    [DllImport("user32.dll")]
    private static extern nint GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern bool IsIconic(nint hWnd);

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(nint hWnd, out RectStruct lpRect);

    private const int DWMWA_EXTENDED_FRAME_BOUNDS = 9;

    [DllImport("dwmapi.dll")]
    private static extern int DwmGetWindowAttribute(nint hwnd, int dwAttribute, out RectStruct pvAttribute, int cbAttribute);

    [StructLayout(LayoutKind.Sequential)]
    private struct RectStruct
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PointStruct
    {
        public int X;
        public int Y;
    }

    // Data comes from ArrayPool and is therefore usually LONGER than the packet
    // it holds - Length, not Data.Length, is the packet. Every consumer must
    // respect that.
    private readonly record struct RingPacket(byte[] Data, int Length, long PtsMs, bool IsKeyframe, DateTime WallClockUtc);

    private readonly record struct PauseEvent(DateTime WallClockUtc, bool IsPaused);
}
