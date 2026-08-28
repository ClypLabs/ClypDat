using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Media;
using Avalonia.Platform;
using Avalonia.Threading;
using System.Diagnostics;
using ClypDat.App.Services;
using ClypDat.App.ViewModels;
using ClypDat.App.Views;

namespace ClypDat.App;

public sealed partial class App : Application
{
    private TrayIcon? _trayIcon;
    private MainWindow? _mainWindow;
    private Stream? _trayIconStream;
    private ServerTrayMenuRenderer? _serverTrayMenuRenderer;
    private int _installerShutdownRequested;
    private Color _systemAccent = Color.FromRgb(0x58, 0x64, 0xE8);
    private string _themePreset = "System";

    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        AppLog.Startup();
        AppLog.Info($"Graphics: os={Environment.OSVersion.Version}; {GraphicsOptionsResolver.ActiveDescription}");
        InstallGlobalExceptionHandlers();
        RuntimeHealthWatchdog.Start();
        // Idle sweep for everything the explicit trims miss - a long recording
        // session with no editor activity at all. State comes from
        // MemoryTrimmer's own flags, which the view model pushes; see the
        // comment there for why this is not a callback into the window.
        MemoryTrimmer.Start();
        // Both of these are heavy disk IO with no user waiting on the result -
        // LibVLC's warmup scans its whole plugin directory, and the janitor
        // mass-deletes scratch files (real installs seen with 350MB+ of
        // leftovers). Running either at process start meant paying that cost at
        // the worst possible moment: logon, competing with the OS's own boot IO
        // and every other autostart app.
        //
        StorageJanitor.Initialize();
        _ = Task.Delay(TimeSpan.FromSeconds(45)).ContinueWith(_ => StorageJanitor.CleanupAtStartup(), TaskScheduler.Default);

        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var minimized = desktop.Args?.Any(arg => string.Equals(arg, "--minimized", StringComparison.OrdinalIgnoreCase)) == true;
            // The playback warmup keeps its session now instead of throwing it
            // away (see PlaybackSession.WarmUp), so it is the difference between
            // the first clip click building a whole LibVLC engine - measured at
            // 9-10s cold on a real install - and it reusing one. That makes WHEN
            // it runs matter, and the answer differs by how the app was started.
            //
            // --minimized is the logon autostart: nobody is waiting, and the
            // logon IO burst is real, so stay out of it. A manual launch is the
            // opposite - somebody double-clicked the icon, and the overwhelming
            // reason to do that is to watch a clip. Deferring 12s there just
            // guaranteed the click landed inside the warm-up window and waited
            // out the whole construction, which is exactly what the traces kept
            // showing ("engine ready at 9836ms", clicked at launch+13s).
            if (!UiPreviewMode.Enabled)
            {
                var warmupDelay = minimized ? TimeSpan.FromSeconds(12) : TimeSpan.FromSeconds(1);
                _ = Task.Delay(warmupDelay).ContinueWith(_ => PlaybackSession.WarmUp(), TaskScheduler.Default);
            }
            // The loader is also the startup update check. Autostart still
            // finishes in the tray, but it must not skip that work entirely.
            var useSplash = !UiPreviewMode.Enabled;
            InitializeAccentColor();
            var viewModel = new MainWindowViewModel();
            ApplyTheme(viewModel.Settings.ThemePreset);
            _mainWindow = new MainWindow
            {
                DataContext = viewModel,
                WindowState = WindowState.Normal,
                ShowInTaskbar = !minimized
            };
            _mainWindow.ApplySavedWindowBounds();
            _mainWindow.SetStartupLoaderActive(useSplash);
            if (useSplash) _mainWindow.RaiseStartupCurtain();
            desktop.MainWindow = _mainWindow;
            if (WindowsPlatformProfile.IsServer())
            {
                _serverTrayMenuRenderer = new ServerTrayMenuRenderer("ClypDat");
                desktop.Exit += (_, _) => _serverTrayMenuRenderer?.Dispose();
            }
            InitializeTrayIcon();
            if (useSplash) StartWithSplash(_mainWindow, minimized);
            if (minimized)
            {
                void HideOnFirstOpen(object? _, EventArgs __)
                {
                    _mainWindow.Opened -= HideOnFirstOpen;
                    _mainWindow.Hide();
                }
                _mainWindow.Opened += HideOnFirstOpen;
            }
        }

        base.OnFrameworkInitializationCompleted();
        // Started after the window exists so the first presence describes real
        // state rather than an empty library. Dormant unless enabled AND an
        // application id resolves, so this costs nothing when it is off.
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime discordLifetime)
        {
            discordLifetime.Exit += (_, _) => DiscordRichPresenceService.Shutdown();
            if (discordLifetime.MainWindow?.DataContext is ViewModels.MainWindowViewModel viewModel)
            {
                viewModel.ApplyDiscordSettings();
            }
        }

        if (DevChannelMode.Enabled)
        {
            Dispatcher.UIThread.Post(DevHealthSignal.SignalIfRequested, DispatcherPriority.ApplicationIdle);
            DevUpdateService.StartBackgroundCheck();
        }
    }

    private void StartWithSplash(MainWindow mainWindow, bool finishInTray)
    {
        SplashWindow splash;
        try
        {
            splash = new SplashWindow();
            splash.Show();
        }
        catch (Exception error)
        {
            // Never let the splash be the reason the app does not start.
            AppLog.Error("Startup: splash could not be shown.", error);
            return;
        }

        // Already raised before MainWindow was handed to the desktop lifetime.

        var clock = Stopwatch.StartNew();
        _ = Dispatcher.UIThread.InvokeAsync(async () =>
        {
            try
            {
                var viewModel = mainWindow.DataContext as MainWindowViewModel;
                var essentials = viewModel?.LibraryReadyForRevealTask ?? Task.CompletedTask;
                // Auto-install is a setting, and a version the user already
                // told us to ignore is not one to install behind their back.
                bool ShouldInstall(AppUpdateInfo candidate) =>
                    viewModel?.Settings.InstallUpdatesOnLaunch == true &&
                    !string.Equals(viewModel.Settings.IgnoredUpdateVersion, candidate.TagName, StringComparison.OrdinalIgnoreCase);
                // Handed over rather than dropped: the window's own startup
                // check would otherwise ask GitHub again seconds later.
                mainWindow.PendingStartupUpdate = await splash.RunAsync(
                    essentials,
                    ShouldInstall,
                    () => mainWindow.ExitForUpdateAsync());
            }
            catch (Exception error)
            {
                AppLog.Error("Startup: splash sequence failed.", error);
            }
            finally
            {
                AppLog.Info($"Startup: loader ran for {clock.ElapsedMilliseconds}ms" +
                            (mainWindow.PendingStartupUpdate is { } pending ? $"; update {pending.LatestVersion.ToString(3)} is available." : "."));
                // Loader gets out of the way first, then the app it was
                // loading is uncovered underneath it.
                await splash.FadeOutAndCloseAsync();
                if (finishInTray) mainWindow.FinishStartupInTray();
                else
                {
                    mainWindow.RevealFromStartupLoader();
                    mainWindow.Activate();
                }
                await mainWindow.LiftStartupCurtainAsync();
            }
        });
    }

    // Without this, ANY unhandled exception on the UI thread - a timer tick,
    // a posted continuation, routed input dispatch - takes the whole process
    // down immediately with no chance to log what happened. This is the root
    // cause of "ClypDat crashes when deleting a clip in File Explorer while it's
    // running": the playback timer/audio pipeline can hit a file-not-found/
    // I/O error mid-read when the open clip's file vanishes out from under
    // it, and nothing was catching that. Logging + marking Handled turns a
    // hard crash into a recoverable error instead. AppDomain/TaskScheduler
    // hooks below can't prevent a crash the same way (those fire after the
    // process has already decided to die), but still get the failure logged
    // for a background-thread exception that never reached the UI thread.
    private void InstallGlobalExceptionHandlers()
    {
        Dispatcher.UIThread.UnhandledException += (_, e) =>
        {
            AppLog.Error("Unhandled UI-thread exception - recovered.", e.Exception);
            e.Handled = true;
        };
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        {
            AppLog.Error("Unhandled exception (fatal).", e.ExceptionObject as Exception);
        };
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            AppLog.Error("Unobserved task exception.", e.Exception);
            e.SetObserved();
        };
    }

    private void InitializeAccentColor()
    {
        try
        {
            var settings = PlatformSettings;
            if (settings is null) return;

            _systemAccent = settings.GetColorValues().AccentColor1;
            ApplyTheme(_themePreset);
            settings.ColorValuesChanged += (_, values) =>
            {
                _systemAccent = values.AccentColor1;
                if (AppThemeService.UsesSystemAccent(_themePreset)) ApplyTheme(_themePreset);
            };
        }
        catch (Exception error)
        {
            AppLog.Error("Accent color unavailable, using default", error);
        }
    }

    internal void ApplyTheme(string preset)
    {
        _themePreset = AppThemeService.Normalize(preset);
        AppThemeService.Apply(this, _themePreset, _systemAccent);
    }

    private void ApplyAccentColor(Color accent)
    {
        if (Resources["AccentBrush"] is SolidColorBrush accentBrush) accentBrush.Color = accent;
        if (Resources["AccentBrushHover"] is SolidColorBrush hoverBrush) hoverBrush.Color = BlendWithWhite(accent, 0.18);

        // Selection tints for the sidebar rail. The accent at full strength is
        // far too loud as a backdrop behind an icon, so these are the accent
        // mixed down into the rail's own background - which keeps them
        // following the system colour instead of being fixed indigo shades
        // that only happened to match the default accent.
        if (Resources["AccentSelectedBrush"] is SolidColorBrush selectedBrush) selectedBrush.Color = BlendWith(accent, RailBackground, 0.78);
        if (Resources["AccentSelectedHoverBrush"] is SolidColorBrush selectedHoverBrush) selectedHoverBrush.Color = BlendWith(accent, RailBackground, 0.84);
        if (Resources["AccentSelectedIconBrush"] is SolidColorBrush selectedIconBrush) selectedIconBrush.Color = BlendWithWhite(accent, 0.55);
        // Game and folder hover is intentionally darker than the selected
        // tint, preserving hue while making selection the stronger state.
        if (Resources["AccentGameHoverBrush"] is SolidColorBrush gameHoverBrush) gameHoverBrush.Color = BlendWith(accent, RailBackground, 0.84);
        if (Resources["AccentFolderBrush"] is SolidColorBrush folderBrush) folderBrush.Color = BlendWith(accent, RailBackground, 0.55);
        if (Resources["AccentHoverBrush"] is SolidColorBrush accentHoverBrush) accentHoverBrush.Color = BlendWith(accent, RailBackground, 0.84);
    }

    // #0D1116 - the sidebar rail's background, which selection tints are mixed
    // into so they read as a subtle wash rather than a solid accent block.
    private static readonly Color RailBackground = Color.FromRgb(0x0D, 0x11, 0x16);

    private static Color BlendWithWhite(Color color, double amount)
    {
        byte Blend(byte channel) => (byte)(channel + (255 - channel) * amount);
        return Color.FromArgb(color.A, Blend(color.R), Blend(color.G), Blend(color.B));
    }

    // amount is how far from color toward target: 0 keeps color, 1 gives target.
    private static Color BlendWith(Color color, Color target, double amount)
    {
        byte Blend(byte from, byte to) => (byte)(from + (to - from) * amount);
        return Color.FromArgb(color.A, Blend(color.R, target.R), Blend(color.G, target.G), Blend(color.B, target.B));
    }

    private void InitializeTrayIcon()
    {
        if (_mainWindow is null) return;
        try
        {
            _trayIconStream = AssetLoader.Open(new Uri("avares://ClypDat/Assets/clypdat-icon.ico"));
            var openItem = new NativeMenuItem("Open");
            openItem.Click += (_, _) => RestoreMainWindow();
            var settingsItem = new NativeMenuItem("Settings");
            settingsItem.Click += (_, _) => OpenSettingsFromTray();
            var quitItem = new NativeMenuItem("Quit");
            quitItem.Click += async (_, _) =>
            {
                if (_mainWindow is not null) await _mainWindow.ShutdownCaptureWorkerAsync();
                _trayIcon?.Dispose();
                if (_mainWindow is not null) _mainWindow.AllowRealClose = true;
                if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
                {
                    desktop.Shutdown();
                }
            };
            _trayIcon = new TrayIcon
            {
                Icon = new WindowIcon(_trayIconStream),
                ToolTipText = "ClypDat",
                Menu = new NativeMenu { Items = { openItem, settingsItem, quitItem } }
            };
            _trayIcon.Clicked += (_, _) => RestoreMainWindow();
            _trayIcon.IsVisible = true;
        }
        catch (Exception error)
        {
            AppLog.Error("Tray icon unavailable", error);
        }
    }

    // Called from Program.cs's single-instance listener thread, which is not
    // the UI thread, so this has to marshal over instead of touching the
    // window directly.
    public void ShowMainWindowFromExternalRequest() => Avalonia.Threading.Dispatcher.UIThread.Post(RestoreMainWindow);

    // Called by Program's named-event listener. Multiple installer launches
    // can signal at once, but capture shutdown and desktop lifetime shutdown
    // must happen exactly once.
    public void ShutdownForInstallerRequest() => Dispatcher.UIThread.Post(async () =>
    {
        if (Interlocked.Exchange(ref _installerShutdownRequested, 1) != 0) return;

        AppLog.Info("Installer requested graceful shutdown.");
        try
        {
            if (_mainWindow is not null)
            {
                await _mainWindow.ExitForUpdateAsync();
                return;
            }

            if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
            {
                desktop.Shutdown();
            }
            else
            {
                Environment.Exit(0);
            }
        }
        catch (Exception error)
        {
            AppLog.Error("Installer-requested graceful shutdown failed.", error);
            Environment.Exit(0);
        }
    });

    public void FocusMainWindow() => RestoreMainWindow();

    private void RestoreMainWindow()
    {
        if (_mainWindow is null) return;
        _mainWindow.ShowInTaskbar = true;
        _mainWindow.Show();
        if (_mainWindow.WindowState == WindowState.Minimized) _mainWindow.WindowState = WindowState.Normal;
        _mainWindow.Activate();
    }

    private void OpenSettingsFromTray()
    {
        RestoreMainWindow();
        if (_mainWindow?.DataContext is MainWindowViewModel viewModel) viewModel.OpenSettings();
    }
}
