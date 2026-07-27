using ClypDat.Capture.Abstractions;
using System.Diagnostics;
using System.Runtime.Versioning;

namespace ClypDat.App.Services;

[SupportedOSPlatform("windows")]
public sealed class ObsReplayBuffer : IReplayBuffer, IReplayCaptureDiagnostics
{
    private readonly Func<ReplayBufferConfig> _configProvider;
    private readonly ObsBridgeWorker _worker = new();
    private bool _initialized;
    private Timer? _healthTimer;
    private ReplayCaptureHealth _health = ReplayCaptureHealth.Unknown("OBS");

    public ObsReplayBuffer(Func<ReplayBufferConfig> configProvider)
    {
        _configProvider = configProvider;
    }

    public bool IsRecording { get; private set; }
    public TimeSpan Duration { get; private set; } = TimeSpan.FromSeconds(60);
    public event EventHandler? RecordingStopped;
    public event EventHandler<ReplayCaptureHealth>? HealthChanged;

    public ReplayCaptureHealth GetHealthSnapshot() => _health;

    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        if (IsRecording) return;
        if (!ObsRuntimeLocator.IsAvailable(out var runtime, out var reason))
        {
            throw new InvalidOperationException(reason);
        }

        var config = _configProvider();
        Duration = TimeSpan.FromSeconds(Math.Clamp(config.DurationSeconds, 30, 1200));
        using var process = Process.GetCurrentProcess();
        var chatAudioProcessName = config.ChatAudioProcessNames.FirstOrDefault() ?? string.Empty;
        var microphoneDeviceId = config.MicrophoneDeviceIds.FirstOrDefault() ?? string.Empty;
        AppLog.Info($"OBS replay backend starting: pid={Environment.ProcessId}, process={process.ProcessName}, runtime={runtime.RootFolder}, maxHeight={config.MaxHeight}, fps={config.FrameRate}, duration={Duration.TotalSeconds:0}s, game={config.GameExecutableName}, chat={chatAudioProcessName}, mic={config.MicrophoneDeviceName}.");
        try
        {
            var encoder = await _worker.InvokeAsync(bridge =>
            {
                bridge.Initialize(
                    runtime.RootFolder,
                    config.MaxHeight,
                    config.FrameRate,
                    (int)Duration.TotalSeconds,
                    chatAudioProcessName,
                    microphoneDeviceId,
                    config.GameExecutableName,
                    config.GameWindowTitle,
                    config.GameWindowClass,
                    config.GameDisplayName);
                bridge.StartReplayCapture();
                return bridge.GetActiveEncoder();
            }).WaitAsync(cancellationToken);
            _initialized = true;
            IsRecording = true;
            SetHealth(new ReplayCaptureHealth("OBS", "Game hook", ReplayCaptureState.Starting,
                config.FrameRate, 0, 0, 0, 0, 0, 0, encoder, string.Empty,
                "Waiting for game hook frames.", DateTime.UtcNow));
            _healthTimer = new Timer(_ => _ = RefreshHealthAsync(), null, TimeSpan.Zero, TimeSpan.FromSeconds(1));
            AppLog.Info("OBS replay backend started.");
        }
        catch
        {
            await CleanupAfterFailedStartAsync();
            throw;
        }
    }

    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        if (!IsRecording && !_initialized) return;
        _healthTimer?.Dispose();
        _healthTimer = null;
        var clock = Stopwatch.StartNew();
        try
        {
            if (IsRecording) await _worker.InvokeAsync(bridge => bridge.Stop()).WaitAsync(cancellationToken);
        }
        catch (Exception error)
        {
            AppLog.Error("OBS replay backend stop failed", error);
        }

        try
        {
            if (_initialized) await _worker.InvokeAsync(bridge => bridge.Shutdown()).WaitAsync(cancellationToken);
        }
        catch (Exception error)
        {
            AppLog.Error("OBS replay backend shutdown failed", error);
        }
        finally
        {
            IsRecording = false;
            _initialized = false;
            SetHealth(_health with { State = ReplayCaptureState.Stopped, UpdatedUtc = DateTime.UtcNow });
            RecordingStopped?.Invoke(this, EventArgs.Empty);
            AppLog.Info($"OBS replay backend stopped in {clock.ElapsedMilliseconds}ms.");
        }

    }

    public async Task<string> SaveReplayAsync(string outputFolder, CancellationToken cancellationToken = default, string? titleOverride = null, ReplayClipWindow? clipWindow = null)
    {
        if (!IsRecording) throw new InvalidOperationException("OBS replay buffer is not running.");
        var capturePath = await _worker.InvokeAsync(bridge => bridge.GetCapturePath()).WaitAsync(cancellationToken);
        if (capturePath == ObsCapturePath.NoFrames)
        {
            throw new InvalidOperationException("OBS has not received game or window frames yet.");
        }

        Directory.CreateDirectory(outputFolder);
        var output = await _worker.InvokeAsync(bridge => bridge.SaveReplay(outputFolder)).WaitAsync(cancellationToken);
        if (string.IsNullOrWhiteSpace(output)) throw new InvalidOperationException("OBS replay backend returned no output path.");
        AppLog.Info($"OBS replay saved: {output}.");

        if (clipWindow is not null)
        {
            var duration = Math.Max(1, (clipWindow.EndUtc - clipWindow.StartUtc).TotalSeconds);
            var trimmed = Path.Combine(Path.GetDirectoryName(output) ?? outputFolder, $"clypdat-obs-trim-{Guid.NewGuid():N}{Path.GetExtension(output)}");
            var result = await AudioCapturePipeline.RunProcessAsync("ffmpeg", new[]
            {
                "-y", "-sseof", $"-{duration:0.###}", "-i", output,
                "-map", "0", "-c", "copy", trimmed
            }, cancellationToken);
            if (result.ExitCode != 0)
            {
                AudioCapturePipeline.TryDelete(trimmed);
                throw new InvalidOperationException(string.IsNullOrWhiteSpace(result.Error) ? "OBS event-window trim failed." : result.Error);
            }

            File.Move(trimmed, output, overwrite: true);
        }

        output = await ClipMetadataTagger.TagCaptureBackendAsync(output, "OBS", cancellationToken);

        // OBS itself picks the exact save path/filename, so the per-game
        // subfolder can only be applied as a move afterward, not up front like
        // the other backends.
        var config = _configProvider();
        if (!string.IsNullOrWhiteSpace(config.GameDisplayName))
        {
            try
            {
                var gameFolder = Path.Combine(outputFolder, ClipFileNaming.BuildBaseName(config.GameDisplayName));
                Directory.CreateDirectory(gameFolder);
                var relocated = Path.Combine(gameFolder, Path.GetFileName(output));
                File.Move(output, relocated);
                output = relocated;
            }
            catch (Exception error)
            {
                AppLog.Error($"OBS replay: failed moving into per-game folder, leaving at {output}.", error);
            }
        }

        output = ApplyFileNamingScheme(output, string.IsNullOrWhiteSpace(titleOverride) ? config.GameDisplayName : titleOverride, config);

        return output;
    }

    // OBS names the file itself when it saves the replay buffer, so a title
    // override (e.g. "4K - Inferno") can only be applied by renaming after the fact.
    internal static string ApplyFileNamingScheme(string path, string title, ReplayBufferConfig config)
    {
        try
        {
            var directory = Path.GetDirectoryName(path) ?? string.Empty;
            var extension = Path.GetExtension(path).TrimStart('.');
            var renamed = ClipFileNaming.BuildUniquePath(directory, ClipFileNaming.BuildFileName(title, DateTime.Now, extension, config.ClipFileNameScheme, config.CustomClipFileNameTemplate, config.GameDisplayName));
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
        _ = SetCapturePausedAsync(paused);
    }

    private void SetHealth(ReplayCaptureHealth health)
    {
        _health = health;
        HealthChanged?.Invoke(this, health);
    }

    private async Task SetCapturePausedAsync(bool paused)
    {
        try
        {
            await _worker.InvokeAsync(bridge => bridge.SetCapturePaused(paused));
        }
        catch (Exception error)
        {
            AppLog.Error("OBS capture pause toggle failed", error);
        }
    }

    private async Task CleanupAfterFailedStartAsync()
    {
        try
        {
            await _worker.InvokeAsync(bridge => bridge.Stop());
        }
        catch (Exception error)
        {
            AppLog.Error("OBS replay backend stop after failed start failed", error);
        }

        try
        {
            await _worker.InvokeAsync(bridge => bridge.Shutdown());
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

    private async Task RefreshHealthAsync()
    {
        if (!IsRecording) return;
        try
        {
            var status = await _worker.InvokeAsync(bridge => (bridge.GetCapturePath(), bridge.GetActiveEncoder()));
            var (path, state, message) = status.Item1 switch
            {
                ObsCapturePath.GameHook => ("Game hook", ReplayCaptureState.Healthy, string.Empty),
                ObsCapturePath.WindowFallback => ("Window fallback", ReplayCaptureState.Degraded, "Game hook unavailable; OBS window fallback is capturing."),
                _ => ("OBS", ReplayCaptureState.Starting, "Waiting for game or window frames.")
            };
            SetHealth(_health with { CaptureMode = path, State = state, Encoder = status.Item2, LastFailure = message, UpdatedUtc = DateTime.UtcNow });
        }
        catch (Exception error)
        {
            AppLog.Error("OBS capture health query failed", error);
        }
    }

    public void Dispose()
    {
        StopAsync().GetAwaiter().GetResult();
        _worker.Dispose();
    }
}
