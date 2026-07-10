using Eve.Capture.Abstractions;
using System.Diagnostics;
using System.Runtime.Versioning;

namespace Eve.App.Services;

[SupportedOSPlatform("windows")]
public sealed class ObsReplayBuffer : IReplayBuffer
{
    private readonly Func<ReplayBufferConfig> _configProvider;
    private readonly ObsNativeBridge _bridge = new();
    private bool _initialized;

    public ObsReplayBuffer(Func<ReplayBufferConfig> configProvider)
    {
        _configProvider = configProvider;
    }

    public bool IsRecording { get; private set; }
    public TimeSpan Duration { get; private set; } = TimeSpan.FromSeconds(60);
    public event EventHandler? RecordingStopped;

    public Task StartAsync(CancellationToken cancellationToken = default)
    {
        if (IsRecording) return Task.CompletedTask;
        if (!ObsRuntimeLocator.IsAvailable(out var runtime, out var reason))
        {
            throw new InvalidOperationException(reason);
        }

        var config = _configProvider();
        Duration = TimeSpan.FromSeconds(Math.Clamp(config.DurationSeconds, 30, 1200));
        using var process = Process.GetCurrentProcess();
        AppLog.Info($"OBS replay backend starting: pid={Environment.ProcessId}, process={process.ProcessName}, runtime={runtime.RootFolder}, maxHeight={config.MaxHeight}, fps={config.FrameRate}, duration={Duration.TotalSeconds:0}s, game={config.GameExecutableName}, chat={config.ChatAudioProcessName}, mic={config.MicrophoneDeviceName}.");
        try
        {
            _bridge.Initialize(
                runtime.RootFolder,
                config.MaxHeight,
                config.FrameRate,
                (int)Duration.TotalSeconds,
                config.ChatAudioProcessName,
                config.MicrophoneDeviceId,
                config.GameExecutableName,
                config.GameWindowTitle,
                config.GameWindowClass);
            _initialized = true;
            _bridge.StartReplayCapture();
            IsRecording = true;
            AppLog.Info("OBS replay backend started.");
        }
        catch
        {
            CleanupAfterFailedStart();
            throw;
        }

        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken = default)
    {
        if (!IsRecording && !_initialized) return Task.CompletedTask;
        var clock = Stopwatch.StartNew();
        try
        {
            if (IsRecording) _bridge.Stop();
        }
        catch (Exception error)
        {
            AppLog.Error("OBS replay backend stop failed", error);
        }

        try
        {
            if (_initialized) _bridge.Shutdown();
        }
        catch (Exception error)
        {
            AppLog.Error("OBS replay backend shutdown failed", error);
        }
        finally
        {
            IsRecording = false;
            _initialized = false;
            RecordingStopped?.Invoke(this, EventArgs.Empty);
            AppLog.Info($"OBS replay backend stopped in {clock.ElapsedMilliseconds}ms.");
        }

        return Task.CompletedTask;
    }

    public Task<string> SaveReplayAsync(string outputFolder, CancellationToken cancellationToken = default)
    {
        if (!IsRecording) throw new InvalidOperationException("OBS replay buffer is not running.");
        Directory.CreateDirectory(outputFolder);
        var output = _bridge.SaveReplay(outputFolder);
        if (string.IsNullOrWhiteSpace(output)) throw new InvalidOperationException("OBS replay backend returned no output path.");
        AppLog.Info($"OBS replay saved: {output}.");
        return Task.FromResult(output);
    }

    public void SetCapturePaused(bool paused)
    {
        if (!IsRecording) return;
        try
        {
            _bridge.SetCapturePaused(paused);
        }
        catch (Exception error)
        {
            AppLog.Error("OBS capture pause toggle failed", error);
        }
    }

    private void CleanupAfterFailedStart()
    {
        try
        {
            _bridge.Stop();
        }
        catch (Exception error)
        {
            AppLog.Error("OBS replay backend stop after failed start failed", error);
        }

        try
        {
            if (_initialized) _bridge.Shutdown();
        }
        catch (Exception error)
        {
            AppLog.Error("OBS replay backend shutdown after failed start failed", error);
        }
        finally
        {
            _initialized = false;
            IsRecording = false;
        }
    }

    public void Dispose()
    {
        StopAsync().GetAwaiter().GetResult();
    }
}
