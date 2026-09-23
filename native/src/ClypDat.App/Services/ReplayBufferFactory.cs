using ClypDat.Capture.Abstractions;

namespace ClypDat.App.Services;

public static class ReplayBufferFactory
{

    public static IReplayBuffer Create(Func<ReplayBufferConfig> configProvider)
    {
#if CLYPDAT_UI_PREVIEW
        return new UiPreviewReplayBuffer();
#else
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("ClypDat capture requires the native Windows recorder.");

        return new CaptureWorkerProxy(configProvider);
#endif
    }

    internal static IReplayBuffer CreateLocal(Func<ReplayBufferConfig> configProvider)
    {
#if CLYPDAT_UI_PREVIEW
        return new UiPreviewReplayBuffer();
#else
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("ClypDat replay capture requires the native Windows recorder.");

        AppLog.Info("Replay backend selected: native C++ recorder.");
        return new NativeRecordingAdapter(configProvider);
#endif
    }

    public static ReplayBackendOption ResolveEffectiveBackend(ReplayBufferConfig config)
    {
        return ReplayBackendOption.Native;
    }
}
