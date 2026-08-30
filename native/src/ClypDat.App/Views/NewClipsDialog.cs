using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using ClypDat.App.Services;

namespace ClypDat.App.Views;

// Top-level editor variant: native VLC video cannot be covered by a control in
// MainWindow's visual tree, so this mirrors ShareDialog's transparent airspace.
internal sealed class NewClipsDialog : Window
{
    private readonly Window _owner;
    private readonly TextBlock _title;
    private readonly TextBlock _subtitle;
    private readonly Border _card;
    private readonly Button _primaryButton;
    public StackPanel Cards { get; } = new() { Margin = new Thickness(28, 20, 28, 8) };
    public Button DeleteButton { get; } = new() { MinWidth = 140, Height = 42 };

    public NewClipsDialog(Window owner, EventHandler<RoutedEventArgs> close, EventHandler<RoutedEventArgs> delete, EventHandler<RoutedEventArgs> viewAll)
    {
        _owner = owner;
        WindowDecorations = WindowDecorations.None;
        ShowInTaskbar = false;
        CanResize = false;
        WindowStartupLocation = WindowStartupLocation.Manual;
        Background = Brushes.Transparent;
        TransparencyLevelHint = new[] { WindowTransparencyLevel.Transparent };
        PositionOverOwner(owner);
        owner.PositionChanged += Owner_OnPositionChanged;
        owner.SizeChanged += Owner_OnSizeChanged;
        Closed += (_, _) =>
        {
            owner.PositionChanged -= Owner_OnPositionChanged;
            owner.SizeChanged -= Owner_OnSizeChanged;
        };

        _title = new TextBlock { Foreground = AppThemeService.Brush("Text_D8E4F2", "#D8E4F2"), FontSize = 19, FontWeight = FontWeight.Bold };
        _subtitle = new TextBlock { Foreground = AppThemeService.Brush("Text_8C98A7", "#8C98A7"), FontSize = 12 };
        var summary = new StackPanel { Spacing = 3, VerticalAlignment = VerticalAlignment.Center, Children = { _title, _subtitle } };
        var closeButton = new Button { Content = "✕", Width = 36, Height = 36, CornerRadius = new CornerRadius(18), FontSize = 13 };
        closeButton.Classes.Add("dialogClose");
        closeButton.Click += close;
        DeleteButton.Classes.Add("subtleDangerButton");
        DeleteButton.Click += delete;
        _primaryButton = new Button { Content = "View All Clips", MinWidth = 170, Height = 42 };
        _primaryButton.Classes.Add("primaryButton");
        _primaryButton.Click += viewAll;

        var headerGrid = new Grid { MinHeight = 44, Margin = new Thickness(28, 17, 18, 15), ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        headerGrid.Children.Add(summary);
        headerGrid.Children.Add(closeButton);
        Grid.SetColumn(closeButton, 1);
        var header = new Border { Background = AppThemeService.Brush("Surface_0C1319", "#0C1319"), Child = headerGrid };
        var footerGrid = new Grid { Margin = new Thickness(28, 14), ColumnDefinitions = new ColumnDefinitions("*,Auto,12,Auto") };
        footerGrid.Children.Add(DeleteButton);
        Grid.SetColumn(DeleteButton, 1);
        footerGrid.Children.Add(_primaryButton);
        Grid.SetColumn(_primaryButton, 3);
        var footer = new Border
        {
            Background = AppThemeService.Brush("Surface_141B23", "#141B23"),
            BorderBrush = AppThemeService.Brush("Surface_232F3A", "#232F3A"),
            BorderThickness = new Thickness(0, 1, 0, 0),
            Child = footerGrid
        };
        var dock = new DockPanel();
        dock.Children.Add(header);
        DockPanel.SetDock(header, Dock.Top);
        dock.Children.Add(footer);
        DockPanel.SetDock(footer, Dock.Bottom);
        dock.Children.Add(new ScrollViewer { Content = Cards, MaxHeight = 560 });
        _card = new Border { Width = 496, MaxHeight = 780, CornerRadius = new CornerRadius(20), Background = AppThemeService.Brush("Surface_111920", "#111920"), BorderBrush = AppThemeService.Brush("Surface_3A4856", "#3A4856"), BorderThickness = new Thickness(1), BoxShadow = BoxShadows.Parse("0 18 48 -12 #B0000000"), ClipToBounds = true, Child = dock, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        var scrim = new Border { Background = Brush.Parse("#C7000000"), Child = _card };
        Content = scrim;
        Opened += (_, _) =>
        {
            OverlayTransparencyDiagnostics.Log(this, "new-clips-dialog");
            WindowTransparencyFallback.ApplyIfNeeded(this, scrim.Background, b => scrim.Background = b);
        };
        KeyDown += (_, e) => { if (e.Key == Key.Escape) close(this, new RoutedEventArgs()); };
    }

    public void SetSummary(string title, string subtitle)
    {
        _title.Text = title;
        _subtitle.Text = subtitle;
    }
    public void SetCardWidth(double width) => _card.Width = Math.Clamp(width, 320, Math.Max(320, _owner.Bounds.Width - 32));
    public void SetPrimaryAction(string label) => _primaryButton.Content = label;

    public void RefreshOwnerBounds() => PositionOverOwner(_owner);

    private void PositionOverOwner(Window owner)
    {
        Position = owner.PointToScreen(new Point(0, 0));
        Width = owner.Bounds.Width;
        Height = owner.Bounds.Height;
    }

    private void Owner_OnPositionChanged(object? sender, PixelPointEventArgs e) => RefreshOwnerBounds();

    private void Owner_OnSizeChanged(object? sender, SizeChangedEventArgs e) => RefreshOwnerBounds();
}
