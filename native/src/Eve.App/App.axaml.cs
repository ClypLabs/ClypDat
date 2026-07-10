using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Platform;
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
        _ = Task.Run(PlaybackSession.WarmUp);

        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var minimized = desktop.Args?.Any(arg => string.Equals(arg, "--minimized", StringComparison.OrdinalIgnoreCase)) == true;
            _mainWindow = new MainWindow
            {
                DataContext = new MainWindowViewModel(),
                WindowState = WindowState.Normal,
                ShowInTaskbar = !minimized
            };
            desktop.MainWindow = _mainWindow;
            InitializeTrayIcon();
            if (minimized)
            {
                _mainWindow.Opened += (_, _) => _mainWindow.Hide();
            }
        }

        base.OnFrameworkInitializationCompleted();
    }

    private void InitializeTrayIcon()
    {
        if (_mainWindow is null) return;
        try
        {
            _trayIconStream = AssetLoader.Open(new Uri("avares://EVE/Assets/eve-icon.ico"));
            var openItem = new NativeMenuItem("Open EVE");
            openItem.Click += (_, _) => RestoreMainWindow();
            var quitItem = new NativeMenuItem("Quit");
            quitItem.Click += (_, _) =>
            {
                _trayIcon?.Dispose();
                if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
                {
                    desktop.Shutdown();
                }
            };
            _trayIcon = new TrayIcon
            {
                Icon = new WindowIcon(_trayIconStream),
                ToolTipText = "EVE",
                Menu = new NativeMenu { Items = { openItem, quitItem } }
            };
            _trayIcon.Clicked += (_, _) => RestoreMainWindow();
            _trayIcon.IsVisible = true;
        }
        catch (Exception error)
        {
            AppLog.Error("Tray icon unavailable", error);
        }
    }

    private void RestoreMainWindow()
    {
        if (_mainWindow is null) return;
        _mainWindow.ShowInTaskbar = true;
        _mainWindow.Show();
        if (_mainWindow.WindowState == WindowState.Minimized) _mainWindow.WindowState = WindowState.Normal;
        _mainWindow.Activate();
    }
}
