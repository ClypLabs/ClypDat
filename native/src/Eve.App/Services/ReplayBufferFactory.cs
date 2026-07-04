using Eve.Capture.Abstractions;

namespace Eve.App.Services;

public static class ReplayBufferFactory
{
    public static IReplayBuffer Create(Func<ReplayBufferConfig> configProvider)
    {
        return OperatingSystem.IsWindows()
            ? new WindowsReplayBuffer(configProvider)
            : new FfmpegReplayBuffer(configProvider);
    }
}
