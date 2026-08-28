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
    private readonly Border _card;
    private readonly Button _primaryButton;
    public StackPanel Cards { get; } = new() { Margin = new Thickness(24, 20, 24, 4) };
    public Button DeleteButton { get; } = new() { MinWidth = 150, Height = 40 };

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

        _title = new TextBlock { Foreground = Brush.Parse("#D8E4F2"), FontSize = 17, FontWeight = FontWeight.Bold, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        var closeButton = new Button { Content = "✕", Width = 52, Height = 56 };
        closeButton.Classes.Add("dialogClose");
        closeButton.Click += close;
        DeleteButton.Classes.Add("deleteButton");
        DeleteButton.Click += delete;
        _primaryButton = new Button { Content = "View All Clips", MinWidth = 180, Height = 40 };
        _primaryButton.Classes.Add("primaryButton");
        _primaryButton.Click += viewAll;

        var header = new Grid { Height = 56, Background = AppThemeService.Brush("Surface_0C1319", "#0C1319"), ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto") };
        header.Children.Add(_title);
        Grid.SetColumn(_title, 1);
        header.Children.Add(closeButton);
        Grid.SetColumn(closeButton, 2);
        var footer = new Grid { Margin = new Thickness(24, 16), ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto") };
        footer.Children.Add(DeleteButton);
        footer.Children.Add(_primaryButton);
        Grid.SetColumn(_primaryButton, 2);
        var dock = new DockPanel();
        dock.Children.Add(header);
        DockPanel.SetDock(header, Dock.Top);
        dock.Children.Add(footer);
        DockPanel.SetDock(footer, Dock.Bottom);
        dock.Children.Add(new ScrollViewer { Content = Cards, MaxHeight = 620 });
        _card = new Border { Width = 520, MaxHeight = 860, CornerRadius = new CornerRadius(12), Background = AppThemeService.Brush("Surface_111920", "#111920"), BorderBrush = AppThemeService.Brush("EdgeBrush", "#232F3A"), BorderThickness = new Thickness(1), ClipToBounds = true, Child = dock, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        var scrim = new Border { Background = Brush.Parse("#DD000000"), Child = _card };
        Content = scrim;
        Opened += (_, _) =>
        {
            OverlayTransparencyDiagnostics.Log(this, "new-clips-dialog");
            WindowTransparencyFallback.ApplyIfNeeded(this, scrim.Background, b => scrim.Background = b);
        };
        KeyDown += (_, e) => { if (e.Key == Key.Escape) close(this, new RoutedEventArgs()); };
    }

    public void SetTitle(string title) => _title.Text = title;
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
