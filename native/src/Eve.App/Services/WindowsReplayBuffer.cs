using Eve.Capture.Abstractions;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using ScreenRecorderLib;
using System.Diagnostics;
using System.Globalization;
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

    public WindowsReplayBuffer(Func<ReplayBufferConfig> configProvider)
    {
        _configProvider = configProvider;
        _bufferFolder = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "EVE",
            "windows-replay-buffer");
    }

    public bool IsRecording => _recorder is not null;
    public TimeSpan Duration { get; private set; } = TimeSpan.FromSeconds(60);
    public event EventHandler? RecordingStopped;

    public Task StartAsync(CancellationToken cancellationToken = default)
    {
        if (IsRecording) return Task.CompletedTask;

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

    public async Task<string> SaveReplayAsync(string outputFolder, CancellationToken cancellationToken = default)
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
                if (!IsRecording)
                {
                    StartRecorder();
                }
            }

            sourcePath = await BuildReplayVideoAsync(sourceSegments, cancellationToken);
            outputPath = Path.Combine(outputFolder, $"Replay {DateTime.Now:yyyy-MM-dd HH-mm-ss}.mp4");
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

    private RecorderOptions CreateOptions(ReplayBufferConfig config)
    {
        var options = RecorderOptions.DefaultMainMonitor;
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
            Framerate = Math.Clamp(config.FrameRate, 15, 60)
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
        var frameRate = Math.Clamp(config.FrameRate, 15, 60);
        var megapixels = height switch
        {
            >= 2160 => 8.3,
            >= 1440 => 3.7,
            >= 1080 => 2.1,
            >= 720 => 0.9,
            _ => 0.4
        };
        return (int)Math.Clamp(megapixels * frameRate * 115_000, 6_000_000, 60_000_000);
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
        var routes = ResolveAudioRoutes(config);
        _audioRouteKey = routes.RouteKey;
        AppLog.Info(
            $"Audio route resolved: chat='{config.ChatAudioProcessName}', chatPids={FormatIds(routes.ChatProcessIds)}, exclusions='{string.Join(",", config.GameAudioExcludedProcesses)}', excludedPids={FormatIds(routes.ExcludedProcessIds)}, gamePids={FormatIds(routes.GameProcessIds)}.");
        using var enumerator = new MMDeviceEnumerator();
        StopStaleAudioCaptures(routes);
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
            if (!string.IsNullOrWhiteSpace(config.MicrophoneDeviceId))
            {
                if (!HasLiveCapture(AudioCaptureKind.Microphone, null))
                {
                    StartMicrophoneCapture(enumerator.GetDevice(config.MicrophoneDeviceId), "Microphone");
                }
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

    private void StartMicrophoneCapture(MMDevice device, string title)
    {
        var path = Path.Combine(_bufferFolder, $"{AudioKindPrefix(AudioCaptureKind.Microphone)}_{Guid.NewGuid():N}.wav");
        TryDelete(path);
        var capture = new WasapiCapture(device);
        _audioCaptures.Add(new ReplayAudioCapture(AudioCaptureSession.Start(capture, path, title), path, title, AudioCaptureKind.Microphone, null, DateTime.UtcNow));
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

    private void StopStaleAudioCaptures(AudioRoutes routes)
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
                AudioCaptureKind.Microphone => true,
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

    private async Task MuxAudioTracksAsync(string videoPath, string outputPath, double videoOffsetSeconds, IReadOnlyList<ReplayVideoSegment> sourceSegments, double clipDurationSeconds, ReplayBufferConfig config, CancellationToken cancellationToken)
    {
        var snapshots = new List<string>();
        var duration = Math.Max(1, clipDurationSeconds);
        var captures = _audioCaptures.ToArray();
        var windowStartUtc = sourceSegments[0].StartedAtUtc + TimeSpan.FromSeconds(videoOffsetSeconds);
        var windowEndUtc = windowStartUtc + TimeSpan.FromSeconds(duration);
        AppLog.Info($"Replay mux start: videoOffset={videoOffsetSeconds:0.###}s, duration={duration:0.###}s, captures={captures.Length}, window={windowStartUtc:o}->{windowEndUtc:o}.");
        var gameInputs = captures
            .Where(capture => capture.Kind == AudioCaptureKind.Game && AudioCaptureOverlaps(capture, windowStartUtc, windowEndUtc))
            .Select(capture => SnapshotAudioFile(capture, windowStartUtc, duration, snapshots))
            .Where(IsUsableAudioFile)
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var chatInputs = captures
            .Where(capture => capture.Kind == AudioCaptureKind.Chat && AudioCaptureOverlaps(capture, windowStartUtc, windowEndUtc))
            .Select(capture => SnapshotAudioFile(capture, windowStartUtc, duration, snapshots))
            .Where(IsUsableAudioFile)
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var microphonePath = SnapshotAudioFile(captures.LastOrDefault(capture => capture.Kind == AudioCaptureKind.Microphone && AudioCaptureOverlaps(capture, windowStartUtc, windowEndUtc)), windowStartUtc, duration, snapshots);
        AppLog.Info($"Replay mux inputs: game={gameInputs.Length}, chat={chatInputs.Length}, microphone={(IsUsableAudioFile(microphonePath) ? 1 : 0)}.");
        AppLog.Info($"Replay mux bytes: game={string.Join(",", gameInputs.Select(AudioFileLength))}, chat={string.Join(",", chatInputs.Select(AudioFileLength))}, microphone={AudioFileLength(microphonePath)}.");
        var inputs = new List<AudioMuxInput>();
        var gameStart = inputs.Count + 1;
        inputs.AddRange(gameInputs.Select((path, index) => new AudioMuxInput($"game{index}", path)));
        if (gameInputs.Length == 0)
        {
            inputs.Add(new AudioMuxInput("gameSilent", string.Empty));
        }

        var chatOutStart = inputs.Count + 1;
        inputs.AddRange(chatInputs.Select((path, index) => new AudioMuxInput($"chatOut{index}", path)));
        if (chatInputs.Length == 0)
        {
            inputs.Add(new AudioMuxInput("chatOutSilent", string.Empty));
        }

        var microphoneIndex = inputs.Count + 1;
        inputs.Add(new AudioMuxInput("microphone", IsUsableAudioFile(microphonePath) ? microphonePath : string.Empty));

        var args = new List<string>
        {
            "-y",
            "-i", videoPath
        };
        foreach (var input in inputs)
        {
            if (!string.IsNullOrWhiteSpace(input.Path) && IsUsableAudioFile(input.Path))
            {
                args.AddRange(new[] { "-i", input.Path });
            }
            else
            {
                args.AddRange(new[] { "-f", "lavfi", "-t", FormatSeconds(duration), "-i", "anullsrc=channel_layout=stereo:sample_rate=48000" });
            }
        }

        try
        {
            var filter = new StringBuilder();
            filter.Append($"[0:v]trim=start={FormatSeconds(videoOffsetSeconds)}:duration={FormatSeconds(duration)},setpts=PTS-STARTPTS[vout];");
            for (var index = 0; index < inputs.Count; index++)
            {
                var inputIndex = index + 1;
                filter.Append($"[{inputIndex}:a]aresample=48000,atrim=0:{FormatSeconds(duration)},asetpts=PTS-STARTPTS,apad=whole_dur={FormatSeconds(duration)}[a{inputIndex}];");
            }

            var gameInputCount = Math.Max(1, gameInputs.Length);
            if (gameInputCount > 1)
            {
                foreach (var index in Enumerable.Range(gameStart, gameInputCount)) filter.Append($"[a{index}]");
                filter.Append($"amix=inputs={gameInputCount}:normalize=0,atrim=0:{FormatSeconds(duration)},asetpts=PTS-STARTPTS[gameout];");
            }
            else
            {
                filter.Append($"[a{gameStart}]atrim=0:{FormatSeconds(duration)},asetpts=PTS-STARTPTS[gameout];");
            }

            if (chatInputs.Length > 1)
            {
                foreach (var index in Enumerable.Range(chatOutStart, chatInputs.Length)) filter.Append($"[a{index}]");
                filter.Append($"amix=inputs={chatInputs.Length}:normalize=0,atrim=0:{FormatSeconds(duration)},asetpts=PTS-STARTPTS[chatout];");
            }
            else
            {
                filter.Append($"[a{chatOutStart}]atrim=0:{FormatSeconds(duration)},asetpts=PTS-STARTPTS[chatout];");
            }

            filter.Append($"[a{microphoneIndex}]atrim=0:{FormatSeconds(duration)},asetpts=PTS-STARTPTS[micout]");
            args.AddRange(new[] { "-filter_complex", filter.ToString() });
            args.AddRange(new[]
            {
                "-map", "[vout]",
                "-map", "[gameout]",
                "-map", "[chatout]",
                "-map", "[micout]",
                "-metadata:s:a:0", "title=Game Audio",
                "-metadata:s:a:1", "title=Chat Audio",
                "-metadata:s:a:2", "title=Microphone",
                "-t", FormatSeconds(duration)
            });
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
        var frameRate = Math.Clamp(config.FrameRate, 15, 60);
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
            var routes = ResolveAudioRoutes(latestConfig);
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

    private static AudioRoutes ResolveAudioRoutes(ReplayBufferConfig config)
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
        var key = $"{useProcessRouting}|{string.Join(',', chatPids.OrderBy(pid => pid))}|{string.Join(',', excludedPids)}|{string.Join(',', gamePids)}";
        return new AudioRoutes(chatPids.OrderBy(pid => pid).ToArray(), excludedPids, gamePids, useProcessRouting, key);
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

    private sealed record AudioMuxInput(string Title, string Path);

    private sealed record AudioRoutes(int[] ChatProcessIds, int[] ExcludedProcessIds, int[] GameProcessIds, bool UseProcessRouting, string RouteKey);

    private sealed record ReplayVideoSegment(string Path, DateTime StartedAtUtc, DateTime EndedAtUtc, TimeSpan VideoDuration);

    private enum AudioCaptureKind
    {
        Game,
        Chat,
        Microphone
    }

    private sealed class ReplayAudioCapture
    {
        public ReplayAudioCapture(AudioCaptureSession session, string path, string title, AudioCaptureKind kind, int? processId, DateTime startedAtUtc)
        {
            Session = session;
            Path = path;
            Title = title;
            Kind = kind;
            ProcessId = processId;
            StartedAtUtc = startedAtUtc;
        }

        public AudioCaptureSession Session { get; }
        public string Path { get; }
        public string Title { get; }
        public AudioCaptureKind Kind { get; }
        public int? ProcessId { get; }
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
