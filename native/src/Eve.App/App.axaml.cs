using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Media;
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

            InitializeAccentColor();
        }

        base.OnFrameworkInitializationCompleted();
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
