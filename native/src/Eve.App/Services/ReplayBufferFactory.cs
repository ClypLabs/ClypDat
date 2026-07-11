using Eve.Capture.Abstractions;

namespace Eve.App.Services;

public static class ReplayBufferFactory
{
    // Games known to fight OBS's game_capture hook (VAC blocks it for CS2 without a
    // launch option, causing a black/frozen capture) default to Windows Capture
    // instead when the user hasn't explicitly picked a backend.
    private static readonly HashSet<string> AutoLegacyGames = new(StringComparer.OrdinalIgnoreCase)
    {
        "cs2.exe",
        "Marvel-Win64-Shipping.exe",
        "FortniteClient-Win64-Shipping.exe",
        "FortniteClient-Win64-Shipping_EAC.exe",
        "FortniteClient-Win64-Shipping_EAC_EOS.exe",
        "helldivers2.exe",
        "forhonor.exe",
        "DeadByDaylight.exe",
        "TheFirstDescendant.exe",
        "cod.exe",
        "cod24-cod.exe",
        "Wuthering Waves.exe",
        "Overwatch.exe",
        "forzahorizon6.exe"
    };

    public static IReplayBuffer Create(Func<ReplayBufferConfig> configProvider)
    {
        if (!OperatingSystem.IsWindows()) return new FfmpegReplayBuffer(configProvider);

        var backend = ResolveEffectiveBackend(configProvider());
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

    public static ReplayBackendOption ResolveEffectiveBackend(ReplayBufferConfig config)
    {
        var backend = ParseBackend(config.Backend);
        if (backend == ReplayBackendOption.Auto && AutoLegacyGames.Contains(config.GameExecutableName))
        {
            return ReplayBackendOption.Legacy;
        }

        return backend;
    }

    private static ReplayBackendOption ParseBackend(string value)
    {
        return Enum.TryParse<ReplayBackendOption>(value, ignoreCase: true, out var backend)
            ? backend
            : ReplayBackendOption.Auto;
    }
}
