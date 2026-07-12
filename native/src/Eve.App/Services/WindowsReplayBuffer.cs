using Eve.Capture.Abstractions;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using ScreenRecorderLib;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;

namespace Eve.App.Services;

[SupportedOSPlatform("windows")]
public sealed class WindowsReplayBuffer : IReplayBuffer, IDisposable
{
    private static readonly TimeSpan MaxVideoSegmentDuration = TimeSpan.FromSeconds(20);
    private const double MinimumSegmentHealth = 0.80;
    private readonly Func<ReplayBufferConfig> _configProvider;
    private readonly string _bufferFolder;
    private readonly AudioCapturePipeline _audio;
    private readonly object _lock = new();
    private Recorder? _recorder;
    private Timer? _rotationTimer;
    private string _activePath = string.Empty;
    private ReplayBufferConfig? _config;
    private DateTime _startedAtUtc;
    private DateTime _lastRecorderRestartUtc = DateTime.MinValue;
    // A kill-streak's debounced auto-clips (Assist, 3K, Headshot 4K, ...) tend to
    // land roughly 15-16s apart in practice - each one still past a 5s "fresh"
    // window forces its own recorder stop/restart, and each restart is a small
    // real gap in the buffer. Since a *later* clip's window can span back across
    // an *earlier* clip's forced-restart point (they're pulling from the same
    // buffered segments), that earlier gap ends up embedded inside the later,
    // bigger clip - showing up as a jump partway through. 18s covers a typical
    // debounced-burst gap so consecutive queued saves reuse the same fresh
    // segment instead of each forcing their own restart.
    private static readonly TimeSpan MinRecorderRestartInterval = TimeSpan.FromSeconds(18);
    private TaskCompletionSource<string>? _completion;
    private readonly SemaphoreSlim _transition = new(1, 1);
    private readonly List<ReplayVideoSegment> _segments = new();
    private volatile bool _sessionActive;

