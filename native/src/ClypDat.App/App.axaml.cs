using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Media;
using Avalonia.Media.Fonts;
using Avalonia.Platform;
using Avalonia.Threading;
using System.Diagnostics;
using ClypDat.App.Services;
using ClypDat.App.ViewModels;
using ClypDat.App.Views;
using ClypDat.Core.Settings;

namespace ClypDat.App;

public sealed partial class App : Application
{
    private static readonly Uri WindowsFontCollectionKey = new("fonts:ClypDatWindows");
    private static readonly Uri SelectedWindowsFontCollectionKey = new("fonts:ClypDatSelectedWindowsFont");
    private static IFontCollection? _windowsFontCollection;
    private static string? _windowsFontsPath;
    private TrayIcon? _trayIcon;
    private MainWindow? _mainWindow;
    private ServerTrayMenuRenderer? _serverTrayMenuRenderer;
    private int _installerShutdownRequested;
    private Color _systemAccent = Color.FromRgb(0x58, 0x64, 0xE8);
    private string _themePreset = "System";
    private bool _useSystemAccent = true;
    private CustomThemeSettings? _customTheme;

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
            var viewModel = new MainWindowViewModel();
            var launchPresentation = ResolveLaunchPresentation(desktop.Args, viewModel);
            var minimized = LaunchPresentationPolicy.StartsInTray(launchPresentation);
            // LibVLC construction is deferred until the editor asks for it.
            // Starting its native plugin scan during app launch can terminate
            // the process before the main window is ready.
            // The loader is also the startup update check. Autostart still
            // finishes in the tray, but it must not skip that work entirely.
            var useSplash = !UiPreviewMode.Enabled && LaunchPresentationPolicy.UsesStartupLoader(launchPresentation);
            // Before the first theme apply, so the first paint already carries
            // the chosen mark rather than flashing the other one.
            AppThemeService.UseClassicLogo = viewModel.Settings.UseClassicLogo;
            InitializeAccentColor();
            ApplyTheme(viewModel.Settings.ThemePreset, viewModel.Settings.UseSystemAccent,
                viewModel.Settings.CustomThemes.FirstOrDefault(theme => string.Equals(CustomThemeLibrary.Selection(theme), viewModel.Settings.ThemePreset, StringComparison.OrdinalIgnoreCase)));
            ApplyFontFamily(viewModel.Settings.FontFamilyName);
            _mainWindow = new MainWindow
            {
                DataContext = viewModel,
                WindowState = WindowState.Normal,
                ShowInTaskbar = !minimized,
                // Never let initial Window.Show steal focus. Normal launches
                // explicitly activate after their interactive loader finishes;
                // restart/minimized launches stay passive until tray/user input.
                ShowActivated = false
            };
            // The XAML icon is the build-time one; the running window follows the
            // chosen mark so the taskbar button switches with it.
            _mainWindow.Icon = AppThemeService.CreateWindowIcon();
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
            if (useSplash) StartWithSplash(_mainWindow, launchPresentation);
            else if (!UiPreviewMode.Enabled) _ = _mainWindow.PreparePlaybackAsync();
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

    private static LaunchPresentation ResolveLaunchPresentation(
        IEnumerable<string>? arguments,
        MainWindowViewModel viewModel)
    {
        if (!LaunchPresentationPolicy.RequiresForegroundGameCheck(arguments))
            return LaunchPresentationPolicy.Resolve(arguments);

        try
        {
            var detector = new ForegroundGameDetector();
            detector.ApplyCustomGameNames(viewModel.Settings.GameCaptureOverrides);
            detector.ApplyUserIgnoredExecutables(viewModel.Settings.IgnoredGameExecutables);
            var game = detector.Detect();
            AppLog.Info(game.IsForeground
                ? $"Publish restart: foreground game detected ({game.DisplayName}); starting in tray."
                : "Publish restart: no foreground game detected; showing startup loader.");
            return LaunchPresentationPolicy.Resolve(arguments, foregroundGameDetected: game.IsForeground);
        }
        catch (Exception error)
        {
            AppLog.Error("Publish restart: foreground game detection failed; starting in tray.", error);
            return LaunchPresentationPolicy.Resolve(arguments, foregroundGameDetectionFailed: true);
        }
    }

