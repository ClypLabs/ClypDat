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

    private MainWindowViewModel? Model => DataContext as MainWindowViewModel;

    private void Cs2MatchHistoryLink_OnClick(object? sender, RoutedEventArgs e) => Model?.LinkCs2MatchHistory();
    private void Cs2MatchHistoryUnlink_OnClick(object? sender, RoutedEventArgs e) => Model?.UnlinkCs2MatchHistory();
    private void Cs2MatchHistoryEdit_OnClick(object? sender, RoutedEventArgs e) => Model?.BeginEditCs2MatchHistory();
    private void Cs2MatchHistoryCancel_OnClick(object? sender, RoutedEventArgs e) => Model?.CancelEditCs2MatchHistory();

    private void Cs2AuthCodeHelp_OnClick(object? sender, RoutedEventArgs e)
    {
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(
                "https://help.steampowered.com/en/wizard/HelpWithGameIssue/?appid=730&issueid=128") { UseShellExecute = true });
        }
        catch (Exception error) { Services.AppLog.Error("Opening the Steam authentication code page failed", error); }
    }
}
