using Avalonia;
using Eve.App.Services;

namespace Eve.App;

internal static class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        FfmpegPathResolver.EnsureBundledFfmpegOnPath();
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
