using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using ClypDat.App.ViewModels;

namespace ClypDat.App.Views.Settings;

public sealed partial class AutoClipSection : UserControl
{
    public AutoClipSection()
    {
        InitializeComponent();
    }

    // Settings markup lives here, but the handlers still belong to
    // MainWindow - their bodies reach all over its state. These forward
    // to the owning window rather than duplicating any of it.
    private MainWindow? Owner => TopLevel.GetTopLevel(this) as MainWindow;

    private void SetupDotaAutoClipButton_OnClick(object? sender, RoutedEventArgs e)
        => Owner?.SetupDotaAutoClipButton_OnClick(sender, e);

    private void AutoClipGroupToggleButton_OnClick(object? sender, RoutedEventArgs e)
        => Owner?.AutoClipGroupToggleButton_OnClick(sender, e);

    private void AutoClipGameExpandButton_OnClick(object? sender, RoutedEventArgs e)
        => Owner?.AutoClipGameExpandButton_OnClick(sender, e);

    private void AutoClipGroupExpandButton_OnClick(object? sender, RoutedEventArgs e)
        => Owner?.AutoClipGroupExpandButton_OnClick(sender, e);
}
