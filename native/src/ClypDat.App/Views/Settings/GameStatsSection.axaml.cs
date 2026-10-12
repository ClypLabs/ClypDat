using Avalonia.Controls;
using Avalonia.Interactivity;
using ClypDat.App.ViewModels;

namespace ClypDat.App.Views.Settings;

public sealed partial class GameStatsSection : UserControl
{
    public GameStatsSection()
    {
        InitializeComponent();
    }

    private MainWindowViewModel? Model => DataContext as MainWindowViewModel;

    private void Cs2MatchHistoryLink_OnClick(object? sender, RoutedEventArgs e) => Model?.LinkCs2MatchHistory();
    private void Cs2MatchHistoryUnlink_OnClick(object? sender, RoutedEventArgs e) => Model?.UnlinkCs2MatchHistory();
    private void Cs2MatchHistoryEdit_OnClick(object? sender, RoutedEventArgs e) => Model?.BeginEditCs2MatchHistory();
    private void Cs2MatchHistoryCancel_OnClick(object? sender, RoutedEventArgs e) => Model?.CancelEditCs2MatchHistory();

    private void Cs2MatchHistoryGuide_OnClick(object? sender, RoutedEventArgs e)
    {
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(
                "https://www.clypdat.xyz/docs/cs2-match-stats") { UseShellExecute = true });
        }
        catch (Exception error) { Services.AppLog.Error("Opening the CS2 match stats guide failed", error); }
    }

    private void SteamAccountPage_OnClick(object? sender, RoutedEventArgs e)
    {
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(
                "https://store.steampowered.com/account/") { UseShellExecute = true });
        }
        catch (Exception error) { Services.AppLog.Error("Opening the Steam account page failed", error); }
    }

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
