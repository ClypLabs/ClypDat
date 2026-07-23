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
        var chatAudioProcessName = config.ChatAudioProcessNames.FirstOrDefault() ?? string.Empty;
        var microphoneDeviceId = config.MicrophoneDeviceIds.FirstOrDefault() ?? string.Empty;
        AppLog.Info($"OBS replay backend starting: pid={Environment.ProcessId}, process={process.ProcessName}, runtime={runtime.RootFolder}, maxHeight={config.MaxHeight}, fps={config.FrameRate}, duration={Duration.TotalSeconds:0}s, game={config.GameExecutableName}, chat={chatAudioProcessName}, mic={config.MicrophoneDeviceName}.");
        try
        {
            _bridge.Initialize(
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

    public async Task<string> SaveReplayAsync(string outputFolder, CancellationToken cancellationToken = default, string? titleOverride = null, ReplayClipWindow? clipWindow = null)
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

        if (clipWindow is not null)
        {
            var duration = Math.Max(1, (clipWindow.EndUtc - clipWindow.StartUtc).TotalSeconds);
            var trimmed = Path.Combine(Path.GetDirectoryName(output) ?? outputFolder, $"eve-obs-trim-{Guid.NewGuid():N}{Path.GetExtension(output)}");
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
