using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;

namespace ClypDat.App.Views;

// Top-level editor variant: native VLC video cannot be covered by a control in
// MainWindow's visual tree, so this mirrors ShareDialog's transparent airspace.
internal sealed class NewClipsDialog : Window
{
    private readonly TextBlock _title;
    public WrapPanel Cards { get; } = new() { Margin = new Thickness(24, 20, 24, 4) };
    public Button DeleteButton { get; } = new() { MinWidth = 150, Height = 40 };

    public NewClipsDialog(Window owner, EventHandler<RoutedEventArgs> close, EventHandler<RoutedEventArgs> delete, EventHandler<RoutedEventArgs> viewAll)
    {
        SystemDecorations = SystemDecorations.None;
        ShowInTaskbar = false;
        CanResize = false;
        WindowStartupLocation = WindowStartupLocation.Manual;
        Background = Brushes.Transparent;
        TransparencyLevelHint = new[] { WindowTransparencyLevel.Transparent };
        PositionOverOwner(owner);

        _title = new TextBlock { Foreground = Brush.Parse("#D8E4F2"), FontSize = 17, FontWeight = FontWeight.Bold, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        var closeButton = new Button { Content = "✕", Width = 52, Height = 56 };
        closeButton.Click += close;
        DeleteButton.Click += delete;
        var viewAllButton = new Button { Content = "View All Clips", MinWidth = 180, Height = 40 };
        viewAllButton.Click += viewAll;

        var header = new Grid { Height = 56, Background = Brush.Parse("#0C1319"), ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto") };
        header.Children.Add(_title);
        Grid.SetColumn(_title, 1);
        header.Children.Add(closeButton);
        Grid.SetColumn(closeButton, 2);
        var footer = new Grid { Margin = new Thickness(24, 16), ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto") };
        footer.Children.Add(DeleteButton);
        footer.Children.Add(viewAllButton);
        Grid.SetColumn(viewAllButton, 2);
        var dock = new DockPanel();
        dock.Children.Add(header);
        DockPanel.SetDock(header, Dock.Top);
        dock.Children.Add(footer);
        DockPanel.SetDock(footer, Dock.Bottom);
        dock.Children.Add(new ScrollViewer { Content = Cards, MaxHeight = 620 });
        var card = new Border { Width = 1012, MaxHeight = 860, CornerRadius = new CornerRadius(12), Background = Brush.Parse("#111920"), BorderBrush = Brush.Parse("#232F3A"), BorderThickness = new Thickness(1), ClipToBounds = true, Child = dock, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        Content = new Border { Background = Brush.Parse("#DD000000"), Child = card };
        KeyDown += (_, e) => { if (e.Key == Key.Escape) close(this, new RoutedEventArgs()); };
    }

    public void SetTitle(string title) => _title.Text = title;

    private void PositionOverOwner(Window owner)
    {
        Position = owner.PointToScreen(new Point(0, 0));
        Width = owner.Bounds.Width;
        Height = owner.Bounds.Height;
    }
}
