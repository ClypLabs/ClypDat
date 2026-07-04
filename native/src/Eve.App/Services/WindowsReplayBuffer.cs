using Eve.Capture.Abstractions;
using ScreenRecorderLib;

namespace Eve.App.Services;

public sealed class WindowsReplayBuffer : IReplayBuffer, IDisposable
{
    private readonly Func<ReplayBufferConfig> _configProvider;
    private readonly string _bufferFolder;
    private readonly object _lock = new();
    private Recorder? _recorder;
    private Timer? _rotationTimer;
    private string _activePath = string.Empty;
    private string _previousPath = string.Empty;
    private ReplayBufferConfig? _config;
    private DateTime _startedAtUtc;
    private TaskCompletionSource<string>? _completion;
    private readonly SemaphoreSlim _transition = new(1, 1);

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
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        await _transition.WaitAsync(cancellationToken);
        try
        {
            _rotationTimer?.Dispose();
            _rotationTimer = null;
            var recorder = _recorder;
            if (recorder is null) return;
            var completion = _completion;
            recorder.Stop();
            if (completion is not null)
            {
                await completion.Task.WaitAsync(TimeSpan.FromSeconds(10), cancellationToken);
            }

            DisposeRecorder();
            TryDelete(_activePath);
            TryDelete(_previousPath);
            _activePath = string.Empty;
            _previousPath = string.Empty;
        }
        finally
        {
            _transition.Release();
        }
    }

    private async Task StopCurrentForRotateAsync(CancellationToken cancellationToken = default)
    {
        var recorder = _recorder;
        if (recorder is null) return;
        var completion = _completion;
        recorder.Stop();
        if (completion is not null)
        {
            await completion.Task.WaitAsync(TimeSpan.FromSeconds(10), cancellationToken);
        }

        DisposeRecorder();
    }

    public async Task<string> SaveReplayAsync(string outputFolder, CancellationToken cancellationToken = default)
    {
        await _transition.WaitAsync(cancellationToken);
        try
        {
            if (!IsRecording) throw new InvalidOperationException("Replay buffer is not running.");
            if (DateTime.UtcNow - _startedAtUtc < TimeSpan.FromSeconds(2) && string.IsNullOrWhiteSpace(_previousPath))
            {
                throw new InvalidOperationException("Replay buffer is still warming up.");
            }

            Directory.CreateDirectory(outputFolder);
            var sourcePath = DateTime.UtcNow - _startedAtUtc < TimeSpan.FromSeconds(2) && File.Exists(_previousPath)
                ? _previousPath
                : await StopCurrentRecordingAsync(cancellationToken);
            var outputPath = Path.Combine(outputFolder, $"Replay {DateTime.Now:yyyy-MM-dd HH-mm-ss}.mp4");
            File.Copy(sourcePath, outputPath, overwrite: true);
            if (string.Equals(sourcePath, _activePath, StringComparison.OrdinalIgnoreCase))
            {
                TryDelete(sourcePath);
                StartRecorder();
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
            _rotationTimer = new Timer(_ => _ = RotateAsync(), null, Duration, Timeout.InfiniteTimeSpan);
        }
    }

    private async Task RotateAsync()
    {
        if (!await _transition.WaitAsync(0)) return;
        try
        {
            if (!IsRecording) return;
            var completedPath = await StopCurrentRecordingAsync(CancellationToken.None);
            TryDelete(_previousPath);
            _previousPath = completedPath;
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

    private async Task<string> StopCurrentRecordingAsync(CancellationToken cancellationToken)
    {
        var recorder = _recorder ?? throw new InvalidOperationException("Replay buffer is not running.");
        var completion = _completion ?? throw new InvalidOperationException("Replay buffer is not ready.");
        recorder.Stop();
        var path = await completion.Task.WaitAsync(TimeSpan.FromSeconds(10), cancellationToken);
        DisposeRecorder();
        return path;
    }

    private RecorderOptions CreateOptions(ReplayBufferConfig config)
    {
        var options = RecorderOptions.DefaultMainMonitor;
        options.AudioOptions = new AudioOptions
        {
            IsAudioEnabled = true,
            IsInputDeviceEnabled = !string.IsNullOrWhiteSpace(config.MicrophoneDeviceName),
            IsOutputDeviceEnabled = true
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
}
