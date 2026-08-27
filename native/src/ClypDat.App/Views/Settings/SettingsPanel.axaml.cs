using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using ClypDat.App.ViewModels;

namespace ClypDat.App.Views.Settings;

public sealed partial class SettingsPanel : UserControl
{
    public SettingsPanel()
    {
        InitializeComponent();
    }

    // Settings markup lives here, but the handlers still belong to
    // MainWindow - their bodies reach all over its state. These forward
    // to the owning window rather than duplicating any of it.
    private MainWindow? Owner => TopLevel.GetTopLevel(this) as MainWindow;

    private void ClearSettingsSearchButton_OnClick(object? sender, RoutedEventArgs e)
        => Owner?.ClearSettingsSearchButton_OnClick(sender, e);

    private void SettingsNavButton_OnClick(object? sender, RoutedEventArgs e)
        => Owner?.SettingsNavButton_OnClick(sender, e);

    private void FixCustomGameQualityWarningButton_OnClick(object? sender, RoutedEventArgs e)
        => Owner?.FixCustomGameQualityWarningButton_OnClick(sender, e);

    private void AcknowledgeCustomGameQualityWarningButton_OnClick(object? sender, RoutedEventArgs e)
        => Owner?.AcknowledgeCustomGameQualityWarningButton_OnClick(sender, e);

    private void HideCustomGameQualityWarningButton_OnClick(object? sender, RoutedEventArgs e)
        => Owner?.HideCustomGameQualityWarningButton_OnClick(sender, e);
    internal TextBox SearchBox => SettingsSearchBox;

    internal ScrollViewer ScrollViewer => SettingsScrollViewer;
}
