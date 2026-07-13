using Avalonia;
using Eve.App.Services;

namespace Eve.App;

internal static class Program
{
    // EVE now stays running in the tray after the window closes (see
    // App.axaml.cs's tray icon), which makes it much easier to end up with a
    // second instance if someone closes the window expecting a real quit and
    // relaunches - the second instance's GlobalHotkeyService then fails to
    // RegisterHotKey because the first (hidden) instance already holds it,
    // silently breaking the save-clip hotkey with no obvious cause. A named
    // Mutex enforces one instance; a launch that loses the race just asks the
    // existing instance to show itself and exits immediately instead of
    // starting a second capture pipeline.
    private const string SingleInstanceMutexName = "EVE-Recorder-SingleInstance-9F3D2A61";
    private const string ShowRequestEventName = "EVE-Recorder-ShowRequest-9F3D2A61";

    [STAThread]
    public static void Main(string[] args)
    {
        using var singleInstanceMutex = new Mutex(initiallyOwned: true, SingleInstanceMutexName, out var createdNew);
        if (!createdNew)
        {
            try
            {
                using var showRequest = EventWaitHandle.OpenExisting(ShowRequestEventName);
                showRequest.Set();
            }
            catch (Exception error)
            {
                AppLog.Error("Single-instance: failed to signal the existing EVE instance.", error);
            }

            return;
        }

        using var showRequestListener = new EventWaitHandle(false, EventResetMode.AutoReset, ShowRequestEventName);
        var listenerThread = new Thread(() =>
        {
            while (true)
            {
                showRequestListener.WaitOne();
                if (Application.Current is App app) app.ShowMainWindowFromExternalRequest();
            }
        })
        { IsBackground = true, Name = "EVE Single-Instance Listener" };
        listenerThread.Start();

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
