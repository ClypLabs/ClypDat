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
    private void Cs2MatchHistoryRetry_OnClick(object? sender, RoutedEventArgs e) => Model?.RetryCs2MatchHistory();
}
