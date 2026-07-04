using Eve.Capture.Abstractions;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using ScreenRecorderLib;
using System.Diagnostics;
using System.Runtime.Versioning;
using System.Text;

namespace Eve.App.Services;

[SupportedOSPlatform("windows")]
public sealed class WindowsReplayBuffer : IReplayBuffer, IDisposable
{
    private readonly Func<ReplayBufferConfig> _configProvider;
    private readonly string _bufferFolder;
    private readonly object _lock = new();
    private static readonly TimeSpan SegmentDuration = TimeSpan.FromSeconds(5);
    private Recorder? _recorder;
    private Timer? _rotationTimer;
    private Timer? _audioRouteTimer;
    private string _activePath = string.Empty;
    private ReplayBufferConfig? _config;
    private string _audioRouteKey = string.Empty;
    private DateTime _startedAtUtc;
    private TaskCompletionSource<string>? _completion;
    private readonly SemaphoreSlim _transition = new(1, 1);
    private readonly List<AudioCaptureSession> _audioCaptures = new();
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
        await _transition.WaitAsync(cancellationToken);
        try
        {
            if (!IsRecording) throw new InvalidOperationException("Replay buffer is not running.");
            if (DateTime.UtcNow - _startedAtUtc < TimeSpan.FromSeconds(2) && _segments.Count == 0)
            {
                throw new InvalidOperationException("Replay buffer is still warming up.");
            }

            Directory.CreateDirectory(outputFolder);
            var activeSegment = await StopCurrentRecordingAsync(cancellationToken);
            AddSegment(activeSegment);
            var sourceSegments = GetReplaySegments();
            if (sourceSegments.Length == 0)
            {
                throw new InvalidOperationException("Replay buffer has no finished segments yet.");
            }

            StopAudioCaptures();
            var sourcePath = await BuildReplayVideoAsync(sourceSegments, cancellationToken);
            var outputPath = Path.Combine(outputFolder, $"Replay {DateTime.Now:yyyy-MM-dd HH-mm-ss}.mp4");
            try
            {
                await MuxAudioTracksAsync(sourcePath, outputPath, cancellationToken);
            }
            finally
            {
                TryDelete(sourcePath);
                if (!IsRecording)
                {
                    StartRecorder();
                }

                StartAudioCaptures(_config ?? _configProvider());
                PruneSegments();
            }

            return outputPath;
        }
        finally
        {
            _transition.Release();
        }
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
            _rotationTimer = new Timer(_ => _ = RotateAsync(), null, SegmentDuration, Timeout.InfiniteTimeSpan);
        }
    }

    private async Task RotateAsync()
    {
        if (!await _transition.WaitAsync(0)) return;
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
        recorder.Stop();
        var path = await completion.Task.WaitAsync(TimeSpan.FromSeconds(10), cancellationToken);
        var endedAt = DateTime.UtcNow;
        DisposeRecorder();
        return new ReplayVideoSegment(path, startedAt, endedAt);
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
        _audioRouteTimer = new Timer(_ => RefreshAudioRoutes(), null, TimeSpan.FromSeconds(3), TimeSpan.FromSeconds(3));
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

    private async Task<string> BuildReplayVideoAsync(IReadOnlyList<ReplayVideoSegment> segments, CancellationToken cancellationToken)
    {
        var concatPath = Path.Combine(_bufferFolder, $"concat_{Guid.NewGuid():N}.txt");
        var stitchedPath = Path.Combine(_bufferFolder, $"stitched_{Guid.NewGuid():N}.mp4");
        var trimmedPath = Path.Combine(_bufferFolder, $"trimmed_{Guid.NewGuid():N}.mp4");
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

            var trimResult = await RunProcessAsync(
                "ffmpeg",
                new[] { "-y", "-sseof", $"-{Math.Max(1, Duration.TotalSeconds):0.###}", "-i", stitchedPath, "-c", "copy", trimmedPath },
                cancellationToken);
            if (trimResult.ExitCode != 0)
            {
                throw new InvalidOperationException(string.IsNullOrWhiteSpace(trimResult.Error) ? "ffmpeg trim failed." : trimResult.Error);
            }

            return trimmedPath;
        }
        finally
        {
            TryDelete(concatPath);
            TryDelete(stitchedPath);
        }
    }

    private static string EscapeConcatPath(string path)
    {
        return path.Replace("\\", "/", StringComparison.Ordinal).Replace("'", "'\\''", StringComparison.Ordinal);
    }

    private void StartLoopbackCapture(MMDevice device, string title, string fileName)
    {
        var path = Path.Combine(_bufferFolder, $"{fileName}.wav");
        TryDelete(path);
        var capture = new WasapiLoopbackCapture(device);
        _audioCaptures.Add(AudioCaptureSession.Start(capture, path, title));
    }

    private void StartProcessLoopbackCapture(int processId, ProcessLoopbackCaptureMode mode, string title, string fileName)
    {
        var path = Path.Combine(_bufferFolder, $"{fileName}.wav");
        TryDelete(path);
        var capture = new ProcessLoopbackWaveIn(processId, mode);
        _audioCaptures.Add(AudioCaptureSession.Start(capture, path, title));
    }

    private void StartMicrophoneCapture(MMDevice device, string title, string fileName)
    {
        var path = Path.Combine(_bufferFolder, $"{fileName}.wav");
        TryDelete(path);
        var capture = new WasapiCapture(device);
        _audioCaptures.Add(AudioCaptureSession.Start(capture, path, title));
    }

    private void StopAudioCaptures()
    {
        _audioRouteTimer?.Dispose();
        _audioRouteTimer = null;
        foreach (var capture in _audioCaptures.ToArray())
        {
            capture.Dispose();
        }

        _audioCaptures.Clear();
    }

    private async Task MuxAudioTracksAsync(string videoPath, string outputPath, CancellationToken cancellationToken)
    {
        var duration = Math.Max(1, Duration.TotalSeconds);
        var chatInputs = Directory.EnumerateFiles(_bufferFolder, "chat_*.wav")
            .Where(IsUsableAudioFile)
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var excludedInputs = Directory.EnumerateFiles(_bufferFolder, "excluded_*.wav")
            .Where(IsUsableAudioFile)
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var systemPath = Path.Combine(_bufferFolder, "game.wav");
        var microphonePath = Path.Combine(_bufferFolder, "microphone.wav");
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

        var args = new List<string> { "-y", "-i", videoPath };
        foreach (var input in inputs)
        {
            if (!string.IsNullOrWhiteSpace(input.Path) && IsUsableAudioFile(input.Path))
            {
                args.AddRange(new[] { "-sseof", $"-{duration:0.###}", "-i", input.Path });
            }
            else
            {
                args.AddRange(new[] { "-f", "lavfi", "-t", $"{duration:0.###}", "-i", "anullsrc=channel_layout=stereo:sample_rate=48000" });
            }
        }

        var subtractInputIndexes = Enumerable.Range(2, chatInputs.Length)
            .Concat(Enumerable.Range(excludedStart, excludedInputs.Length))
            .ToArray();

        var filter = new StringBuilder();
        if (subtractInputIndexes.Length > 0)
        {
            filter.Append("[1:a]");
            foreach (var index in subtractInputIndexes) filter.Append($"[{index}:a]");
            filter.Append($"amix=inputs={subtractInputIndexes.Length + 1}:weights='1");
            foreach (var _ in subtractInputIndexes) filter.Append(" -1");
            filter.Append("':normalize=0[gameout];");
        }
        else
        {
            filter.Append("[1:a]anull[gameout];");
        }

        if (chatInputs.Length > 1)
        {
            foreach (var index in Enumerable.Range(chatOutStart, chatInputs.Length)) filter.Append($"[{index}:a]");
            filter.Append($"amix=inputs={chatInputs.Length}:normalize=0[chatout];");
        }
        else
        {
            filter.Append($"[{chatOutStart}:a]anull[chatout];");
        }

        filter.Append($"[{microphoneIndex}:a]anull[micout]");
        args.AddRange(new[] { "-filter_complex", filter.ToString() });
        args.AddRange(new[]
        {
            "-map", "0:v:0",
            "-map", "[gameout]",
            "-map", "[chatout]",
            "-map", "[micout]",
            "-metadata:s:a:0", "title=Game Audio",
            "-metadata:s:a:1", "title=Chat Audio",
            "-metadata:s:a:2", "title=Microphone",
            "-c:v", "copy"
        });
        args.AddRange(new[] { "-c:a", "aac", "-b:a", "192k", "-shortest", outputPath });
        var result = await RunProcessAsync("ffmpeg", args, cancellationToken);
        if (result.ExitCode != 0) throw new InvalidOperationException(string.IsNullOrWhiteSpace(result.Error) ? "ffmpeg mux failed." : result.Error);
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

        foreach (var file in Directory.EnumerateFiles(_bufferFolder, "stitched_*.mp4").Concat(Directory.EnumerateFiles(_bufferFolder, "trimmed_*.mp4")).Concat(Directory.EnumerateFiles(_bufferFolder, "concat_*.txt")))
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

    private sealed record ReplayVideoSegment(string Path, DateTime StartedAtUtc, DateTime EndedAtUtc);

    private static bool IsUsableAudioFile(string path)
    {
        return !string.IsNullOrWhiteSpace(path) && File.Exists(path) && new FileInfo(path).Length > 44;
    }
}
