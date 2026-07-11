using Eve.Capture.Abstractions;
using System.Diagnostics;
using System.Runtime.Versioning;

namespace Eve.App.Services;

[SupportedOSPlatform("windows")]
public sealed class ObsReplayBuffer : IReplayBuffer
{
    // OBS's game_capture source has to inject a hook into the target process
    // and wait for it to report frames before real (non-black) video is
    // available; this is a real, observed OBS behaviour (not something EVE
    // controls) and can take up to ~30s depending on the game. A clip saved
    // before the hook has attached is silently all-black, so warn instead of
    // returning a ruined clip.
    private static readonly TimeSpan HookWarmup = TimeSpan.FromSeconds(30);

    private readonly Func<ReplayBufferConfig> _configProvider;
    private readonly ObsNativeBridge _bridge = new();
    private bool _initialized;
    private DateTime _startedAtUtc;

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
                config.GameWindowClass,
                config.GameDisplayName);
            _initialized = true;
            _bridge.StartReplayCapture();
            IsRecording = true;
            _startedAtUtc = DateTime.UtcNow;
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

    public async Task<string> SaveReplayAsync(string outputFolder, CancellationToken cancellationToken = default, string? titleSuffix = null)
    {
        if (!IsRecording) throw new InvalidOperationException("OBS replay buffer is not running.");
        var warmupRemaining = HookWarmup - (DateTime.UtcNow - _startedAtUtc);
        if (warmupRemaining > TimeSpan.Zero)
        {
            throw new InvalidOperationException($"OBS is still hooking into the game, the clip would come out black. Try again in {Math.Ceiling(warmupRemaining.TotalSeconds):0}s.");
        }

        Directory.CreateDirectory(outputFolder);
        var output = _bridge.SaveReplay(outputFolder);
        if (string.IsNullOrWhiteSpace(output)) throw new InvalidOperationException("OBS replay backend returned no output path.");
        AppLog.Info($"OBS replay saved: {output}.");
        output = await ClipMetadataTagger.TagCaptureBackendAsync(output, "OBS", cancellationToken);
        if (!string.IsNullOrWhiteSpace(titleSuffix)) output = AppendTitleSuffix(output, titleSuffix);
        return output;
    }

    // OBS names the file itself when it saves the replay buffer, so the auto-clip
    // event label (e.g. "Ace") can only be applied by renaming after the fact.
    internal static string AppendTitleSuffix(string path, string suffix)
    {
        try
        {
            var directory = Path.GetDirectoryName(path) ?? string.Empty;
            var name = Path.GetFileNameWithoutExtension(path);
            var extension = Path.GetExtension(path);
            var renamed = Path.Combine(directory, $"{name} - {suffix}{extension}");
            File.Move(path, renamed);
            return renamed;
        }
        catch (Exception error)
        {
            AppLog.Error("Auto-clip title rename failed", error);
            return path;
        }
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
