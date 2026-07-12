using Avalonia;
using Eve.App.Services;

namespace Eve.App;

internal static class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        FfmpegPathResolver.EnsureBundledFfmpegOnPath();

        // Hidden validation hook for the Phase 1 native capture engine (see plan) -
        // not part of the normal app UI flow, just a quick way to exercise
        // NativeReplayBuffer end-to-end with the real production class before it's
        // wired into Settings/ReplayBufferFactory.
        if (args.Contains("--test-native-capture"))
        {
            NativeReplayBufferSmokeTest.RunAsync().GetAwaiter().GetResult();
            return;
        }

        BuildAvaloniaApp()
            .StartWithClassicDesktopLifetime(args);
    }

    public static AppBuilder BuildAvaloniaApp()
    {
        return AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();
    }
}
