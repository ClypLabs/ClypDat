using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Media;
using Avalonia.Platform;
using Avalonia.Threading;
using System.Diagnostics;
using Eve.App.Services;
using Eve.App.ViewModels;
using Eve.App.Views;

namespace Eve.App;

public sealed partial class App : Application
{
    private TrayIcon? _trayIcon;
    private MainWindow? _mainWindow;
    private Stream? _trayIconStream;

    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        AppLog.Startup();
        InstallGlobalExceptionHandlers();
        _ = Task.Run(PlaybackSession.WarmUp);
        _ = Task.Run(StorageJanitor.CleanupAtStartup);

        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var minimized = desktop.Args?.Any(arg => string.Equals(arg, "--minimized", StringComparison.OrdinalIgnoreCase)) == true;
            _mainWindow = new MainWindow
            {
                DataContext = new MainWindowViewModel(),
                WindowState = WindowState.Normal,
                ShowInTaskbar = !minimized
            };
            _mainWindow.ApplySavedWindowBounds();
            desktop.MainWindow = _mainWindow;
            InitializeTrayIcon();
            if (minimized)
            {
                // Opened re-fires on every subsequent Show() after a Hide(), not
                // just the first launch - must unsubscribe after firing once or
                // this keeps hiding the window every time the user reopens it
                // from the tray.
                void HideOnFirstOpen(object? _, EventArgs __)
                {
                    _mainWindow.Opened -= HideOnFirstOpen;
                    _mainWindow.Hide();
                }
                _mainWindow.Opened += HideOnFirstOpen;
            }

            InitializeAccentColor();
        }

        base.OnFrameworkInitializationCompleted();
    }

    // Without this, ANY unhandled exception on the UI thread - a timer tick,
    // a posted continuation, routed input dispatch - takes the whole process
    // down immediately with no chance to log what happened. This is the root
    // cause of "EVE crashes when deleting a clip in File Explorer while it's
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

            ApplyAccentColor(settings.GetColorValues().AccentColor1);
            settings.ColorValuesChanged += (_, values) => ApplyAccentColor(values.AccentColor1);
        }
        catch (Exception error)
        {
            AppLog.Error("Accent color unavailable, using default", error);
        }
    }

    private void ApplyAccentColor(Color accent)
    {
        if (Resources["AccentBrush"] is SolidColorBrush accentBrush) accentBrush.Color = accent;
        if (Resources["AccentBrushHover"] is SolidColorBrush hoverBrush) hoverBrush.Color = BlendWithWhite(accent, 0.18);
    }

    private static Color BlendWithWhite(Color color, double amount)
    {
        byte Blend(byte channel) => (byte)(channel + (255 - channel) * amount);
        return Color.FromArgb(color.A, Blend(color.R), Blend(color.G), Blend(color.B));
    }

    private void InitializeTrayIcon()
    {
        if (_mainWindow is null) return;
        try
        {
            _trayIconStream = AssetLoader.Open(new Uri("avares://EVE/Assets/eve-icon.ico"));
            var openItem = new NativeMenuItem("Open");
            openItem.Click += (_, _) => RestoreMainWindow();
            var settingsItem = new NativeMenuItem("Settings");
            settingsItem.Click += (_, _) => OpenSettingsFromTray();
            var quitItem = new NativeMenuItem("Quit");
            quitItem.Click += (_, _) =>
            {
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
                ToolTipText = "EVE",
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