    public WindowsReplayBuffer(Func<ReplayBufferConfig> configProvider)
    {
        _configProvider = configProvider;
        _bufferFolder = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "EVE",
            "windows-replay-buffer");
        _audio = new AudioCapturePipeline(_bufferFolder);
    }

    // Deliberately not "_recorder is not null": rotation disposes the old recorder
    // and creates a new one with a brief gap in between where _recorder is null but
    // the session is still very much active. External callers (MainWindow's 1s game-
    // detection timer) polling IsRecording during that gap would see "not recording"
    // and call StartAsync() again mid-session - whose first action is CleanupOldFiles(),
    // which deletes every replay_*.mp4 including ones the current save still needs.
    // That race was the actual cause of segments vanishing ("No such file or
    // directory") despite having been confirmed finalized moments earlier.
    public bool IsRecording => _sessionActive;
    public TimeSpan Duration { get; private set; } = TimeSpan.FromSeconds(60);
    public event EventHandler? RecordingStopped;

    public Task StartAsync(CancellationToken cancellationToken = default)
    {
        if (IsRecording) return Task.CompletedTask;

        _sessionActive = true;
        Directory.CreateDirectory(_bufferFolder);
        CleanupOldFiles();
        _config = _configProvider();
        Duration = TimeSpan.FromSeconds(Math.Clamp(_config.DurationSeconds, 30, 1200));
        StartRecorder();
        _audio.Start(_config);
        AppLog.Info($"Replay buffer started: duration={Duration.TotalSeconds:0}s, quality={_config.MaxHeight}p{_config.FrameRate}.");
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        await _transition.WaitAsync(cancellationToken);
        try
        {
            _sessionActive = false;
            _rotationTimer?.Dispose();
            _rotationTimer = null;
            var recorder = _recorder;
            if (recorder is null)
            {
                _audio.Stop();
                return;
            }
            var completion = _completion;
            recorder.Stop();
            if (completion is not null)
            {
                await completion.Task.WaitAsync(TimeSpan.FromSeconds(10), cancellationToken);
            }

            DisposeRecorder();
            TryDelete(_activePath);
            foreach (var segment in _segments.ToArray()) TryDelete(segment.Path);
            _segments.Clear();
            _activePath = string.Empty;
            _audio.Stop();
        }
        finally
        {
            _transition.Release();
        }
    }

    public async Task<string> SaveReplayAsync(string outputFolder, CancellationToken cancellationToken = default, string? titleOverride = null)
    {
        ReplayVideoSegment[] sourceSegments;
        double videoOffsetSeconds;
        double clipDurationSeconds;
        ReplayBufferConfig config;
        string sourcePath = string.Empty;
        string outputPath = string.Empty;
        await _transition.WaitAsync(cancellationToken);
        try
        {
            if (!IsRecording) throw new InvalidOperationException("Replay buffer is not running.");

            Directory.CreateDirectory(outputFolder);
            // Stopping the live recording to finalize "right now" as a usable segment,
            // then spinning up a brand new native Recorder, is real GPU-side work - fine
            // once per save under normal use, but auto-clip triggers now queue instead
            // of dropping (see MainWindow's clip-save lock), so a fast multi-kill round
            // can call this several times within a few seconds. Recreating the hardware
            // encoder that fast, back to back, was outpacing the driver's own teardown
            // and growing GPU memory over a burst. Skipping the forced stop/restart when
            // the current segment is still very fresh costs at most a few seconds off the
            // tail of a queued clip - imperceptible against a replay window that's many
            // seconds to minutes long - in exchange for not thrashing the recorder.
            var recorderIsFresh = DateTime.UtcNow - _lastRecorderRestartUtc < MinRecorderRestartInterval;
            var activeSegment = recorderIsFresh ? null : await TryStopCurrentRecordingAsync(cancellationToken);
            if (activeSegment is not null)
            {
                AddSegment(activeSegment);
                // Restart capture immediately, before the ffprobe hydration pass below -
                // that pass runs ffprobe once per buffered segment and can easily take a
                // second or more with a full buffer, and until now the recorder stayed
                // fully stopped for all of it. That was real, silent recording downtime
                // that landed inside the replay window: concat just glues segments back
                // to back with continuous timestamps, so the missing wall-clock time
                // wasn't represented anywhere - playback simply jumped forward across it,
                // in perfect A/V sync since both tracks skipped the same real gap.
                if (IsRecording) StartRecorder();
            }

            var availableSegments = await HydrateSegmentDurationsAsync(GetReplaySegments(), cancellationToken);
            if (availableSegments.Length == 0)
            {
                throw new InvalidOperationException("Replay buffer has no finished segments yet.");
            }

            (sourceSegments, videoOffsetSeconds, clipDurationSeconds) = SelectReplayWindow(availableSegments, Duration.TotalSeconds);
            if (clipDurationSeconds < 1)
            {
                throw new InvalidOperationException("Replay just started. Try again in a second.");
            }

            config = _config ?? _configProvider();
            AppLog.Info($"Replay save timing: segments={sourceSegments.Length}, videoOffset={videoOffsetSeconds:0.###}s, duration={clipDurationSeconds:0.###}s.");
            PruneSegments();

            sourcePath = await BuildReplayVideoAsync(sourceSegments, cancellationToken);
            var clipName = string.IsNullOrWhiteSpace(titleOverride) ? config.GameDisplayName : titleOverride;
            outputPath = Path.Combine(outputFolder, ClipFileNaming.BuildFileName(clipName, DateTime.Now, "mp4"));
            await MuxAudioTracksAsync(sourcePath, outputPath, videoOffsetSeconds, sourceSegments, clipDurationSeconds, config, cancellationToken);
        }
        finally
        {
            TryDelete(sourcePath);
            _transition.Release();
        }

        return outputPath;
    }

    public void Dispose()
    {
        DisposeRecorder();
    }

    private void StartRecorder()
    {
        lock (_lock)
        {
            _activePath = Path.Combine(_bufferFolder, $"replay_{Guid.NewGuid():N}.mp4");
            _completion = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
            var options = CreateOptions(_config ?? _configProvider());
            _recorder = Recorder.CreateRecorder(options);
            _recorder.OnRecordingComplete += Recorder_OnRecordingComplete;
            _recorder.OnRecordingFailed += Recorder_OnRecordingFailed;
            _startedAtUtc = DateTime.UtcNow;
            _lastRecorderRestartUtc = _startedAtUtc;
            _recorder.Record(_activePath);
            _rotationTimer?.Dispose();
            var segmentDuration = VideoSegmentDuration();
            _rotationTimer = new Timer(_ => _ = RotateAsync(), null, segmentDuration, segmentDuration);
            AppLog.Info($"Replay video segment started: path={_activePath}, rotateEvery={segmentDuration.TotalSeconds:0}s.");
        }
    }

    private async Task RotateAsync()
    {
        if (!await _transition.WaitAsync(0))
        {
            _rotationTimer?.Change(TimeSpan.FromSeconds(1), Timeout.InfiniteTimeSpan);
            return;
        }
        try
        {
            if (!IsRecording) return;
            _rotationTimer?.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
            var completedSegment = await TryStopCurrentRecordingAsync(CancellationToken.None);
            if (completedSegment is not null)
            {
                AddSegment(completedSegment);
            }

            PruneSegments();
            // Refresh config on every rotation, not just at session start, so the
            // capture source can follow the currently detected game (or fall back to
            // the monitor when none is detected) instead of being locked to whatever
            // was foreground when the replay buffer first started.
            _config = _configProvider();
            StartRecorder();
        }
        catch (Exception error)
        {
            AppLog.Error("Replay segment rotation failed", error);
            RecoverRecorderAfterFailure();
        }
        finally
        {
            _transition.Release();
        }
    }

    private async Task<ReplayVideoSegment?> TryStopCurrentRecordingAsync(CancellationToken cancellationToken)
    {
        var recorder = _recorder ?? throw new InvalidOperationException("Replay buffer is not running.");
        var completion = _completion ?? throw new InvalidOperationException("Replay buffer is not ready.");
        var startedAt = _startedAtUtc;
        var endedAt = DateTime.UtcNow;
        recorder.Stop();
        string path;
        try
        {
            // A normal stop+finalize completes in well under a second (rotation
            // logs show ~20.0-20.3s wall time against a 20s timer). 10s was
            // generous enough that when the native recorder actually hung (GPU/
            // driver stall), the buffer sat completely offline for the whole
            // wait before recovery even started - directly showing up as a
            // freeze/skip in any clip whose window crossed that gap. Failing
            // faster doesn't fix the underlying native hang, but it shrinks the
            // resulting blackout.
            path = await completion.Task.WaitAsync(TimeSpan.FromSeconds(4), cancellationToken);
        }
        catch (TimeoutException error)
        {
            AppLog.Error($"Replay segment stop timed out; dropping active segment: path={_activePath}", error);
            var failedPath = _activePath;
            DisposeRecorder();
            TryDelete(failedPath);
            return null;
        }

        DisposeRecorder();
        var bytes = File.Exists(path) ? new FileInfo(path).Length : 0;
        AppLog.Info($"Replay segment stopped: path={path}, start={startedAt:o}, end={endedAt:o}, wallDuration={(endedAt - startedAt).TotalSeconds:0.###}s, bytes={bytes}.");
        return new ReplayVideoSegment(path, startedAt, endedAt, TimeSpan.Zero);
    }

    private static RecorderOptions CreateOptions(ReplayBufferConfig config)
    {
        var options = RecorderOptions.DefaultMainMonitor;
        if (config.GameWindowHandle != 0 && IsWindow(config.GameWindowHandle))
        {
            options.SourceOptions.RecordingSources = new List<RecordingSourceBase>
            {
                new WindowRecordingSource(config.GameWindowHandle)
                {
                    IsCursorCaptureEnabled = false,
                    IsBorderRequired = false,
                    Stretch = StretchMode.Uniform
                }
            };
            AppLog.Info($"Replay capture source: window handle={config.GameWindowHandle}, game={config.GameDisplayName}.");
        }
        else
        {
            AppLog.Info("Replay capture source: main monitor (no game detected).");
        }

        options.AudioOptions = new AudioOptions
        {
            IsAudioEnabled = false,
            IsInputDeviceEnabled = false,
            IsOutputDeviceEnabled = false
        };
        options.OutputOptions = new OutputOptions
        {
            RecorderMode = RecorderMode.Video,
            IsVideoCaptureEnabled = true,
            OutputFrameSize = CaptureOutputSize(config),
            Stretch = StretchMode.Uniform
        };
        options.VideoEncoderOptions = new VideoEncoderOptions
        {
            Encoder = new H264VideoEncoder
            {
                BitrateMode = H264BitrateControlMode.UnconstrainedVBR,
                EncoderProfile = H264Profile.High
            },
            IsHardwareEncodingEnabled = true,
            IsLowLatencyEnabled = true,
            IsThrottlingDisabled = true,
            IsFixedFramerate = true,
            Quality = 85,
            Bitrate = CaptureBitrate(config),
            Framerate = Math.Clamp(config.FrameRate, 15, 240)
        };

        return options;
    }

    private TimeSpan VideoSegmentDuration()
    {
        return TimeSpan.FromSeconds(Math.Clamp(Math.Min(Duration.TotalSeconds, MaxVideoSegmentDuration.TotalSeconds), 5, MaxVideoSegmentDuration.TotalSeconds));
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
        // Windows Capture (ScreenRecorderLib/Media Foundation) looks noticeably
        // softer than OBS's NVENC CQP encode at an equivalent nominal bitrate, so
        // it needs a higher target to look comparable - bumped from ~115k to
        // ~170k per megapixel-frame after a quality complaint at the old rate.
        return (int)Math.Clamp(megapixels * frameRate * 170_000, 8_000_000, 80_000_000);
    }

    private static ScreenSize CaptureOutputSize(ReplayBufferConfig config)
    {
        var height = Math.Clamp(config.MaxHeight, 480, 2160);
        var sourceWidth = Math.Max(1, config.CaptureWidth);
        var sourceHeight = Math.Max(1, config.CaptureHeight);
        var aspect = sourceWidth / (double)sourceHeight;
        var width = MakeEven((int)Math.Round(height * aspect));
        return new ScreenSize(width, MakeEven(height));
    }

    private static int MakeEven(int value)
    {
        value = Math.Max(2, value);
        return value % 2 == 0 ? value : value + 1;
    }

    private ReplayVideoSegment[] GetReplaySegments()
    {
        var cutoff = DateTime.UtcNow - Duration - TimeSpan.FromSeconds(2);
        return _segments
            .Where(segment => segment.EndedAtUtc >= cutoff && File.Exists(segment.Path))
            .OrderBy(segment => segment.StartedAtUtc)
            .ToArray();
    }

    private void AddSegment(ReplayVideoSegment segment)
    {
        if (File.Exists(segment.Path)) _segments.Add(segment);
    }

    private void PruneSegments()
    {
        var cutoff = DateTime.UtcNow - Duration - TimeSpan.FromSeconds(15);
        foreach (var segment in _segments.Where(segment => segment.EndedAtUtc < cutoff).ToArray())
        {
            _segments.Remove(segment);
            TryDelete(segment.Path);
        }

        _audio.PruneOlderThan(cutoff);
    }

    private async Task<ReplayVideoSegment[]> HydrateSegmentDurationsAsync(ReplayVideoSegment[] segments, CancellationToken cancellationToken)
    {
        var hydrated = new List<ReplayVideoSegment>(segments.Length);
        foreach (var segment in segments)
        {
            var duration = segment.VideoDuration > TimeSpan.Zero
                ? segment.VideoDuration
                : await ProbeVideoDurationAsync(segment.Path, cancellationToken);
            if (duration > TimeSpan.FromMilliseconds(250))
            {
                var wallDuration = segment.EndedAtUtc - segment.StartedAtUtc;
                var adjustedStartUtc = segment.EndedAtUtc - duration;
                var correctionMs = (adjustedStartUtc - segment.StartedAtUtc).TotalMilliseconds;
                var fpsHealth = wallDuration.TotalSeconds > 0 ? duration.TotalSeconds / wallDuration.TotalSeconds : 1d;
                AppLog.Info($"Replay segment hydrated: path={segment.Path}, wall={wallDuration.TotalSeconds:0.###}s, video={duration.TotalSeconds:0.###}s, startCorrection={correctionMs:0}ms, health={fpsHealth:P0}.");
                if (fpsHealth < MinimumSegmentHealth || wallDuration > MaxVideoSegmentDuration + TimeSpan.FromSeconds(8))
                {
                    AppLog.Info($"Replay segment skipped: unhealthy capture, path={segment.Path}, health={fpsHealth:P0}.");
                    TryDelete(segment.Path);
                    continue;
                }

                hydrated.Add(segment with { StartedAtUtc = adjustedStartUtc, VideoDuration = duration });
            }
            else
            {
                AppLog.Info($"Replay segment skipped: no usable duration, path={segment.Path}.");
            }
        }

        return hydrated.ToArray();
    }

    private static (ReplayVideoSegment[] Segments, double FirstOffsetSeconds, double DurationSeconds) SelectReplayWindow(ReplayVideoSegment[] segments, double targetDurationSeconds)
    {
        var selected = new List<ReplayVideoSegment>();
        var total = 0d;
        for (var index = segments.Length - 1; index >= 0 && total < targetDurationSeconds; index--)
        {
            selected.Insert(0, segments[index]);
            total += segments[index].VideoDuration.TotalSeconds;
        }

        total = selected.Sum(segment => segment.VideoDuration.TotalSeconds);
        var duration = Math.Min(total, targetDurationSeconds);
        var offset = Math.Max(0, total - duration);
        return (selected.ToArray(), offset, duration);
    }

    // Returns the timestamp of the video's own keyframe at-or-before targetSeconds -
    // i.e. exactly where "-ss targetSeconds -i path -c:v copy" will actually start
    // the output, which ffmpeg does not report back on its own. Reads packet-level
    // flags only (no decode), so it's cheap even on a full clip-length file. Null
    // if the probe fails or has nothing usable, signaling the caller to fall back
    // to the slower but exact decode+re-encode trim instead of guessing.
    private async Task<double?> FindNearestKeyframeAtOrBeforeAsync(string path, double targetSeconds, CancellationToken cancellationToken)
    {
        var result = await RunProcessAsync("ffprobe", new[]
        {
            "-v", "error",
            "-select_streams", "v:0",
            "-show_entries", "packet=pts_time,flags",
            "-of", "csv=p=0",
            path
        }, cancellationToken);

        if (result.ExitCode != 0)
        {
            AppLog.Info($"Keyframe probe failed: path={path}, exit={result.ExitCode}, error={result.Error}");
            return null;
        }

        double? best = null;
        foreach (var line in result.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = line.Split(',');
            if (parts.Length < 2 || !parts[1].Contains('K', StringComparison.Ordinal)) continue;
            if (!double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var pts)) continue;
            if (pts <= targetSeconds && (best is null || pts > best)) best = pts;
        }

        return best ?? 0;
    }

    private async Task<TimeSpan> ProbeVideoDurationAsync(string path, CancellationToken cancellationToken)
    {
        // The most recently stopped segment is the one most likely to fail here: the
        // container's moov atom can still be finalizing for a moment right after
        // ScreenRecorderLib's "recording complete" callback fires. That segment is also
        // the one covering the moment the hotkey was pressed, so if this gives up
        // immediately, the whole segment gets silently dropped and the clip ends up to
        // 20s short of where it should - the reported "the shot doesn't appear at all".
        const int attempts = 5;
        for (var attempt = 0; attempt < attempts; attempt++)
        {
            var result = await RunProcessAsync("ffprobe", new[]
            {
                "-v", "error",
                "-show_entries", "format=duration",
                "-of", "default=noprint_wrappers=1:nokey=1",
                path
            }, cancellationToken);

            if (result.ExitCode == 0 &&
                double.TryParse(result.Output.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds) &&
                seconds > 0)
            {
                return TimeSpan.FromSeconds(seconds);
            }

            if (attempt < attempts - 1)
            {
                await Task.Delay(150, cancellationToken);
                continue;
            }

            AppLog.Info($"ffprobe duration failed after {attempts} attempts: path={path}, exit={result.ExitCode}, error={result.Error}");
        }

        return TimeSpan.Zero;
    }

    private async Task<string> BuildReplayVideoAsync(IReadOnlyList<ReplayVideoSegment> segments, CancellationToken cancellationToken)
    {
        var concatPath = Path.Combine(_bufferFolder, $"concat_{Guid.NewGuid():N}.txt");
        var stitchedPath = Path.Combine(_bufferFolder, $"stitched_{Guid.NewGuid():N}.mp4");
        try
        {
            // A segment that was readable moments ago during hydration can still vanish
            // out from under us before ffmpeg gets to it (seen in practice - likely AV
            // real-time scanning briefly locking a freshly written mp4). That kind of
            // lock is normally short-lived, so retry with backoff before giving up on a
            // segment instead of dropping it immediately.
            var usableSegments = await Task.WhenAll(segments.Select(async segment => (segment, exists: await WaitForFileAsync(segment.Path, cancellationToken))));
            var missing = usableSegments.Count(entry => !entry.exists);
            if (missing > 0)
            {
                AppLog.Info($"Replay concat: {missing} segment(s) still missing after retry, continuing without them.");
            }
            var resolvedSegments = usableSegments.Where(entry => entry.exists).Select(entry => entry.segment).ToArray();
            if (resolvedSegments.Length == 0)
            {
                throw new InvalidOperationException("Replay segments were unavailable when building the clip. Try again.");
            }

            await File.WriteAllLinesAsync(
                concatPath,
                resolvedSegments.Select(segment => $"file '{EscapeConcatPath(segment.Path)}'"),
                cancellationToken);

            var concatResult = await RunProcessAsync(
                "ffmpeg",
                new[] { "-y", "-f", "concat", "-safe", "0", "-i", concatPath, "-c", "copy", stitchedPath },
                cancellationToken);
            if (concatResult.ExitCode != 0)
            {
                throw new InvalidOperationException(string.IsNullOrWhiteSpace(concatResult.Error) ? "ffmpeg concat failed." : concatResult.Error);
            }

            return stitchedPath;
        }
        finally
        {
            TryDelete(concatPath);
        }
    }

    private static async Task<bool> WaitForFileAsync(string path, CancellationToken cancellationToken)
    {
        const int attempts = 5;
        for (var attempt = 0; attempt < attempts; attempt++)
        {
            try
            {
                if (File.Exists(path))
                {
                    using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                    if (stream.Length > 0) return true;
                }
            }
            catch (IOException)
            {
                // Likely still locked (e.g. AV real-time scan) - retry.
            }
            catch (UnauthorizedAccessException)
            {
                // Same as above.
            }

            if (attempt < attempts - 1) await Task.Delay(150, cancellationToken);
        }

        return false;
    }

    private static string EscapeConcatPath(string path)
    {
        return path.Replace("\\", "/", StringComparison.Ordinal).Replace("'", "'\\''", StringComparison.Ordinal);
    }

    private static string FormatDuration(TimeSpan duration)
    {
        return duration.TotalSeconds < 1
            ? "0s"
            : $"{Math.Floor(duration.TotalSeconds):0}s";
    }

    // Video segments are only ever wall-clock-accurate individually (each one's
    // StartedAtUtc/EndedAtUtc/VideoDuration triple is self-consistent per
    // segment via the health correction in HydrateSegmentDurationsAsync), and
    // ScreenRecorderLib's per-segment capture rate ("health") varies segment to
    // segment (seen ~94-96% in practice, i.e. a 20s wall-clock segment only
    // encodes ~19s of actual video). Building ONE global wall-clock window
    // anchored off just the first segment and assuming a constant 1:1 video-
    // to-wall-clock rate for the rest of the clip's duration was accurate near
    // the start but drifted more with every additional segment stitched in -
    // exactly the "gets worse toward the end of the clip" desync reported.
    // Fixed by aligning audio independently per video segment (each segment's
    // own accurate wall window) and concatenating those aligned slices in the
    // same order as the video segments, instead of one flat trim across the
    // whole clip.
    private async Task MuxAudioTracksAsync(string videoPath, string outputPath, double videoOffsetSeconds, IReadOnlyList<ReplayVideoSegment> sourceSegments, double clipDurationSeconds, ReplayBufferConfig config, CancellationToken cancellationToken)
    {
        var snapshots = new List<string>();
        var duration = Math.Max(1, clipDurationSeconds);

        // -ss + -c:v copy seeks to the nearest keyframe AT OR BEFORE videoOffsetSeconds,
        // not videoOffsetSeconds itself - the container timestamps end up perfectly
        // clean either way (confirmed via ffprobe packet inspection), but the audio
        // tracks were still being built for the window starting at the exact
        // requested offset. Video actually starting earlier than that while audio
        // starts exactly there is a constant, whole-clip offset between them - not a
        // muxer timestamp bug, which is why -avoid_negative_ts didn't touch it.
        // Finding the real keyframe position first and aligning audio to THAT instead
        // fixes the two tracks representing the same window again.
        var keyframeOffset = await FindNearestKeyframeAtOrBeforeAsync(videoPath, videoOffsetSeconds, cancellationToken);
        var useStreamCopy = keyframeOffset.HasValue;
        var effectiveOffsetSeconds = keyframeOffset ?? videoOffsetSeconds;
        var effectiveDurationSeconds = duration + (videoOffsetSeconds - effectiveOffsetSeconds);

        var segmentWindows = BuildSegmentWindows(sourceSegments, effectiveOffsetSeconds);
        AppLog.Info($"Replay mux start: videoOffset={videoOffsetSeconds:0.###}s, keyframeOffset={effectiveOffsetSeconds:0.###}s, duration={effectiveDurationSeconds:0.###}s, segments={segmentWindows.Count}.");

        try
        {
            var tracks = await _audio.BuildAlignedTracksAsync(segmentWindows, config, snapshots, cancellationToken);
            AppLog.Info($"Replay mux inputs: {string.Join(", ", tracks.Select(track => $"{track.Label}={AudioFileLength(track.Path)}b"))}.");

            var metadataArgs = new List<string>();
            for (var i = 0; i < tracks.Count; i++) metadataArgs.AddRange(new[] { $"-metadata:s:a:{i}", $"title={tracks[i].Label}" });
            metadataArgs.AddRange(new[] { "-metadata", $"comment={ClipMetadataTagger.BuildCommentValue("Windows Capture")}" });

            // Trimming via -filter_complex meant decoding and fully re-encoding the
            // clip's entire video track (NVENC/x264) just to get a frame-accurate cut
            // - for a long/high-res replay that's genuinely slow (this was the actual
            // "clips take forever" bottleneck, not the concat step, which already used
            // -c copy). -ss before -i is a fast keyframe seek, and -c:v copy just
            // remuxes instead of re-encoding, using the keyframe-aligned offset/duration
            // computed above so audio and video represent the same window. Falls back
            // to the old exact-offset decode+re-encode path if no keyframe could be
            // found (e.g. probe failure) or the copy attempt itself fails.
            (int ExitCode, string Output, string Error) result = (-1, string.Empty, string.Empty);
            if (useStreamCopy)
            {
                var copyArgs = new List<string> { "-y", "-ss", FormatSeconds(effectiveOffsetSeconds), "-i", videoPath };
                foreach (var track in tracks) copyArgs.AddRange(new[] { "-i", track.Path });
                copyArgs.AddRange(new[] { "-map", "0:v" });
                for (var i = 0; i < tracks.Count; i++) copyArgs.AddRange(new[] { "-map", $"{i + 1}:a" });
                copyArgs.AddRange(new[] { "-c:v", "copy", "-c:a", "aac", "-b:a", "192k", "-avoid_negative_ts", "make_zero", "-t", FormatSeconds(effectiveDurationSeconds) });
                copyArgs.AddRange(metadataArgs);
                copyArgs.Add(outputPath);
                result = await RunProcessAsync("ffmpeg", copyArgs, cancellationToken);
                AppLog.Info($"Replay mux (stream copy) result: exit={result.ExitCode}, output={outputPath}.");
            }

            if (result.ExitCode != 0)
            {
                var reencodeBase = new List<string> { "-y", "-i", videoPath };
                foreach (var track in tracks) reencodeBase.AddRange(new[] { "-i", track.Path });
                reencodeBase.AddRange(new[]
                {
                    "-filter_complex", $"[0:v]trim=start={FormatSeconds(videoOffsetSeconds)}:duration={FormatSeconds(duration)},setpts=PTS-STARTPTS[vout]",
                    "-map", "[vout]"
                });
                for (var i = 0; i < tracks.Count; i++) reencodeBase.AddRange(new[] { "-map", $"{i + 1}:a" });
                reencodeBase.AddRange(new[] { "-t", FormatSeconds(duration) });
                reencodeBase.AddRange(metadataArgs);

                var args = reencodeBase.ToList();
                args.AddRange(BuildNvencVideoArgs(config));
                args.AddRange(new[] { "-c:a", "aac", "-b:a", "192k", outputPath });
                result = await RunProcessAsync("ffmpeg", args, cancellationToken);
                AppLog.Info($"Replay mux (re-encode, NVENC) result: exit={result.ExitCode}, output={outputPath}.");
                if (result.ExitCode != 0)
                {
                    args = reencodeBase.ToList();
                    args.AddRange(BuildSoftwareVideoArgs());
                    args.AddRange(new[] { "-c:a", "aac", "-b:a", "192k", outputPath });
                    result = await RunProcessAsync("ffmpeg", args, cancellationToken);
                    AppLog.Info($"Replay mux (re-encode, software) result: exit={result.ExitCode}, output={outputPath}.");
                    if (result.ExitCode != 0) throw new InvalidOperationException(string.IsNullOrWhiteSpace(result.Error) ? "ffmpeg mux failed." : result.Error);
                }
            }
        }
        finally
        {
            foreach (var snapshot in snapshots) TryDelete(snapshot);
        }
    }

    private static List<(DateTime StartUtc, double DurationSeconds)> BuildSegmentWindows(IReadOnlyList<ReplayVideoSegment> sourceSegments, double videoOffsetSeconds)
    {
        var windows = new List<(DateTime StartUtc, double DurationSeconds)>(sourceSegments.Count);
        var remainingOffset = videoOffsetSeconds;
        foreach (var segment in sourceSegments)
        {
            var segmentDuration = segment.VideoDuration.TotalSeconds;
            var trimFromStart = Math.Min(remainingOffset, segmentDuration);
            remainingOffset = Math.Max(0, remainingOffset - trimFromStart);
            var windowDuration = segmentDuration - trimFromStart;
            if (windowDuration <= 0) continue;
            windows.Add((segment.StartedAtUtc + TimeSpan.FromSeconds(trimFromStart), windowDuration));
        }

        return windows;
    }

    private static string FormatSeconds(double seconds)
    {
        return seconds.ToString("0.###", CultureInfo.InvariantCulture);
    }

    private static string[] BuildNvencVideoArgs(ReplayBufferConfig config)
    {
        var bitrate = MuxVideoBitrate(config);
        var maxrate = (int)Math.Round(bitrate * 1.5);
        var bufsize = bitrate * 2;
        return new[]
        {
            "-c:v", "h264_nvenc",
            "-preset", "p4",
            "-tune", "hq",
            "-rc", "vbr",
            "-cq", "18",
            "-profile:v", "high",
            "-b:v", bitrate.ToString(CultureInfo.InvariantCulture),
            "-maxrate", maxrate.ToString(CultureInfo.InvariantCulture),
            "-bufsize", bufsize.ToString(CultureInfo.InvariantCulture)
        };
    }

    private static string[] BuildSoftwareVideoArgs()
    {
        return new[] { "-c:v", "libx264", "-preset", "veryfast", "-crf", "18" };
    }

    private static int MuxVideoBitrate(ReplayBufferConfig config)
    {
        var height = Math.Clamp(config.MaxHeight, 480, 2160);
        var frameRate = Math.Clamp(config.FrameRate, 15, 240);
        var baseRate = height switch
        {
            >= 2160 => 48_000_000,
            >= 1440 => 32_000_000,
            >= 1080 => 20_000_000,
            >= 720 => 10_000_000,
            _ => 6_000_000
        };
        return frameRate >= 60 ? baseRate : (int)Math.Round(baseRate * 0.7);
    }

    private void RecoverRecorderAfterFailure()
    {
        var failedPath = _activePath;
        DisposeRecorder();
        TryDelete(failedPath);
        try
        {
            StartRecorder();
        }
        catch (Exception restartError)
        {
            AppLog.Error("Replay recorder restart failed", restartError);
            _sessionActive = false;
            RecordingStopped?.Invoke(this, EventArgs.Empty);
        }
    }

    private void Recorder_OnRecordingComplete(object? sender, RecordingCompleteEventArgs e)
    {
        _completion?.TrySetResult(e.FilePath);
    }

    private void Recorder_OnRecordingFailed(object? sender, RecordingFailedEventArgs e)
    {
        _completion?.TrySetException(new InvalidOperationException(e.Error));
        RecordingStopped?.Invoke(this, EventArgs.Empty);
    }

    private void DisposeRecorder()
    {
        lock (_lock)
        {
            if (_recorder is not null)
            {
                _recorder.OnRecordingComplete -= Recorder_OnRecordingComplete;
                _recorder.OnRecordingFailed -= Recorder_OnRecordingFailed;
                _recorder.Dispose();
                _recorder = null;
            }
        }

        foreach (var file in Directory.EnumerateFiles(_bufferFolder, "concat_*.txt"))
        {
            TryDelete(file);
        }
    }

    private void CleanupOldFiles()
    {
        foreach (var file in Directory.EnumerateFiles(_bufferFolder, "replay_*.mp4"))
        {
            TryDelete(file);
        }

        foreach (var file in Directory.EnumerateFiles(_bufferFolder, "audio_*.wav"))
        {
            TryDelete(file);
        }

        foreach (var file in Directory.EnumerateFiles(_bufferFolder, "game_*.wav")
                     .Concat(Directory.EnumerateFiles(_bufferFolder, "chat_*.wav"))
                     .Concat(Directory.EnumerateFiles(_bufferFolder, "microphone_*.wav")))
        {
            TryDelete(file);
        }

        foreach (var file in Directory.EnumerateFiles(_bufferFolder, "stitched_*.mp4"))
        {
            TryDelete(file);
        }
    }

    [DllImport("user32.dll")]
    private static extern bool IsWindow(IntPtr hWnd);

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch
        {
            // Cleanup best effort.
        }
    }

    private static async Task<(int ExitCode, string Output, string Error)> RunProcessAsync(string fileName, IEnumerable<string> args, CancellationToken cancellationToken)
    {
        using var process = StartProcess(fileName, args);
        var outputTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var errorTask = process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken);
        return (process.ExitCode, await outputTask, await errorTask);
    }

    private static Process StartProcess(string fileName, IEnumerable<string> args)
    {
        var info = new ProcessStartInfo(fileName)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            StandardErrorEncoding = Encoding.UTF8,
            StandardOutputEncoding = Encoding.UTF8
        };
        foreach (var arg in args) info.ArgumentList.Add(arg);
        var process = Process.Start(info) ?? throw new InvalidOperationException($"Could not start {fileName}.");
        try
        {
            // Concat/mux ffmpeg runs at full CPU priority by default, competing
            // directly with whatever game is running - normally one quick pass per
            // clip is barely noticeable, but queued auto-clip saves can run several
            // of these back to back, which is what was making the whole system feel
            // sluggish during a clip burst. BelowNormal still gets scheduled
            // whenever a core is actually free, it just yields under contention.
            process.PriorityClass = ProcessPriorityClass.BelowNormal;
        }
        catch
        {
            // Priority is a nice-to-have; never let it block starting the process.
        }

        return process;
    }

    private sealed record ReplayVideoSegment(string Path, DateTime StartedAtUtc, DateTime EndedAtUtc, TimeSpan VideoDuration);

    private static bool IsUsableAudioFile(string path)
    {
        return !string.IsNullOrWhiteSpace(path) && File.Exists(path) && new FileInfo(path).Length > 44;
    }

    private static long AudioFileLength(string path)
    {
        return IsUsableAudioFile(path) ? new FileInfo(path).Length : 0;
    }
}
