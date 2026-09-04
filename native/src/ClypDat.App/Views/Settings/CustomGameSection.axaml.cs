using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using ClypDat.App.ViewModels;

namespace ClypDat.App.Views.Settings;

public sealed partial class CustomGameSection : UserControl
{
    public CustomGameSection()
    {
        InitializeComponent();
    }

    // Settings markup lives here, but the handlers still belong to
    // MainWindow - their bodies reach all over its state. These forward
    // to the owning window rather than duplicating any of it.
    private MainWindow? Owner => TopLevel.GetTopLevel(this) as MainWindow;

    private void AddCustomGameComboBox_OnSelectionChanged(object? sender, SelectionChangedEventArgs e)
        => Owner?.AddCustomGameComboBox_OnSelectionChanged(sender, e);

    private void CustomGameTab_OnClick(object? sender, RoutedEventArgs e)
        => Owner?.CustomGameTab_OnClick(sender, e);

    private void RemoveCustomGameGroupButton_OnClick(object? sender, RoutedEventArgs e)
        => Owner?.RemoveCustomGameGroupButton_OnClick(sender, e);

    private void AddCustomGameSettingButton_OnClick(object? sender, RoutedEventArgs e)
        => Owner?.AddCustomGameSettingButton_OnClick(sender, e);

    private void AddCustomGameGroupMenuItem_OnClick(object? sender, RoutedEventArgs e)
        => Owner?.AddCustomGameGroupMenuItem_OnClick(sender, e);

    private void DeleteCustomGameButton_OnClick(object? sender, RoutedEventArgs e)
        => Owner?.DeleteCustomGameButton_OnClick(sender, e);

    private void HotkeyCaptureButton_OnClick(object? sender, RoutedEventArgs e)
        => Owner?.HotkeyCaptureButton_OnClick(sender, e);

    private void ResetGameAudioVolumeButton_OnClick(object? sender, RoutedEventArgs e)
        => Owner?.ResetGameAudioVolumeButton_OnClick(sender, e);

    private void ResetMicrophoneVolumeButton_OnClick(object? sender, RoutedEventArgs e)
        => Owner?.ResetMicrophoneVolumeButton_OnClick(sender, e);

    private void ResetAppVolumeButton_OnClick(object? sender, RoutedEventArgs e)
        => Owner?.ResetAppVolumeButton_OnClick(sender, e);
}
