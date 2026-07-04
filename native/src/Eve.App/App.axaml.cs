using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Eve.App.Services;
using Eve.App.ViewModels;
using Eve.App.Views;

namespace Eve.App;

public sealed partial class App : Application
{
    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        PlaybackSession.WarmUp();

        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var minimized = desktop.Args?.Any(arg => string.Equals(arg, "--minimized", StringComparison.OrdinalIgnoreCase)) == true;
            desktop.MainWindow = new MainWindow
            {
                DataContext = new MainWindowViewModel(),
                WindowState = minimized ? Avalonia.Controls.WindowState.Minimized : Avalonia.Controls.WindowState.Normal,
                ShowInTaskbar = true
            };
        }

        base.OnFrameworkInitializationCompleted();
    }
}
