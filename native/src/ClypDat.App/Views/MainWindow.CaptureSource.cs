using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Interactivity;
using ClypDat.App.Services;

namespace ClypDat.App.Views;

// The capture-source chooser. Picking what the buffer records is a
// Game-or-Desktop decision and then, for Desktop, which display - too much for
// the combo box the flyout used to carry, so it gets a dialog with preview
// tiles instead.
public sealed partial class MainWindow
{
    private const string GameCaptureSource = "Game Capture";
    private const string DesktopCaptureSource = "Desktop Capture";

    // The status card does double duty: with a game detected it offers the same
    // detection menu the header's game label does, and without one it takes the
    // user to the page where an unlisted game gets added - because
    // ActiveGameButton_OnClick deliberately does nothing when nothing is detected.
    private void ReplayStatusCard_OnClick(object? sender, RoutedEventArgs e)
    {
        if (ViewModel is not { } model) return;
        if (model.ActiveGameDetection.IsDetected)
        {
            ActiveGameButton_OnClick(sender, e);
            return;
        }

        model.SelectSettingsSection("Game Detection");
        model.OpenSettings();
    }

    private async void CaptureSourceRow_OnClick(object? sender, RoutedEventArgs e)
    {
        try
        {
            if (ViewModel is null) return;
            // Displays come and go while the app is running; the collection is
            // otherwise only built once, in the view model's constructor.
            ViewModel.RefreshDesktopMonitors();
            await ShowCaptureSourceDialogAsync();
        }
        catch (Exception error)
        {
            AppLog.Error("Capture source chooser failed", error);
        }
    }

    private async Task ShowCaptureSourceDialogAsync()
    {
        if (ViewModel is not { } model) return;

        var (window, body) = CreateChromelessDialog("Capture source");
        window.Width = 680;
        if (body is StackPanel bodyStack) bodyStack.Spacing = 18;

        var startedOnDesktop = model.IsDesktopCapture;
        var selectedMonitor = model.SelectedDesktopMonitor ?? model.DesktopMonitors.FirstOrDefault();
        var desktopChosen = startedOnDesktop;

        var gameTab = CreateSourceTab("Game");
        var desktopTab = CreateSourceTab("Desktop");

        var gamePane = BuildGamePane(model);
        var desktopPane = new StackPanel { Spacing = 14 };
        var tiles = new List<(Button Button, DesktopMonitorOption Monitor)>();
        var tileRow = new WrapPanel { Orientation = Orientation.Horizontal };

        foreach (var monitor in model.DesktopMonitors)
        {
            var tile = BuildMonitorTile(monitor);
            tiles.Add((tile, monitor));
            tileRow.Children.Add(tile);
        }

        void PaintTiles()
        {
            foreach (var (button, monitor) in tiles)
            {
                var isSelected = selectedMonitor is not null &&
                                 string.Equals(monitor.DeviceName, selectedMonitor.DeviceName, StringComparison.OrdinalIgnoreCase);
                // The ring is an accent brush, not a fixed colour, so it follows
                // whatever theme the app is wearing.
                button.BorderBrush = isSelected
                    ? AppThemeService.Brush("AccentBrush", "#5864E8")
                    : AppThemeService.Brush("Surface_263846", "#263846");
            }
        }

        foreach (var (button, monitor) in tiles)
        {
            button.Click += (_, _) =>
            {
                selectedMonitor = monitor;
                desktopChosen = true;
                PaintTiles();
                PaintTabs();
            };
        }

        PaintTiles();
        desktopPane.Children.Add(tileRow);
        desktopPane.Children.Add(BuildDesktopToggles(model));
        desktopPane.Children.Add(BuildNotice("Desktop capture records everything on this display, including anything you would rather not share."));

        void PaintTabs()
        {
            gameTab.Classes.Set("sourceTabActive", !desktopChosen);
            desktopTab.Classes.Set("sourceTabActive", desktopChosen);
            gamePane.IsVisible = !desktopChosen;
            desktopPane.IsVisible = desktopChosen;
        }

        gameTab.Click += (_, _) => { desktopChosen = false; PaintTabs(); };
        desktopTab.Click += (_, _) => { desktopChosen = true; PaintTabs(); };
        PaintTabs();

        var tabStrip = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Center,
            Spacing = 8,
            Children = { gameTab, desktopTab }
        };

        var apply = new Button
        {
            Classes = { "primaryButton" },
            Content = "Use this source",
            MinWidth = 150,
            Height = 36,
            Padding = new Thickness(16, 0),
            HorizontalContentAlignment = HorizontalAlignment.Center,
            VerticalContentAlignment = VerticalAlignment.Center
        };
        var close = new Button
        {
            Content = "Close",
            Width = 100,
            Height = 36,
            HorizontalContentAlignment = HorizontalAlignment.Center,
            VerticalContentAlignment = VerticalAlignment.Center
        };
        close.Click += (_, _) => window.Close();
        apply.Click += (_, _) =>
        {
            if (desktopChosen && selectedMonitor is not null) model.SelectedDesktopMonitor = selectedMonitor;
            model.SelectedReplayCaptureSource = desktopChosen ? DesktopCaptureSource : GameCaptureSource;
            window.Close();
        };

