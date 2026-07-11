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
    private readonly object _lock = new();
    private Recorder? _recorder;
    private Timer? _rotationTimer;
    private Timer? _audioRouteTimer;
    private string _activePath = string.Empty;
    private ReplayBufferConfig? _config;
    private string _audioRouteKey = string.Empty;
    private DateTime _startedAtUtc;
    private TaskCompletionSource<string>? _completion;
    private readonly SemaphoreSlim _transition = new(1, 1);
    private readonly List<ReplayAudioCapture> _audioCaptures = new();
    private readonly List<ReplayVideoSegment> _segments = new();
    private volatile bool _sessionActive;

    public WindowsReplayBuffer(Func<ReplayBufferConfig> configProvider)
    {
        _configProvider = configProvider;
        _bufferFolder = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "EVE",
            "windows-replay-buffer");
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
        StartAudioCaptures(_config);
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
            _audioRouteTimer?.Dispose();
            _audioRouteTimer = null;
            var recorder = _recorder;
            if (recorder is null)
            {
                StopAudioCaptures();
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
            StopAudioCaptures();
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
            var activeSegment = await TryStopCurrentRecordingAsync(cancellationToken);
            try
            {
                if (activeSegment is not null)
                {
                    AddSegment(activeSegment);
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
            }
            finally
            {
                if (IsRecording)
                {
                    StartRecorder();
                }
            }

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
            path = await completion.Task.WaitAsync(TimeSpan.FromSeconds(10), cancellationToken);
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

    private void StartAudioCaptures(ReplayBufferConfig config)
    {
        using var enumerator = new MMDeviceEnumerator();
        var resolvedMicDeviceId = ResolveMicrophoneDeviceId(enumerator, config.MicrophoneDeviceId);
        var routes = ResolveAudioRoutes(config, resolvedMicDeviceId);
        _audioRouteKey = routes.RouteKey;
        AppLog.Info(
            $"Audio route resolved: chat='{config.ChatAudioProcessName}', chatPids={FormatIds(routes.ChatProcessIds)}, exclusions='{string.Join(",", config.GameAudioExcludedProcesses)}', excludedPids={FormatIds(routes.ExcludedProcessIds)}, gamePids={FormatIds(routes.GameProcessIds)}.");
        StopStaleAudioCaptures(routes, resolvedMicDeviceId);
        if (routes.UseProcessRouting)
        {
            foreach (var pid in routes.GameProcessIds)
            {
                if (HasLiveCapture(AudioCaptureKind.Game, pid)) continue;
                try
                {
                    StartProcessLoopbackCapture(AudioCaptureKind.Game, pid, ProcessLoopbackCaptureMode.IncludeTargetProcessTree, "Game Audio");
                }
                catch (Exception error)
                {
                    AppLog.Error($"Game app audio capture failed: pid={pid}", error);
                }
            }

            if (routes.GameProcessIds.Length == 0)
            {
                AppLog.Info("Game audio process routing active but no allowed audio apps found; Game Audio track will be silent.");
            }
        }
        else
        {
            try
            {
                if (!HasLiveCapture(AudioCaptureKind.Game, null))
                {
                    StartLoopbackCapture(enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia), AudioCaptureKind.Game, "Game Audio");
                }
            }
            catch (Exception error)
            {
                AppLog.Error("Game audio capture failed", error);
            }
        }

        foreach (var pid in routes.ChatProcessIds)
        {
            if (HasLiveCapture(AudioCaptureKind.Chat, pid)) continue;
            try
            {
                StartProcessLoopbackCapture(AudioCaptureKind.Chat, pid, ProcessLoopbackCaptureMode.IncludeTargetProcessTree, "Chat Audio");
            }
            catch (Exception error)
            {
                AppLog.Error($"Chat audio capture failed: pid={pid}", error);
            }
        }

        try
        {
            if (!HasLiveCapture(AudioCaptureKind.Microphone, null) && !string.IsNullOrEmpty(resolvedMicDeviceId))
            {
                var micDevice = enumerator.GetDevice(resolvedMicDeviceId);
                StartMicrophoneCapture(micDevice, "Microphone");
            }
        }
        catch (Exception error)
        {
            AppLog.Error("Microphone capture failed", error);
        }

        StartAudioRouteTimer();
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

        foreach (var capture in _audioCaptures.Where(capture => capture.EndedAtUtc is not null && capture.EndedAtUtc < cutoff).ToArray())
        {
            _audioCaptures.Remove(capture);
            TryDelete(capture.Path);
        }
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

    private string SnapshotAudioFile(ReplayAudioCapture? capture, DateTime windowStartUtc, double durationSeconds, ICollection<string> snapshots)
    {
        if (capture is null || !IsUsableAudioFile(capture.Path)) return string.Empty;
        var captureEndUtc = capture.EndedAtUtc ?? DateTime.UtcNow;
        var windowEndUtc = windowStartUtc + TimeSpan.FromSeconds(durationSeconds);
        var overlapStartUtc = capture.StartedAtUtc > windowStartUtc ? capture.StartedAtUtc : windowStartUtc;
        var overlapEndUtc = captureEndUtc < windowEndUtc ? captureEndUtc : windowEndUtc;
        if (overlapEndUtc <= overlapStartUtc) return string.Empty;

        var sourceSnapshotPath = Path.Combine(_bufferFolder, $"audio_source_{Guid.NewGuid():N}.wav");
        var snapshotPath = Path.Combine(_bufferFolder, $"audio_{Guid.NewGuid():N}.wav");
        try
        {
            var copied = capture.EndedAtUtc is null
                ? capture.Session.SnapshotTo(sourceSnapshotPath)
                : CopyAudioFile(capture.Path, sourceSnapshotPath);
            if (!copied || !IsUsableAudioFile(sourceSnapshotPath))
            {
                TryDelete(sourceSnapshotPath);
                TryDelete(snapshotPath);
                return string.Empty;
            }

            snapshots.Add(sourceSnapshotPath);
            var trimStart = Math.Max(0, (overlapStartUtc - capture.StartedAtUtc).TotalSeconds);
            var overlapDuration = Math.Max(0, (overlapEndUtc - overlapStartUtc).TotalSeconds);
            var delayMs = Math.Max(0, (int)Math.Round((overlapStartUtc - windowStartUtc).TotalMilliseconds));
            var filters = $"[0:a]atrim=start={FormatSeconds(trimStart)}:duration={FormatSeconds(overlapDuration)},asetpts=PTS-STARTPTS,aresample=48000,adelay={delayMs}|{delayMs},apad=whole_dur={FormatSeconds(durationSeconds)},atrim=0:{FormatSeconds(durationSeconds)}[out]";
            AppLog.Info($"Replay audio overlap: kind={capture.Kind}, pid={capture.ProcessId?.ToString() ?? "none"}, trim={trimStart:0.###}s, overlap={overlapDuration:0.###}s, delay={delayMs}ms, bytes={AudioFileLength(capture.Path)}.");

            var result = RunProcessAsync("ffmpeg", new[]
            {
                "-y",
                "-v", "error",
                "-i", sourceSnapshotPath,
                "-filter_complex", filters.ToString(),
                "-map", "[out]",
                "-ac", "2",
                "-c:a", "pcm_s16le",
                snapshotPath
            }, CancellationToken.None).GetAwaiter().GetResult();
            if (result.ExitCode != 0 || !IsUsableAudioFile(snapshotPath))
            {
                TryDelete(snapshotPath);
                return string.Empty;
            }

            snapshots.Add(snapshotPath);
            return snapshotPath;
        }
        catch
        {
            TryDelete(sourceSnapshotPath);
            TryDelete(snapshotPath);
            return string.Empty;
        }
    }

    private string SnapshotAudioFile(ReplayAudioCapture capture, ICollection<string> snapshots)
    {
        if (!IsUsableAudioFile(capture.Path)) return string.Empty;
        var snapshotPath = Path.Combine(_bufferFolder, $"audio_{Guid.NewGuid():N}.wav");
        try
        {
            if (!capture.Session.SnapshotTo(snapshotPath) || !IsUsableAudioFile(snapshotPath))
            {
                TryDelete(snapshotPath);
                return string.Empty;
            }

            snapshots.Add(snapshotPath);
            return snapshotPath;
        }
        catch
        {
            TryDelete(snapshotPath);
            return string.Empty;
        }
    }

    private static string FormatDuration(TimeSpan duration)
    {
        return duration.TotalSeconds < 1
            ? "0s"
            : $"{Math.Floor(duration.TotalSeconds):0}s";
    }

    private void StartLoopbackCapture(MMDevice device, AudioCaptureKind kind, string title)
    {
        var path = Path.Combine(_bufferFolder, $"{AudioKindPrefix(kind)}_{Guid.NewGuid():N}.wav");
        TryDelete(path);
        var capture = new WasapiLoopbackCapture(device);
        _audioCaptures.Add(new ReplayAudioCapture(AudioCaptureSession.Start(capture, path, title), path, title, kind, null, DateTime.UtcNow));
        AppLog.Info($"Audio capture started: {title}, device={device.FriendlyName}.");
    }

    private void StartProcessLoopbackCapture(AudioCaptureKind kind, int processId, ProcessLoopbackCaptureMode mode, string title)
    {
        var path = Path.Combine(_bufferFolder, $"{AudioKindPrefix(kind)}_{processId}_{Guid.NewGuid():N}.wav");
        TryDelete(path);
        var capture = new ProcessLoopbackWaveIn(processId, mode);
        _audioCaptures.Add(new ReplayAudioCapture(AudioCaptureSession.Start(capture, path, title), path, title, kind, processId, DateTime.UtcNow));
        AppLog.Info($"Audio capture started: {title}, pid={processId}, mode={mode}.");
    }

    // Resolves to the actual endpoint ID (not the "default" sentinel) so the caller can
    // detect a live default-device swap (e.g. user changes default mic in Windows Sound
    // settings) by comparing this against the device ID the running capture was opened
    // with - WASAPI capture stays bound to whatever device it started on and never
    // follows the system default on its own.
    private static string ResolveMicrophoneDeviceId(MMDeviceEnumerator enumerator, string microphoneDeviceId)
    {
        if (string.IsNullOrWhiteSpace(microphoneDeviceId) || microphoneDeviceId == AudioDeviceOption.DefaultDeviceId)
        {
            try
            {
                return enumerator.GetDefaultAudioEndpoint(DataFlow.Capture, Role.Communications).ID;
            }
            catch
            {
                return string.Empty;
            }
        }

        try
        {
            return enumerator.GetDevice(microphoneDeviceId).ID;
        }
        catch
        {
            return string.Empty;
        }
    }

    private void StartMicrophoneCapture(MMDevice device, string title)
    {
        var path = Path.Combine(_bufferFolder, $"{AudioKindPrefix(AudioCaptureKind.Microphone)}_{Guid.NewGuid():N}.wav");
        TryDelete(path);
        var capture = new WasapiCapture(device);
        _audioCaptures.Add(new ReplayAudioCapture(AudioCaptureSession.Start(capture, path, title), path, title, AudioCaptureKind.Microphone, null, DateTime.UtcNow, device.ID));
        AppLog.Info($"Audio capture started: {title}, device={device.FriendlyName}.");
    }

    private static string AudioKindPrefix(AudioCaptureKind kind) => kind switch
    {
        AudioCaptureKind.Game => "game",
        AudioCaptureKind.Chat => "chat",
        AudioCaptureKind.Microphone => "microphone",
        _ => "audio"
    };

    private bool HasLiveCapture(AudioCaptureKind kind, int? processId)
    {
        return _audioCaptures.Any(capture => capture.EndedAtUtc is null && capture.Kind == kind && capture.ProcessId == processId);
    }

    private void StopStaleAudioCaptures(AudioRoutes routes, string resolvedMicDeviceId)
    {
        var wantedChat = routes.ChatProcessIds.ToHashSet();
        var excluded = routes.ExcludedProcessIds.ToHashSet();
        foreach (var capture in _audioCaptures.Where(capture => capture.EndedAtUtc is null).ToArray())
        {
            var keep = capture.Kind switch
            {
                AudioCaptureKind.Game when routes.UseProcessRouting => capture.ProcessId is int gamePid && !excluded.Contains(gamePid),
                AudioCaptureKind.Game => capture.ProcessId is null,
                AudioCaptureKind.Chat => capture.ProcessId is int chatPid && wantedChat.Contains(chatPid),
                AudioCaptureKind.Microphone => string.Equals(capture.DeviceId, resolvedMicDeviceId, StringComparison.Ordinal),
                _ => false
            };

            if (!keep || (capture.ProcessId is int processId && !IsProcessAlive(processId)))
            {
                StopAudioCapture(capture);
            }
        }
    }

    private static bool IsProcessAlive(int processId)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            return !process.HasExited;
        }
        catch
        {
            return false;
        }
    }

    private static void StopAudioCapture(ReplayAudioCapture capture)
    {
        if (capture.EndedAtUtc is not null) return;
        capture.EndedAtUtc = DateTime.UtcNow;
        try
        {
            capture.Session.Dispose();
        }
        catch
        {
            // Stop best effort.
        }

        AppLog.Info($"Audio capture stopped: {capture.Title}, pid={capture.ProcessId?.ToString() ?? "none"}, start={capture.StartedAtUtc:o}, end={capture.EndedAtUtc:o}, bytes={AudioFileLength(capture.Path)}.");
    }

    private void StopAudioCaptures()
    {
        _audioRouteTimer?.Dispose();
        _audioRouteTimer = null;
        foreach (var capture in _audioCaptures.ToArray())
        {
            StopAudioCapture(capture);
        }

        _audioCaptures.Clear();
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
        var captures = _audioCaptures.ToArray();
        var segmentWindows = BuildSegmentWindows(sourceSegments, videoOffsetSeconds);
        AppLog.Info($"Replay mux start: videoOffset={videoOffsetSeconds:0.###}s, duration={duration:0.###}s, captures={captures.Length}, segments={segmentWindows.Count}.");

        try
        {
            var gamePath = await BuildAlignedTrackAsync(AudioCaptureKind.Game, captures, segmentWindows, allowMix: true, snapshots, cancellationToken);
            var chatPath = await BuildAlignedTrackAsync(AudioCaptureKind.Chat, captures, segmentWindows, allowMix: true, snapshots, cancellationToken);
            var microphonePath = await BuildAlignedTrackAsync(AudioCaptureKind.Microphone, captures, segmentWindows, allowMix: false, snapshots, cancellationToken);
            AppLog.Info($"Replay mux inputs: game={AudioFileLength(gamePath)}b, chat={AudioFileLength(chatPath)}b, microphone={AudioFileLength(microphonePath)}b.");

            var args = new List<string>
            {
                "-y",
                "-i", videoPath,
                "-i", gamePath,
                "-i", chatPath,
                "-i", microphonePath,
                "-filter_complex", $"[0:v]trim=start={FormatSeconds(videoOffsetSeconds)}:duration={FormatSeconds(duration)},setpts=PTS-STARTPTS[vout]",
                "-map", "[vout]",
                "-map", "1:a",
                "-map", "2:a",
                "-map", "3:a",
                "-metadata:s:a:0", "title=Game Audio",
                "-metadata:s:a:1", "title=Chat Audio",
                "-metadata:s:a:2", "title=Microphone",
                "-metadata", $"comment={ClipMetadataTagger.BuildCommentValue("Windows Capture")}",
                "-t", FormatSeconds(duration)
            };
            var baseArgs = args.ToList();
            args.AddRange(BuildNvencVideoArgs(config));
            args.AddRange(new[] { "-c:a", "aac", "-b:a", "192k", outputPath });
            var result = await RunProcessAsync("ffmpeg", args, cancellationToken);
            AppLog.Info($"Replay mux ffmpeg result: exit={result.ExitCode}, output={outputPath}.");
            if (result.ExitCode != 0)
            {
                args = baseArgs.ToList();
                args.AddRange(BuildSoftwareVideoArgs());
                args.AddRange(new[] { "-c:a", "aac", "-b:a", "192k", outputPath });
                result = await RunProcessAsync("ffmpeg", args, cancellationToken);
                AppLog.Info($"Replay mux fallback result: exit={result.ExitCode}, output={outputPath}.");
                if (result.ExitCode != 0) throw new InvalidOperationException(string.IsNullOrWhiteSpace(result.Error) ? "ffmpeg mux failed." : result.Error);
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

    private async Task<string> BuildAlignedTrackAsync(
        AudioCaptureKind kind,
        ReplayAudioCapture[] captures,
        List<(DateTime StartUtc, double DurationSeconds)> segmentWindows,
        bool allowMix,
        List<string> snapshots,
        CancellationToken cancellationToken)
    {
        var segmentClips = new List<string>();
        foreach (var (startUtc, durationSeconds) in segmentWindows)
        {
            var endUtc = startUtc + TimeSpan.FromSeconds(durationSeconds);
            var overlapping = captures
                .Where(capture => capture.Kind == kind && AudioCaptureOverlaps(capture, startUtc, endUtc))
                .ToArray();
            if (!allowMix && overlapping.Length > 1)
            {
                overlapping = new[] { overlapping[^1] };
            }

            var clipPaths = overlapping
                .Select(capture => SnapshotAudioFile(capture, startUtc, durationSeconds, snapshots))
                .Where(IsUsableAudioFile)
                .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
                .ToArray();

            string segmentClip;
            if (clipPaths.Length == 0)
            {
                segmentClip = await CreateSilentClipAsync(durationSeconds, snapshots, cancellationToken);
            }
            else if (clipPaths.Length == 1)
            {
                segmentClip = clipPaths[0];
            }
            else
            {
                segmentClip = await MixClipsAsync(clipPaths, durationSeconds, snapshots, cancellationToken);
            }

            segmentClips.Add(segmentClip);
        }

        if (segmentClips.Count == 0)
        {
            var totalDuration = segmentWindows.Sum(window => window.DurationSeconds);
            return await CreateSilentClipAsync(totalDuration, snapshots, cancellationToken);
        }

        return segmentClips.Count == 1
            ? segmentClips[0]
            : await ConcatClipsAsync(segmentClips, snapshots, cancellationToken);
    }

    private async Task<string> CreateSilentClipAsync(double durationSeconds, ICollection<string> snapshots, CancellationToken cancellationToken)
    {
        var path = Path.Combine(_bufferFolder, $"audio_silent_{Guid.NewGuid():N}.wav");
        var result = await RunProcessAsync("ffmpeg", new[]
        {
            "-y", "-v", "error",
            "-f", "lavfi", "-t", FormatSeconds(durationSeconds), "-i", "anullsrc=channel_layout=stereo:sample_rate=48000",
            "-c:a", "pcm_s16le",
            path
        }, cancellationToken);
        if (result.ExitCode != 0 || !IsUsableAudioFile(path))
        {
            AppLog.Error($"Silent clip generation failed: duration={durationSeconds:0.###}s, error={result.Error}");
        }

        snapshots.Add(path);
        return path;
    }

    private async Task<string> MixClipsAsync(IReadOnlyList<string> clipPaths, double durationSeconds, ICollection<string> snapshots, CancellationToken cancellationToken)
    {
        var path = Path.Combine(_bufferFolder, $"audio_mix_{Guid.NewGuid():N}.wav");
        var args = new List<string> { "-y", "-v", "error" };
        foreach (var clipPath in clipPaths)
        {
            args.AddRange(new[] { "-i", clipPath });
        }

        var labels = string.Concat(Enumerable.Range(0, clipPaths.Count).Select(index => $"[{index}:a]"));
        args.AddRange(new[]
        {
            "-filter_complex", $"{labels}amix=inputs={clipPaths.Count}:normalize=0,atrim=0:{FormatSeconds(durationSeconds)},asetpts=PTS-STARTPTS[out]",
            "-map", "[out]",
            "-c:a", "pcm_s16le",
            path
        });
        var result = await RunProcessAsync("ffmpeg", args, cancellationToken);
        if (result.ExitCode != 0 || !IsUsableAudioFile(path))
        {
            AppLog.Error($"Audio mix failed: clips={clipPaths.Count}, error={result.Error}");
            return await CreateSilentClipAsync(durationSeconds, snapshots, cancellationToken);
        }

        snapshots.Add(path);
        return path;
    }

    private async Task<string> ConcatClipsAsync(IReadOnlyList<string> clipPaths, ICollection<string> snapshots, CancellationToken cancellationToken)
    {
        var concatPath = Path.Combine(_bufferFolder, $"audio_concat_{Guid.NewGuid():N}.txt");
        var outputPath = Path.Combine(_bufferFolder, $"audio_concat_{Guid.NewGuid():N}.wav");
        await File.WriteAllLinesAsync(concatPath, clipPaths.Select(path => $"file '{EscapeConcatPath(path)}'"), cancellationToken);
        try
        {
            var result = await RunProcessAsync("ffmpeg", new[] { "-y", "-v", "error", "-f", "concat", "-safe", "0", "-i", concatPath, "-c", "copy", outputPath }, cancellationToken);
            if (result.ExitCode != 0 || !IsUsableAudioFile(outputPath))
            {
                AppLog.Error($"Audio concat failed: clips={clipPaths.Count}, error={result.Error}");
            }
        }
        finally
        {
            TryDelete(concatPath);
        }

        snapshots.Add(outputPath);
        return outputPath;
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

    private void RefreshAudioRoutes()
    {
        if (!_transition.Wait(0)) return;
        try
        {
            var config = _config;
            if (config is null || !IsRecording) return;
            var latestConfig = _configProvider();
            using var enumerator = new MMDeviceEnumerator();
            var resolvedMicDeviceId = ResolveMicrophoneDeviceId(enumerator, latestConfig.MicrophoneDeviceId);
            var routes = ResolveAudioRoutes(latestConfig, resolvedMicDeviceId);
            if (string.Equals(routes.RouteKey, _audioRouteKey, StringComparison.Ordinal)) return;
            try
            {
                _config = latestConfig;
                AppLog.Info("Audio route changed; restarting replay audio captures.");
                StartAudioCaptures(latestConfig);
            }
            catch (Exception error)
            {
                AppLog.Error("Audio route refresh failed", error);
            }
        }
        finally
        {
            _transition.Release();
        }
    }

    private void StartAudioRouteTimer()
    {
        _audioRouteTimer?.Dispose();
        _audioRouteTimer = new Timer(_ => RefreshAudioRoutes(), null, TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(2));
    }

    private static AudioRoutes ResolveAudioRoutes(ReplayBufferConfig config, string resolvedMicDeviceId)
    {
        // Multi-process apps (Discord, browsers, etc.) share one executable name across
        // several OS processes. StartProcessLoopbackCapture already uses
        // IncludeTargetProcessTree, so capturing more than one PID from the same app
        // captures its audio twice - the reported "chat audio doubled" bug. Only the
        // lowest (typically root/parent) PID is used; its process tree covers the rest.
        var chatPids = ResolveProcessIds(config.ChatAudioProcessName).OrderBy(pid => pid).Take(1).ToArray();
        var useProcessRouting = chatPids.Length > 0 || config.GameAudioExcludedProcesses.Any(name => !string.IsNullOrWhiteSpace(name));
        var exclusionNames = config.GameAudioExcludedProcesses
            .Append(config.ChatAudioProcessName)
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var excludedPids = exclusionNames
            .SelectMany(ResolveProcessIds)
            .Distinct()
            .OrderBy(pid => pid)
            .ToArray();
        var selfPid = Environment.ProcessId;
        var gamePids = useProcessRouting
            ? ResolveActiveAudioProcessIds()
                .Where(pid => pid != selfPid)
                .Except(excludedPids)
                .Distinct()
                .OrderBy(pid => pid)
                .ToArray()
            : Array.Empty<int>();
        var key = $"{useProcessRouting}|{string.Join(',', chatPids.OrderBy(pid => pid))}|{string.Join(',', excludedPids)}|{string.Join(',', gamePids)}|{resolvedMicDeviceId}";
        return new AudioRoutes(chatPids.OrderBy(pid => pid).ToArray(), excludedPids, gamePids, useProcessRouting, key, resolvedMicDeviceId);
    }

    private static string FormatIds(IEnumerable<int> ids)
    {
        var values = ids.ToArray();
        return values.Length == 0 ? "none" : string.Join(",", values);
    }

    private static IEnumerable<int> ResolveProcessIds(string processName)
    {
        var normalized = Path.GetFileNameWithoutExtension(processName ?? string.Empty);
        if (string.IsNullOrWhiteSpace(normalized)) return Array.Empty<int>();
        var ids = new List<int>();
        foreach (var process in Process.GetProcessesByName(normalized))
        {
            using (process)
            {
                try
                {
                    if (!process.HasExited) ids.Add(process.Id);
                }
                catch
                {
                    // Process can exit while enumerating.
                }
            }
        }

        return ids;
    }

    private static IEnumerable<int> ResolveActiveAudioProcessIds()
    {
        var ids = new HashSet<int>();
        try
        {
            using var enumerator = new MMDeviceEnumerator();
            using var device = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
            var sessions = device.AudioSessionManager.Sessions;
            for (var index = 0; index < sessions.Count; index++)
            {
                using var session = sessions[index];
                try
                {
                    if (session.IsSystemSoundsSession) continue;
                    if (session.State != NAudio.CoreAudioApi.Interfaces.AudioSessionState.AudioSessionStateActive) continue;
                    var processId = (int)session.GetProcessID;
                    if (processId <= 0) continue;
                    using var process = Process.GetProcessById(processId);
                    if (!process.HasExited) ids.Add(processId);
                }
                catch
                {
                    // Audio sessions can disappear while enumerating.
                }
            }
        }
        catch (Exception error)
        {
            AppLog.Error("Active audio process resolve failed", error);
        }

        return ids;
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
        return Process.Start(info) ?? throw new InvalidOperationException($"Could not start {fileName}.");
    }

    private sealed record AudioRoutes(int[] ChatProcessIds, int[] ExcludedProcessIds, int[] GameProcessIds, bool UseProcessRouting, string RouteKey, string MicrophoneDeviceId);

    private sealed record ReplayVideoSegment(string Path, DateTime StartedAtUtc, DateTime EndedAtUtc, TimeSpan VideoDuration);

    private enum AudioCaptureKind
    {
        Game,
        Chat,
        Microphone
    }

    private sealed class ReplayAudioCapture
    {
        public ReplayAudioCapture(AudioCaptureSession session, string path, string title, AudioCaptureKind kind, int? processId, DateTime startedAtUtc, string? deviceId = null)
        {
            Session = session;
            Path = path;
            Title = title;
            Kind = kind;
            ProcessId = processId;
            StartedAtUtc = startedAtUtc;
            DeviceId = deviceId;
        }

        public AudioCaptureSession Session { get; }
        public string Path { get; }
        public string Title { get; }
        public AudioCaptureKind Kind { get; }
        public int? ProcessId { get; }
        public string? DeviceId { get; }
        public DateTime StartedAtUtc { get; }
        public DateTime? EndedAtUtc { get; set; }
    }

    private static bool IsUsableAudioFile(string path)
    {
        return !string.IsNullOrWhiteSpace(path) && File.Exists(path) && new FileInfo(path).Length > 44;
    }

    private static long AudioFileLength(string path)
    {
        return IsUsableAudioFile(path) ? new FileInfo(path).Length : 0;
    }

    private static bool AudioCaptureOverlaps(ReplayAudioCapture capture, DateTime windowStartUtc, DateTime windowEndUtc)
    {
        var captureEndUtc = capture.EndedAtUtc ?? DateTime.UtcNow;
        return capture.StartedAtUtc < windowEndUtc && captureEndUtc > windowStartUtc;
    }

    private static bool CopyAudioFile(string source, string destination)
    {
        try
        {
            using var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.Read);
            input.CopyTo(output);
            return true;
        }
        catch
        {
            return false;
        }
    }
}
