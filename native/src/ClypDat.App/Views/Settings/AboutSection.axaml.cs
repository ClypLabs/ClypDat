using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using ClypDat.App.ViewModels;

namespace ClypDat.App.Views.Settings;

public sealed partial class AboutSection : UserControl
{
    public AboutSection()
    {
        InitializeComponent();
    }

    // Settings markup lives here, but the handlers still belong to
    // MainWindow - their bodies reach all over its state. These forward
    // to the owning window rather than duplicating any of it.
    private MainWindow? Owner => TopLevel.GetTopLevel(this) as MainWindow;

    private void LicenseLinkText_OnPointerPressed(object? sender, PointerPressedEventArgs e)
        => Owner?.LicenseLinkText_OnPointerPressed(sender, e);

    private async void CheckUpdatesButton_OnClick(object? sender, RoutedEventArgs e)
    {
        if (Owner is not { } owner || !CheckNowButton.IsEnabled) return;

        CheckNowButton.IsEnabled = false;
        CheckNowButtonText.IsVisible = false;
        CheckingContent.IsVisible = true;
        try
        {
            await owner.CheckUpdatesAsync();
        }
        finally
        {
            CheckingContent.IsVisible = false;
            CheckNowButtonText.IsVisible = true;
            CheckNowButton.IsEnabled = true;
        }
    }

    private void OpenGitHubButton_OnClick(object? sender, RoutedEventArgs e)
        => Owner?.OpenGitHubButton_OnClick(sender, e);

    private void ExportCaptureDiagnosticsButton_OnClick(object? sender, RoutedEventArgs e)
        => Owner?.ExportCaptureDiagnosticsButton_OnClick(sender, e);

    private void OpenLogsButton_OnClick(object? sender, RoutedEventArgs e)
        => Owner?.OpenLogsButton_OnClick(sender, e);

    private void ShowWalkthroughButton_OnClick(object? sender, RoutedEventArgs e)
        => Owner?.ShowWalkthroughButton_OnClick(sender, e);

    private void OpenLicensesButton_OnClick(object? sender, RoutedEventArgs e)
        => Owner?.OpenLicensesButton_OnClick(sender, e);
}