        var footer = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Spacing = 10,
            Children = { close, apply }
        };

        if (body is StackPanel stack)
        {
            stack.Children.Add(tabStrip);
            stack.Children.Add(gamePane);
            stack.Children.Add(desktopPane);
            stack.Children.Add(footer);
        }

        using var previews = new CancellationTokenSource();
        window.Closed += (_, _) => previews.Cancel();
        _ = LoadMonitorPreviewsAsync(tiles, previews.Token);

        await ShowModalDialogAsync<object?>(window);
    }

    private static Button CreateSourceTab(string label) => new()
    {
        Classes = { "sourceTab" },
        Content = label,
        MinWidth = 130,
        Height = 38,
        HorizontalContentAlignment = HorizontalAlignment.Center,
        VerticalContentAlignment = VerticalAlignment.Center
    };

    private static Control BuildGamePane(ViewModels.MainWindowViewModel model)
    {
        var detected = model.ActiveGameDetection.IsDetected;
        var panel = new StackPanel { Spacing = 8, Margin = new Thickness(0, 10, 0, 6) };
        panel.Children.Add(new TextBlock
        {
            Text = detected ? model.ActiveGameDetection.DisplayName : "Waiting for a game",
            Foreground = AppThemeService.Brush("Text_EDF4FB", "#EDF4FB"),
            FontSize = 16,
            FontWeight = FontWeight.Bold,
            HorizontalAlignment = HorizontalAlignment.Center
        });
        panel.Children.Add(new TextBlock
        {
            Text = detected
                ? "The buffer follows this game while it is in the foreground."
                : "Launch a game and the buffer picks it up. If yours is not detected, add it from Settings > Game Detection.",
            Foreground = AppThemeService.Brush("Text_8EA1B6", "#8EA1B6"),
            FontSize = 12,
            TextWrapping = TextWrapping.Wrap,
            TextAlignment = TextAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Center,
            MaxWidth = 420
        });
        return panel;
    }

    private static Control BuildDesktopToggles(ViewModels.MainWindowViewModel model)
    {
        var cursor = new CheckBox { Content = "Capture cursor", IsChecked = model.ReplayDesktopCaptureCursor };
        cursor.IsCheckedChanged += (_, _) => model.ReplayDesktopCaptureCursor = cursor.IsChecked == true;

        var switchToGames = new CheckBox { Content = "Switch to game capture when a game is detected", IsChecked = model.ReplayAutoSwitchToGameCapture };
        switchToGames.IsCheckedChanged += (_, _) => model.ReplayAutoSwitchToGameCapture = switchToGames.IsChecked == true;

        return new StackPanel { Spacing = 8, Children = { cursor, switchToGames } };
    }

    private static Control BuildNotice(string text) => new Border
    {
        Background = AppThemeService.Brush("Surface_15212B", "#15212B"),
        BorderBrush = AppThemeService.Brush("Surface_263846", "#263846"),
        BorderThickness = new Thickness(1),
        CornerRadius = new CornerRadius(10),
        Padding = new Thickness(12, 10),
        Child = new TextBlock
        {
            Text = text,
            Foreground = AppThemeService.Brush("Text_8EA1B6", "#8EA1B6"),
            FontSize = 12,
            TextWrapping = TextWrapping.Wrap
        }
    };

    private static Button BuildMonitorTile(DesktopMonitorOption monitor)
    {
        var preview = new Border
        {
            Name = "PreviewHost",
            Height = 150,
            CornerRadius = new CornerRadius(8),
            ClipToBounds = true,
            Background = AppThemeService.Brush("Surface_0C1319", "#0C1319")
        };
        var label = new TextBlock
        {
            Text = monitor.Label,
            Foreground = AppThemeService.Brush("Text_D7E2EF", "#D7E2EF"),
            FontSize = 12,
            FontWeight = FontWeight.SemiBold,
            TextTrimming = TextTrimming.CharacterEllipsis
        };

        return new Button
        {
            Classes = { "monitorTile" },
            Width = 288,
            Margin = new Thickness(0, 0, 12, 12),
            BorderThickness = new Thickness(2),
            Content = new StackPanel { Spacing = 8, Children = { preview, label } }
        };
    }

    private static async Task LoadMonitorPreviewsAsync(
        List<(Button Button, DesktopMonitorOption Monitor)> tiles, CancellationToken cancellationToken)
    {
        foreach (var (button, monitor) in tiles)
        {
            if (cancellationToken.IsCancellationRequested) return;
            Bitmap? frame = null;
            if (OperatingSystem.IsWindowsVersionAtLeast(10, 0, 17763))
            {
                frame = await MonitorThumbnailService.TryCaptureAsync(monitor, cancellationToken);
            }

            if (frame is null || cancellationToken.IsCancellationRequested) continue;
            if (button.Content is StackPanel { Children: [Border host, ..] })
            {
                host.Child = new Image { Source = frame, Stretch = Stretch.UniformToFill };
            }
        }
    }
}
