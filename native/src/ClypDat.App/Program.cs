using Avalonia;
using ClypDat.App.Services;
using ClypDat.Core.Settings;
using Microsoft.Win32;
using SQLitePCL;

namespace ClypDat.App;

internal static class Program
{
    // ClypDat now stays running in the tray after the window closes (see
    // App.axaml.cs's tray icon), which makes it much easier to end up with a
    // second instance if someone closes the window expecting a real quit and
    // relaunches - the second instance's GlobalHotkeyService then fails to
    // RegisterHotKey because the first (hidden) instance already holds it,
    // silently breaking the save-clip hotkey with no obvious cause. A named
    // Mutex enforces one instance; a launch that loses the race just asks the
    // existing instance to show itself and exits immediately instead of
    // starting a second capture pipeline.
    private const string SingleInstanceMutexName = "ClypDat-Recorder-SingleInstance-9F3D2A61";
    private const string ShowRequestEventName = "ClypDat-Recorder-ShowRequest-9F3D2A61";

    [STAThread]
    public static void Main(string[] args)
    {
        // Release packaging calls this after publish. Reaching Main proves the
        // app host loaded ClypDat with its bundled runtime before any user data,
        // capture devices, or UI initialization can have side effects.
        if (args.Contains("--verify-self-contained", StringComparer.Ordinal)) return;

        // Capture worker must stay independent from Avalonia, UI mutex, SQLite,
        // playback warmup, and all other desktop initialization.
        if (args.Contains("--capture-worker", StringComparer.OrdinalIgnoreCase))
        {
            // The worker owns all replay backends, including the native FFmpeg
            // path. Initialize bundled FFmpeg before the worker loads any
            // backend; the normal UI path does this below after its mutex setup.
            FfmpegPathResolver.EnsureBundledFfmpegOnPath();
            CaptureWorkerHost.Run();
            return;
        }

        // Must run before anything else touches %LocalAppData%\ClypDat (AppLog,
        // settings, caches) - one-time migration from the pre-rebrand "EVE" name.
        MigrateFromLegacyEveInstall();

        // Routes Microsoft.Data.Sqlite (MedalImportService's read-only import)
        // through Windows' own winsqlite3.dll instead of a bundled native
        // SQLite binary - see the SQLitePCLRaw.provider.winsqlite3 PackageReference
        // comment in ClypDat.App.csproj for why.
        raw.SetProvider(new SQLite3Provider_winsqlite3());

        var singleInstanceMutex = new Mutex(initiallyOwned: true, SingleInstanceMutexName, out var createdNew);
        var restartRequested = args.Contains("--restart", StringComparer.OrdinalIgnoreCase);
        // An intentional restart can launch with --restart before old process
        // exits, so only that overlap retries mutex acquisition.
        // Ordinary shortcut launches must signal hidden existing process now,
        // not pay this two-second restart-race allowance.
        for (var attempt = 0; restartRequested && attempt < 20 && !createdNew; attempt++)
        {
            Thread.Sleep(100);
            singleInstanceMutex.Dispose();
            singleInstanceMutex = new Mutex(initiallyOwned: true, SingleInstanceMutexName, out createdNew);
        }

        using var _ = singleInstanceMutex;
        if (!createdNew)
        {
            try
            {
                using var showRequest = EventWaitHandle.OpenExisting(ShowRequestEventName);
                showRequest.Set();
            }
            catch (Exception error)
            {
                AppLog.Error("Single-instance: failed to signal the existing ClypDat instance.", error);
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
        { IsBackground = true, Name = "ClypDat Single-Instance Listener" };
        listenerThread.Start();

        FfmpegPathResolver.EnsureBundledFfmpegOnPath();

        // Off-thread, and only after the line above has put the bundled ffmpeg on
        // PATH for it to find. Doing it now means the first export already knows
        // which vendor's encoder works instead of blocking on the probe.
        ExportEncoderProbe.Prewarm();

        BuildAvaloniaApp()
            .StartWithClassicDesktopLifetime(args);
    }

    // One-time migration for the EVE -> ClypDat rebrand: moves the whole
    // settings/logs/cache folder over (rather than leaving existing users
    // looking like a fresh first-run with none of their settings/library
    // path/logs), and re-points the Windows startup registry entry (which
    // StartupService only (re)writes reactively when the user toggles the
    // setting, not proactively on every launch) at the new value name so
    // "launch on startup" doesn't silently stop working after the upgrade.
    // Best-effort throughout - a failure here just means the app behaves like
    // a fresh install rather than crashing.
    private static void MigrateFromLegacyEveInstall()
    {
        try
        {
            var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            var oldFolder = Path.Combine(localAppData, "EVE");
            var newFolder = Path.Combine(localAppData, "ClypDat");
            if (Directory.Exists(oldFolder) && !Directory.Exists(newFolder))
            {
                Directory.Move(oldFolder, newFolder);
            }
        }
        catch
        {
            // Best-effort - see method summary.
        }

        try
        {
            if (!OperatingSystem.IsWindows()) return;
            using var runKey = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run", writable: true);
            if (runKey?.GetValue("EVE") is null) return;

            runKey.DeleteValue("EVE", throwOnMissingValue: false);
            var settings = AppSettingsStore.Load();
            if (settings.LaunchOnWindowsStartup)
            {
                StartupService.SetLaunchOnStartup(true, settings.StartMinimizedToTray);
            }
        }
        catch
        {
            // Best-effort - see method summary.
        }
    }

    public static AppBuilder BuildAvaloniaApp()
    {
        return AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .With(GraphicsOptionsResolver.Resolve())
            .WithInterFont()
            .LogToTrace();
    }
}
