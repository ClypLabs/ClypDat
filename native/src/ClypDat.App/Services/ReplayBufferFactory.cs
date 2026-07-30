using ClypDat.Capture.Abstractions;

namespace ClypDat.App.Services;

public static class ReplayBufferFactory
{

    public static IReplayBuffer Create(Func<ReplayBufferConfig> configProvider)
    {
        if (!OperatingSystem.IsWindows()) return new FfmpegReplayBuffer(configProvider);

        var backend = ResolveEffectiveBackend(configProvider());
        if (backend == ReplayBackendOption.Auto)
        {
            AppLog.Info("Replay backend selected: Hybrid Auto.");
            return new HybridReplayBuffer(configProvider);
        }
        if (backend == ReplayBackendOption.Legacy)
        {
            AppLog.Info("Replay backend selected: Legacy Windows.");
            return new WindowsReplayBuffer(configProvider);
        }

        if (backend == ReplayBackendOption.Native)
        {
            AppLog.Info("Replay backend selected: Native (ClypDat).");
            return new NativeReplayBuffer(configProvider);
        }

        AppLog.Info("Replay backend selected: Legacy Windows.");
        return new WindowsReplayBuffer(configProvider);
    }

    // Auto is intentionally preserved here. Create chooses HybridAuto so explicit
    // Native/Legacy selections remain deterministic and visible in settings.
    public static ReplayBackendOption ResolveEffectiveBackend(ReplayBufferConfig config)
    {
        return ParseBackend(config.Backend);
    }

    private static ReplayBackendOption ParseBackend(string value)
    {
        return Enum.TryParse<ReplayBackendOption>(value, ignoreCase: true, out var backend)
            ? backend
            : ReplayBackendOption.Auto;
    }
}
