using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using ClypDat.App.ViewModels;

namespace ClypDat.App.Views.Settings;

public sealed partial class GameDetectionSection : UserControl
{
    public GameDetectionSection()
    {
        InitializeComponent();
    }

    // Settings markup lives here, but the handlers still belong to
    // MainWindow - their bodies reach all over its state. These forward
    // to the owning window rather than duplicating any of it.
    private MainWindow? Owner => TopLevel.GetTopLevel(this) as MainWindow;

    private void RefreshProcessesButton_OnClick(object? sender, RoutedEventArgs e)
        => Owner?.RefreshProcessesButton_OnClick(sender, e);

    private void AddGameFromProcessButton_OnClick(object? sender, RoutedEventArgs e)
        => Owner?.AddGameFromProcessButton_OnClick(sender, e);

    private void BrowseCustomGameButton_OnClick(object? sender, RoutedEventArgs e)
        => Owner?.BrowseCustomGameButton_OnClick(sender, e);

    private void RefreshGameIconsButton_OnClick(object? sender, RoutedEventArgs e)
        => Owner?.RefreshGameIconsButton_OnClick(sender, e);

    private void RemoveGameButton_OnClick(object? sender, RoutedEventArgs e)
        => Owner?.RemoveGameButton_OnClick(sender, e);

    private void RemoveIgnoredGameButton_OnClick(object? sender, RoutedEventArgs e)
        => Owner?.RemoveIgnoredGameButton_OnClick(sender, e);
}
