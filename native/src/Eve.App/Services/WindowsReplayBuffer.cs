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
            var activeSegment = await StopCurrentRecordingAsync(cancellationToken);
            try
            {
                AddSegment(activeSegment);
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
            var segmentDuration = TimeSpan.FromSeconds(Math.Clamp(Duration.TotalSeconds, 30, 1200));
            _rotationTimer = new Timer(_ => _ = RotateAsync(), null, segmentDuration, Timeout.InfiniteTimeSpan);
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
            var completedSegment = await StopCurrentRecordingAsync(CancellationToken.None);
            AddSegment(completedSegment);
            PruneSegments();
            StartRecorder();
        }
        catch
        {
            RecordingStopped?.Invoke(this, EventArgs.Empty);
        }
        finally
        {
            _transition.Release();
        }
    }

    private async Task<ReplayVideoSegment> StopCurrentRecordingAsync(CancellationToken cancellationToken)
    {
        var recorder = _recorder ?? throw new InvalidOperationException("Replay buffer is not running.");
        var completion = _completion ?? throw new InvalidOperationException("Replay buffer is not ready.");
        var startedAt = _startedAtUtc;
        var endedAt = DateTime.UtcNow;
        recorder.Stop();
        var path = await completion.Task.WaitAsync(TimeSpan.FromSeconds(10), cancellationToken);
        DisposeRecorder();
        AppLog.Info($"Replay segment stopped: path={path}, start={startedAt:o}, end={endedAt:o}, duration={(endedAt - startedAt).TotalSeconds:0.###}s.");
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
            IsVideoCaptureEnabled = true
        };
        options.VideoEncoderOptions = new VideoEncoderOptions
        {
            IsHardwareEncodingEnabled = true,
            IsLowLatencyEnabled = true,
            IsThrottlingDisabled = false,
            IsFixedFramerate = true,
            Quality = 70,
            Framerate = Math.Clamp(config.FrameRate, 15, 60)
        };

        return options;
    }

    private void StartAudioCaptures(ReplayBufferConfig config)
    {
        StopAudioCaptures();
        var routes = ResolveAudioRoutes(config);
        _audioRouteKey = routes.RouteKey;
        using var enumerator = new MMDeviceEnumerator();
        try
        {
            StartLoopbackCapture(enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia), "Game Audio", "game");
        }
        catch
        {
            // Game audio is best effort; save path creates silent missing tracks.
        }

        foreach (var pid in routes.ChatProcessIds)
        {
            try
            {
                StartProcessLoopbackCapture(pid, ProcessLoopbackCaptureMode.IncludeTargetProcessTree, "Chat Audio", $"chat_{pid}");
            }
            catch
            {
                // Process audio can fail for protected/exited apps.
            }
        }

        foreach (var pid in routes.ExcludedProcessIds)
        {
            try
            {
                StartProcessLoopbackCapture(pid, ProcessLoopbackCaptureMode.IncludeTargetProcessTree, "Excluded Audio", $"excluded_{pid}");
            }
            catch
            {
                // Process audio can fail for protected/exited apps.
            }
        }

        try
        {
            if (!string.IsNullOrWhiteSpace(config.MicrophoneDeviceId))
            {
                StartMicrophoneCapture(enumerator.GetDevice(config.MicrophoneDeviceId), "Microphone", "microphone");
            }
        }
        catch
        {
            // Microphone capture is best effort.
        }

        _audioRouteTimer?.Dispose();
        _audioRouteTimer = null;
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
                hydrated.Add(segment with { VideoDuration = duration });
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

        AppLog.Info($"ffprobe duration failed: path={path}, exit={result.ExitCode}, error={result.Error}");
        return TimeSpan.Zero;
    }

    private async Task<string> BuildReplayVideoAsync(IReadOnlyList<ReplayVideoSegment> segments, CancellationToken cancellationToken)
    {
        var concatPath = Path.Combine(_bufferFolder, $"concat_{Guid.NewGuid():N}.txt");
        var stitchedPath = Path.Combine(_bufferFolder, $"stitched_{Guid.NewGuid():N}.mp4");
        try
        {
            await File.WriteAllLinesAsync(
                concatPath,
                segments.Select(segment => $"file '{EscapeConcatPath(segment.Path)}'"),
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

    private static string EscapeConcatPath(string path)
    {
        return path.Replace("\\", "/", StringComparison.Ordinal).Replace("'", "'\\''", StringComparison.Ordinal);
    }

    private string SnapshotAudioFile(ReplayAudioCapture? capture, IReadOnlyList<ReplayVideoSegment> segments, double firstVideoOffsetSeconds, double durationSeconds, ICollection<string> snapshots)
    {
        if (capture is null || !IsUsableAudioFile(capture.Path)) return string.Empty;
        var sourceSnapshotPath = Path.Combine(_bufferFolder, $"audio_source_{Guid.NewGuid():N}.wav");
        var snapshotPath = Path.Combine(_bufferFolder, $"audio_{Guid.NewGuid():N}.wav");
        try
        {
            if (!capture.Session.SnapshotTo(sourceSnapshotPath) || !IsUsableAudioFile(sourceSnapshotPath))
            {
                TryDelete(sourceSnapshotPath);
                TryDelete(snapshotPath);
                return string.Empty;
            }

            snapshots.Add(sourceSnapshotPath);
            var filters = new StringBuilder();
            var partCount = 0;
            var remaining = durationSeconds;
            foreach (var segment in segments)
            {
                if (remaining <= 0) break;
                var segmentOffset = partCount == 0 ? firstVideoOffsetSeconds : 0;
                var partDuration = Math.Min(remaining, Math.Max(0, segment.VideoDuration.TotalSeconds - segmentOffset));
                if (partDuration <= 0) continue;
                var segmentAudioStartUtc = segment.EndedAtUtc - segment.VideoDuration + TimeSpan.FromSeconds(segmentOffset);
                var audioOffset = Math.Max(0, (segmentAudioStartUtc - capture.StartedAtUtc).TotalSeconds);
                filters.Append($"[0:a]atrim=start={FormatSeconds(audioOffset)}:duration={FormatSeconds(partDuration)},asetpts=PTS-STARTPTS[p{partCount}];");
                remaining -= partDuration;
                partCount++;
            }

            if (partCount == 0)
            {
                TryDelete(snapshotPath);
                return string.Empty;
            }

            for (var index = 0; index < partCount; index++) filters.Append($"[p{index}]");
            filters.Append($"concat=n={partCount}:v=0:a=1,aresample=48000,apad=whole_dur={FormatSeconds(durationSeconds)},atrim=0:{FormatSeconds(durationSeconds)}[out]");

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

    private static string BuildVideoFilter(ReplayBufferConfig config)
    {
        var height = Math.Clamp(config.MaxHeight, 480, 2160);
        return $"fps={Math.Clamp(config.FrameRate, 15, 60)},scale=-2:{height}:force_original_aspect_ratio=decrease";
    }

    private static string FormatDuration(TimeSpan duration)
    {
        return duration.TotalSeconds < 1
            ? "0s"
            : $"{Math.Floor(duration.TotalSeconds):0}s";
    }

    private void StartLoopbackCapture(MMDevice device, string title, string fileName)
    {
        var path = Path.Combine(_bufferFolder, $"{fileName}.wav");
        TryDelete(path);
        var capture = new WasapiLoopbackCapture(device);
        _audioCaptures.Add(new ReplayAudioCapture(AudioCaptureSession.Start(capture, path, title), path, title, DateTime.UtcNow));
        AppLog.Info($"Audio capture started: {title}, device={device.FriendlyName}.");
    }

    private void StartProcessLoopbackCapture(int processId, ProcessLoopbackCaptureMode mode, string title, string fileName)
    {
        var path = Path.Combine(_bufferFolder, $"{fileName}.wav");
        TryDelete(path);
        var capture = new ProcessLoopbackWaveIn(processId, mode);
        _audioCaptures.Add(new ReplayAudioCapture(AudioCaptureSession.Start(capture, path, title), path, title, DateTime.UtcNow));
        AppLog.Info($"Audio capture started: {title}, pid={processId}, mode={mode}.");
    }

    private void StartMicrophoneCapture(MMDevice device, string title, string fileName)
    {
        var path = Path.Combine(_bufferFolder, $"{fileName}.wav");
        TryDelete(path);
        var capture = new WasapiCapture(device);
        _audioCaptures.Add(new ReplayAudioCapture(AudioCaptureSession.Start(capture, path, title), path, title, DateTime.UtcNow));
        AppLog.Info($"Audio capture started: {title}, device={device.FriendlyName}.");
    }

    private void StopAudioCaptures()
    {
        _audioRouteTimer?.Dispose();
        _audioRouteTimer = null;
        foreach (var capture in _audioCaptures.ToArray())
        {
            capture.Session.Dispose();
        }

        _audioCaptures.Clear();
    }

    private async Task MuxAudioTracksAsync(string videoPath, string outputPath, double videoOffsetSeconds, IReadOnlyList<ReplayVideoSegment> sourceSegments, double clipDurationSeconds, ReplayBufferConfig config, CancellationToken cancellationToken)
    {
        var snapshots = new List<string>();
        var duration = Math.Max(1, clipDurationSeconds);
        var captures = _audioCaptures.ToArray();
        AppLog.Info($"Replay mux start: videoOffset={videoOffsetSeconds:0.###}s, duration={duration:0.###}s, captures={captures.Length}.");
        var chatInputs = captures
            .Where(capture => capture.Path.Contains("\\chat_", StringComparison.OrdinalIgnoreCase) || Path.GetFileName(capture.Path).StartsWith("chat_", StringComparison.OrdinalIgnoreCase))
            .Select(capture => SnapshotAudioFile(capture, sourceSegments, videoOffsetSeconds, duration, snapshots))
            .Where(IsUsableAudioFile)
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var excludedInputs = captures
            .Where(capture => capture.Path.Contains("\\excluded_", StringComparison.OrdinalIgnoreCase) || Path.GetFileName(capture.Path).StartsWith("excluded_", StringComparison.OrdinalIgnoreCase))
            .Select(capture => SnapshotAudioFile(capture, sourceSegments, videoOffsetSeconds, duration, snapshots))
            .Where(IsUsableAudioFile)
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var systemPath = SnapshotAudioFile(captures.FirstOrDefault(capture => string.Equals(Path.GetFileName(capture.Path), "game.wav", StringComparison.OrdinalIgnoreCase)), sourceSegments, videoOffsetSeconds, duration, snapshots);
        var microphonePath = SnapshotAudioFile(captures.FirstOrDefault(capture => string.Equals(Path.GetFileName(capture.Path), "microphone.wav", StringComparison.OrdinalIgnoreCase)), sourceSegments, videoOffsetSeconds, duration, snapshots);
        var inputs = new List<AudioMuxInput>
        {
            new("system", IsUsableAudioFile(systemPath) ? systemPath : string.Empty)
        };
        inputs.AddRange(chatInputs.Select((path, index) => new AudioMuxInput($"chatForGame{index}", path)));
        var chatOutStart = inputs.Count + 1;
        inputs.AddRange(chatInputs.Select((path, index) => new AudioMuxInput($"chatOut{index}", path)));
        if (chatInputs.Length == 0)
        {
            inputs.Add(new AudioMuxInput("chatOutSilent", string.Empty));
        }

        var microphoneIndex = inputs.Count + 1;
        inputs.Add(new AudioMuxInput("microphone", IsUsableAudioFile(microphonePath) ? microphonePath : string.Empty));
        var excludedStart = inputs.Count + 1;
        inputs.AddRange(excludedInputs.Select((path, index) => new AudioMuxInput($"excluded{index}", path)));

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
            var subtractInputIndexes = Enumerable.Range(2, chatInputs.Length)
                .Concat(Enumerable.Range(excludedStart, excludedInputs.Length))
                .ToArray();

            var filter = new StringBuilder();
            filter.Append($"[0:v]trim=start={FormatSeconds(videoOffsetSeconds)}:duration={FormatSeconds(duration)},setpts=PTS-STARTPTS,{BuildVideoFilter(config)}[vout];");
            for (var index = 0; index < inputs.Count; index++)
            {
                var inputIndex = index + 1;
                filter.Append($"[{inputIndex}:a]aresample=48000,atrim=0:{FormatSeconds(duration)},asetpts=PTS-STARTPTS,apad=whole_dur={FormatSeconds(duration)}[a{inputIndex}];");
            }

            if (subtractInputIndexes.Length > 0)
            {
                filter.Append("[a1]");
                foreach (var index in subtractInputIndexes) filter.Append($"[a{index}]");
                filter.Append($"amix=inputs={subtractInputIndexes.Length + 1}:weights='1");
                foreach (var _ in subtractInputIndexes) filter.Append(" -1");
                filter.Append($"':normalize=0,atrim=0:{FormatSeconds(duration)},asetpts=PTS-STARTPTS[gameout];");
            }
            else
            {
                filter.Append($"[a1]atrim=0:{FormatSeconds(duration)},asetpts=PTS-STARTPTS[gameout];");
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
                "-t", FormatSeconds(duration),
                "-c:v", "h264_nvenc",
                "-preset", "p1",
                "-tune", "ull"
            });
            args.AddRange(new[] { "-c:a", "aac", "-b:a", "192k", outputPath });
            var result = await RunProcessAsync("ffmpeg", args, cancellationToken);
            AppLog.Info($"Replay mux ffmpeg result: exit={result.ExitCode}, output={outputPath}.");
            if (result.ExitCode != 0)
            {
                args = args.Select(arg => arg == "h264_nvenc" ? "libx264" : arg).Where(arg => arg is not "-preset" and not "p1" and not "-tune" and not "ull").ToList();
                args.InsertRange(args.Count - 1, new[] { "-preset", "ultrafast" });
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

    private void RefreshAudioRoutes()
    {
        var config = _config;
        if (config is null || !IsRecording) return;
        var routes = ResolveAudioRoutes(config);
        if (string.Equals(routes.RouteKey, _audioRouteKey, StringComparison.Ordinal)) return;
        try
        {
            StartAudioCaptures(config);
        }
        catch
        {
            // Routing refresh is best effort.
        }
    }

    private static AudioRoutes ResolveAudioRoutes(ReplayBufferConfig config)
    {
        var chatPids = ResolveProcessIds(config.ChatAudioProcessName).ToArray();
        var exclusionNames = config.GameAudioExcludedProcesses
            .Append(config.ChatAudioProcessName)
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var excludedPids = exclusionNames
            .SelectMany(ResolveProcessIds)
            .Except(chatPids)
            .Distinct()
            .OrderBy(pid => pid)
            .ToArray();
        var key = $"{string.Join(',', chatPids.OrderBy(pid => pid))}|{string.Join(',', excludedPids)}";
        return new AudioRoutes(chatPids.OrderBy(pid => pid).ToArray(), excludedPids, key);
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

    private sealed record AudioRoutes(int[] ChatProcessIds, int[] ExcludedProcessIds, string RouteKey);

    private sealed record ReplayVideoSegment(string Path, DateTime StartedAtUtc, DateTime EndedAtUtc, TimeSpan VideoDuration);

    private sealed record ReplayAudioCapture(AudioCaptureSession Session, string Path, string Title, DateTime StartedAtUtc);

    private static bool IsUsableAudioFile(string path)
    {
        return !string.IsNullOrWhiteSpace(path) && File.Exists(path) && new FileInfo(path).Length > 44;
    }
}
