using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using ClypDat.App.Services;
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
    private void FortniteReplayFolder_OnClick(object? sender, RoutedEventArgs e) => Model?.OpenFortniteReplayFolder();

    private async void Cs2DemoImport_OnClick(object? sender, RoutedEventArgs e)
    {
        if (Model is not { IsImportingCs2Demo: false } model || TopLevel.GetTopLevel(this) is not { } owner) return;
        try
        {
            var files = await owner.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = "Import saved CS2 demo",
                AllowMultiple = false,
                FileTypeFilter = [new FilePickerFileType("CS2 demo") { Patterns = ["*.dem", "*.dem.bz2"] }]
            });
            if (files.FirstOrDefault()?.TryGetLocalPath() is { Length: > 0 } path) await model.ImportCs2DemoAsync(path);
        }
        catch (Exception error) { AppLog.Error("Opening the CS2 demo picker failed", error); }
    }
}
