using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using ClypDat.App.ViewModels;

namespace ClypDat.App.Views.Settings;

public sealed partial class ReplayBufferSection : UserControl
{
    public ReplayBufferSection()
    {
        InitializeComponent();
    }

    // Settings markup lives here, but the handlers still belong to
    // MainWindow - their bodies reach all over its state. These forward
    // to the owning window rather than duplicating any of it.
    private MainWindow? Owner => TopLevel.GetTopLevel(this) as MainWindow;

    private void HotkeyCaptureButton_OnClick(object? sender, RoutedEventArgs e)
        => Owner?.HotkeyCaptureButton_OnClick(sender, e);

    private void ApplyReplayBitrateRecommendationButton_OnClick(object? sender, RoutedEventArgs e)
        => Owner?.ApplyReplayBitrateRecommendationButton_OnClick(sender, e);
}
