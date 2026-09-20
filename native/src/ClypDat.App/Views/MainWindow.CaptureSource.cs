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

        CloseHeaderFlyouts();
        model.SettingsSearchText = string.Empty;
        model.SelectSettingsSection("Game Detection");
        model.OpenSettings();
    }

    private async void CaptureSourceRow_OnClick(object? sender, RoutedEventArgs e)
    {
        try
        {
            if (ViewModel is null) return;
            CloseHeaderFlyouts();
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

        var (window, body) = CreateChromelessDialog("Capture source", centerTitle: false);
        window.Width = 720;
        if (body is StackPanel bodyStack)
        {
            bodyStack.Spacing = 20;
            bodyStack.Margin = new Thickness(24, 16, 24, 24);
        }

        var startedOnDesktop = model.IsDesktopCapture;
        var selectedMonitor = model.SelectedDesktopMonitor ?? model.DesktopMonitors.FirstOrDefault();
        var desktopChosen = startedOnDesktop;

        var gameTab = CreateSourceTab("Game", "Follow the active game");
        var desktopTab = CreateSourceTab("Desktop", "Record a display");

        var gamePane = BuildGamePane(model);
        var desktopPane = new StackPanel { Spacing = 14 };
        var tiles = new List<(Button Button, DesktopMonitorOption Monitor)>();
        var tileRow = new WrapPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Center
        };

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
                button.Classes.Set("selected", isSelected);
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
        desktopPane.Children.Add(new TextBlock
        {
            Text = "Choose a display",
            FontSize = 13,
            FontWeight = FontWeight.SemiBold,
            Foreground = AppThemeService.Brush("Text_D7E2EF", "#D7E2EF")
        });
        desktopPane.Children.Add(new ScrollViewer
        {
            MaxHeight = 300,
            HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled,
            Content = tileRow
        });
        desktopPane.Children.Add(BuildDesktopToggles(model));
        desktopPane.Children.Add(BuildNotice("Everything visible on this display will be recorded, including notifications."));

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

        var tabStrip = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("*,*"),
            ColumnSpacing = 8,
            Children = { gameTab, desktopTab }
        };
        Grid.SetColumn(desktopTab, 1);

        var apply = new Button
        {
            Classes = { "primaryButton" },
            Content = "Use this source",
            MinWidth = 150,
            Height = 40,
            Padding = new Thickness(16, 0),
            HorizontalContentAlignment = HorizontalAlignment.Center,
            VerticalContentAlignment = VerticalAlignment.Center
        };
        var close = new Button
        {
            Classes = { "sourceCancel" },
            Content = "Close",
            Width = 100,
            Height = 40,
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
            stack.Children.Add(new TextBlock
            {
                Text = "What would you like to record?",
                FontSize = 20,
                FontWeight = FontWeight.SemiBold,
                Foreground = AppThemeService.Brush("Text_EDF4FB", "#EDF4FB")
            });
            stack.Children.Add(tabStrip);
            stack.Children.Add(gamePane);
            stack.Children.Add(desktopPane);
            stack.Children.Add(new Border
            {
                BorderBrush = AppThemeService.Brush("Surface_263846", "#263846"),
                BorderThickness = new Thickness(0, 1, 0, 0),
                Padding = new Thickness(0, 16, 0, 0),
                Child = footer
            });
        }

        using var previews = new CancellationTokenSource();
        window.Closed += (_, _) => previews.Cancel();
        _ = LoadMonitorPreviewsAsync(tiles, previews.Token);

        await ShowModalDialogAsync<object?>(window);
    }

    private static Button CreateSourceTab(string label, string description) => new()
    {
        Classes = { "sourceTab" },
        Content = new StackPanel
        {
            Spacing = 4,
            Children =
            {
                new TextBlock { Text = label, FontSize = 14, FontWeight = FontWeight.SemiBold },
                new TextBlock
                {
                    Text = description,
                    FontSize = 12,
                    FontWeight = FontWeight.Normal,
                    Foreground = AppThemeService.Brush("Text_8EA1B6", "#8EA1B6")
                }
            }
        },
        HorizontalAlignment = HorizontalAlignment.Stretch,
        HorizontalContentAlignment = HorizontalAlignment.Left,
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
        return new Border
        {
            Background = AppThemeService.Brush("Surface_15212B", "#15212B"),
            CornerRadius = new CornerRadius(12),
            Padding = new Thickness(24),
            Child = panel
        };
    }

    private static Control BuildDesktopToggles(ViewModels.MainWindowViewModel model)
    {
        var cursor = new ToggleSwitch { OnContent = null, OffContent = null, IsChecked = model.ReplayDesktopCaptureCursor };
        cursor.IsCheckedChanged += (_, _) => model.ReplayDesktopCaptureCursor = cursor.IsChecked == true;

        var switchToGames = new ToggleSwitch { OnContent = null, OffContent = null, IsChecked = model.ReplayAutoSwitchToGameCapture };
        switchToGames.IsCheckedChanged += (_, _) => model.ReplayAutoSwitchToGameCapture = switchToGames.IsChecked == true;

        static Control Row(string title, string description, ToggleSwitch toggle)
        {
            var text = new StackPanel
            {
                Spacing = 3,
                Children =
                {
                    new TextBlock { Text = title, FontSize = 13, FontWeight = FontWeight.SemiBold },
                    new TextBlock
                    {
                        Text = description, FontSize = 12, TextWrapping = TextWrapping.Wrap,
                        Foreground = AppThemeService.Brush("Text_8EA1B6", "#8EA1B6")
                    }
                }
            };
            Avalonia.Automation.AutomationProperties.SetName(toggle, title);
            toggle.VerticalAlignment = VerticalAlignment.Center;
            Grid.SetColumn(toggle, 1);
            return new Grid
            {
                ColumnDefinitions = new ColumnDefinitions("*,Auto"),
                ColumnSpacing = 16,
                Children = { text, toggle }
            };
        }

        return new Border
        {
            Background = AppThemeService.Brush("Surface_15212B", "#15212B"),
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(16),
            Child = new StackPanel
            {
                Spacing = 16,
                Children =
                {
                    Row("Capture cursor", "Include the pointer in your clips.", cursor),
                    Row("Auto-switch to game", "Use game capture when a game is detected.", switchToGames)
                }
            }
        };
    }

    private static Control BuildNotice(string text) => new Border
    {
        Background = AppThemeService.Brush("Surface_15212B", "#15212B"),
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
            Height = 144,
            CornerRadius = new CornerRadius(8),
            ClipToBounds = true,
            Background = AppThemeService.Brush("Surface_0C1319", "#0C1319")
        };
        var label = new TextBlock
        {
            Text = monitor.Label.Split('—')[0].Trim(),
            Foreground = AppThemeService.Brush("Text_D7E2EF", "#D7E2EF"),
            FontSize = 13,
            FontWeight = FontWeight.SemiBold,
            TextTrimming = TextTrimming.CharacterEllipsis
        };

        var selected = new TextBlock
        {
            Classes = { "sourceSelection" },
            Text = "Selected",
            FontSize = 11,
            FontWeight = FontWeight.SemiBold,
            VerticalAlignment = VerticalAlignment.Center
        };
        Grid.SetColumn(selected, 1);
        var heading = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("*,Auto"),
            ColumnSpacing = 8,
            Children = { label, selected }
        };
        var details = new TextBlock
        {
            Text = $"{monitor.Width} × {monitor.Height}" + (monitor.IsPrimary ? " · Primary display" : ""),
            FontSize = 11,
            Foreground = AppThemeService.Brush("Text_8EA1B6", "#8EA1B6")
        };
        return new Button
        {
            Classes = { "monitorTile" },
            Width = 312,
            Margin = new Thickness(5, 0, 5, 10),
            BorderThickness = new Thickness(1),
            Content = new StackPanel { Spacing = 8, Children = { preview, heading, details } }
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
                host.Child = new Image { Source = frame, Stretch = Stretch.Uniform };
            }
        }
    }
}
