using ClypDat.Capture.Abstractions;

namespace ClypDat.App.Services;

public static class ReplayBufferFactory
{

    public static IReplayBuffer Create(Func<ReplayBufferConfig> configProvider)
    {
#if CLYPDAT_UI_PREVIEW
        return new UiPreviewReplayBuffer();
#else
        if (!OperatingSystem.IsWindows()) return new FfmpegReplayBuffer(configProvider);

        return new CaptureWorkerProxy(configProvider);
#endif
    }

    internal static IReplayBuffer CreateLocal(Func<ReplayBufferConfig> configProvider)
    {
#if CLYPDAT_UI_PREVIEW
        return new UiPreviewReplayBuffer();
#else
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("ClypDat replay capture requires Windows DXGI Desktop Duplication.");

        AppLog.Info("Replay backend selected: DXGI Desktop Duplication.");
        return new NativeReplayBuffer(configProvider);
#endif
    }

    public static ReplayBackendOption ResolveEffectiveBackend(ReplayBufferConfig config)
    {
        return ReplayBackendOption.Native;
    }
}
