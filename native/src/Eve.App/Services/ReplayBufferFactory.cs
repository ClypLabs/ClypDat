using Eve.Capture.Abstractions;

namespace Eve.App.Services;

public static class ReplayBufferFactory
{
    public static IReplayBuffer Create(Func<ReplayBufferConfig> configProvider)
    {
        if (!OperatingSystem.IsWindows()) return new FfmpegReplayBuffer(configProvider);

        var backend = ResolveBackend(configProvider);
        if (backend == ReplayBackendOption.Legacy)
        {
            AppLog.Info("Replay backend selected: Legacy Windows.");
            return new WindowsReplayBuffer(configProvider);
        }

        if (backend == ReplayBackendOption.Obs || ObsRuntimeLocator.IsAvailable(out _, out _))
        {
            AppLog.Info("Replay backend selected: OBS.");
            return new ObsReplayBuffer(configProvider);
        }

        ObsRuntimeLocator.IsAvailable(out _, out var reason);
        AppLog.Info($"Replay backend selected: Legacy Windows. OBS unavailable: {reason}");
        return new WindowsReplayBuffer(configProvider);
    }

    private static ReplayBackendOption ResolveBackend(Func<ReplayBufferConfig> configProvider)
    {
        var config = configProvider();
        return Enum.TryParse<ReplayBackendOption>(config.Backend, ignoreCase: true, out var backend)
            ? backend
            : ReplayBackendOption.Auto;
    }
}
