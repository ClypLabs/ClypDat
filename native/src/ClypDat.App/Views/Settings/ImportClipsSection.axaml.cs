using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using ClypDat.App.ViewModels;

namespace ClypDat.App.Views.Settings;

public sealed partial class ImportClipsSection : UserControl
{
    public ImportClipsSection()
    {
        InitializeComponent();
    }

    // Settings markup lives here, but the handlers still belong to
    // MainWindow - their bodies reach all over its state. These forward
    // to the owning window rather than duplicating any of it.
    private MainWindow? Owner => TopLevel.GetTopLevel(this) as MainWindow;

    private void ChooseImportSourceButton_OnClick(object? sender, RoutedEventArgs e)
        => Owner?.ChooseImportSourceButton_OnClick(sender, e);

    private void BackToImportSourcesButton_OnClick(object? sender, RoutedEventArgs e)
        => Owner?.BackToImportSourcesButton_OnClick(sender, e);

    private void ScanMedalButton_OnClick(object? sender, RoutedEventArgs e)
        => Owner?.ScanMedalButton_OnClick(sender, e);

    private void ToggleMedalImportSelection_OnClick(object? sender, RoutedEventArgs e)
        => Owner?.ToggleMedalImportSelection_OnClick(sender, e);

    private void ImportMedalButton_OnClick(object? sender, RoutedEventArgs e)
        => Owner?.ImportMedalButton_OnClick(sender, e);

    private void ScanSteelSeriesButton_OnClick(object? sender, RoutedEventArgs e)
        => Owner?.ScanSteelSeriesButton_OnClick(sender, e);

    private void ToggleSteelSeriesImportSelection_OnClick(object? sender, RoutedEventArgs e)
        => Owner?.ToggleSteelSeriesImportSelection_OnClick(sender, e);

    private void ImportSteelSeriesButton_OnClick(object? sender, RoutedEventArgs e)
        => Owner?.ImportSteelSeriesButton_OnClick(sender, e);
}