    private void StartWithSplash(MainWindow mainWindow, LaunchPresentation launchPresentation)
    {
        SplashWindow splash;
        try
        {
            // The splash has its own taskbar button; without an icon of its own it
            // would show the exe's baked-in mark regardless of the chosen one.
            splash = new SplashWindow { Icon = AppThemeService.CreateWindowIcon() };
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
                    mainWindow.PreparePlaybackAsync(),
                    ShouldInstall,
                    message => mainWindow.StartupPlaybackWarning = message,
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
                var splashOwnsForeground = await splash.FadeOutAndCloseAsync();
                if (LaunchPresentationPolicy.StartsInTray(launchPresentation)) mainWindow.FinishStartupInTray();
                else
                {
                    mainWindow.RevealFromStartupLoader();
                    if (LaunchPresentationPolicy.ActivatesAfterStartupLoader(launchPresentation) && splashOwnsForeground) mainWindow.Activate();
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
            ApplyTheme(_themePreset, _useSystemAccent);
            // Only worth repainting while the accent is actually sourced from
            // Windows; with the toggle off the preset's own accent is in use and
            // a system colour change means nothing to us.
            settings.ColorValuesChanged += (_, values) =>
            {
                _systemAccent = values.AccentColor1;
                if (_useSystemAccent) ApplyTheme(_themePreset, _useSystemAccent);
            };
        }
        catch (Exception error)
        {
            AppLog.Error("Accent color unavailable, using default", error);
        }
    }

    internal void ApplyTheme(string preset, bool useSystemAccent, CustomThemeSettings? customTheme = null)
    {
        _customTheme = customTheme;
        _themePreset = customTheme is null ? AppThemeService.Normalize(preset) : preset;
        _useSystemAccent = customTheme is null && useSystemAccent;
        AppThemeService.Apply(this, _themePreset, _systemAccent, _useSystemAccent, _customTheme);
    }

    /// <summary>
    /// Switches between the current mark and the original hexagon one, everywhere
    /// at once: every in-app logo through the theme's dynamic resources, and the
    /// window, taskbar and tray icons directly.
    /// </summary>
    internal void ApplyLogoStyle(bool classic)
    {
        AppThemeService.SetClassicLogo(this, classic);
        if (_mainWindow is not null) _mainWindow.Icon = AppThemeService.CreateWindowIcon();
        if (_trayIcon is not null) _trayIcon.Icon = AppThemeService.CreateWindowIcon();
    }

    internal void ApplyFontFamily(string? fontFamilyName)
    {
        var name = string.IsNullOrWhiteSpace(fontFamilyName) ? "Inter" : fontFamilyName.Trim();
        FontFamily fontFamily;

        try
        {
            fontFamily = ResolveFontFamily(name);
        }
        catch (Exception error)
        {
            AppLog.Error($"Font family '{name}' could not be applied; using Inter.", error);
            fontFamily = new FontFamily("fonts:Inter#Inter, $Default");
        }

        Resources["ClypDatFontFamily"] = fontFamily;
        if (_mainWindow is not null) _mainWindow.FontFamily = fontFamily;
    }

    private static FontFamily ResolveFontFamily(string name)
    {
        if (string.Equals(name, "Inter", StringComparison.OrdinalIgnoreCase))
            return new FontFamily("fonts:Inter#Inter, $Default");

        var collection = GetWindowsFontCollection();
        var family = collection?.FirstOrDefault(candidate =>
            string.Equals(candidate.Name, name, StringComparison.OrdinalIgnoreCase));
        if (family is not null)
            return new FontFamily($"{WindowsFontCollectionKey}#{family.Name}, $Default");

        var file = FindWindowsFontFile(name);
        if (file is null) return new FontFamily("fonts:Inter#Inter, $Default");

        var selected = new InstalledFontCollection(SelectedWindowsFontCollectionKey, new Uri(file));
        if (selected.Count == 0)
        {
            ((IFontCollection)selected).Dispose();
            return new FontFamily("fonts:Inter#Inter, $Default");
        }

        FontManager.Current.AddFontCollection(selected);
        return new FontFamily($"{SelectedWindowsFontCollectionKey}#{selected[0].Name}, $Default");
    }

    private static IFontCollection? GetWindowsFontCollection()
    {
        if (_windowsFontCollection is not null) return _windowsFontCollection;

        var windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        var folder = Path.Combine(string.IsNullOrWhiteSpace(windows) ? @"C:\Windows" : windows, "Fonts");
        if (!Directory.Exists(folder)) return null;

        var collection = new InstalledFontCollection(
            WindowsFontCollectionKey,
            new Uri(Path.TrimEndingDirectorySeparator(folder) + Path.DirectorySeparatorChar));
        FontManager.Current.AddFontCollection(collection);
        _windowsFontsPath = folder;
        _windowsFontCollection = collection;
        return collection;
    }

    private static string? FindWindowsFontFile(string name)
    {
        _ = GetWindowsFontCollection();
        if (_windowsFontsPath is null) return null;

        return Directory.EnumerateFiles(_windowsFontsPath)
            .Where(path => Path.GetExtension(path) is { } extension &&
                           (extension.Equals(".ttf", StringComparison.OrdinalIgnoreCase) ||
                            extension.Equals(".otf", StringComparison.OrdinalIgnoreCase) ||
                            extension.Equals(".ttc", StringComparison.OrdinalIgnoreCase)))
            .FirstOrDefault(path =>
                string.Equals(Path.GetFileName(path), name, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(Path.GetFileNameWithoutExtension(path), name, StringComparison.OrdinalIgnoreCase));
    }

    private void InitializeTrayIcon()
    {
        if (_mainWindow is null) return;
        try
        {
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
                Icon = AppThemeService.CreateWindowIcon(),
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
