using Eve.Capture.Abstractions;

namespace Eve.App.Services;

public static class ReplayBufferFactory
{

    public static IReplayBuffer Create(Func<ReplayBufferConfig> configProvider)
    {
        if (!OperatingSystem.IsWindows()) return new FfmpegReplayBuffer(configProvider);

        var backend = ResolveEffectiveBackend(configProvider());
        if (backend == ReplayBackendOption.Legacy)
        {
            AppLog.Info("Replay backend selected: Legacy Windows.");
            return new WindowsReplayBuffer(configProvider);
        }

        if (backend == ReplayBackendOption.Native)
        {
            AppLog.Info("Replay backend selected: Native (EVE).");
            return new NativeReplayBuffer(configProvider);
        }

        if (ObsRuntimeLocator.IsAvailable(out _, out _))
        {
            AppLog.Info("Replay backend selected: OBS.");
            return new ObsReplayBuffer(configProvider);
        }

        ObsRuntimeLocator.IsAvailable(out _, out var reason);
        AppLog.Info($"Replay backend selected: Legacy Windows. OBS unavailable: {reason}");
        return new WindowsReplayBuffer(configProvider);
    }

    // Auto always means EVE (Native) - it never hooks the game process, so unlike
    // OBS it has nothing for anti-cheat to block or object to, and unlike Legacy
    // (ScreenRecorderLib's stop/start segment rotation) it has no gap at rotation
    // boundaries. Only an explicit Obs/Legacy/Native choice overrides this.
    public static ReplayBackendOption ResolveEffectiveBackend(ReplayBufferConfig config)
    {
        var backend = ParseBackend(config.Backend);
        return backend == ReplayBackendOption.Auto ? ReplayBackendOption.Native : backend;
    }

    private static ReplayBackendOption ParseBackend(string value)
    {
        return Enum.TryParse<ReplayBackendOption>(value, ignoreCase: true, out var backend)
            ? backend
            : ReplayBackendOption.Auto;
    }
}
