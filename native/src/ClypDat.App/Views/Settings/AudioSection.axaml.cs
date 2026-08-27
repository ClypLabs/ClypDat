using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using ClypDat.App.ViewModels;

namespace ClypDat.App.Views.Settings;

public sealed partial class AudioSection : UserControl
{
    public AudioSection()
    {
        InitializeComponent();
    }

    // Settings markup lives here, but the handlers still belong to
    // MainWindow - their bodies reach all over its state. These forward
    // to the owning window rather than duplicating any of it.
    private MainWindow? Owner => TopLevel.GetTopLevel(this) as MainWindow;

    private void RefreshProcessesButton_OnClick(object? sender, RoutedEventArgs e)
        => Owner?.RefreshProcessesButton_OnClick(sender, e);

    private void AddSelectedChatProcessButton_OnClick(object? sender, RoutedEventArgs e)
        => Owner?.AddSelectedChatProcessButton_OnClick(sender, e);

    private void RemoveChatAudioAppButton_OnClick(object? sender, RoutedEventArgs e)
        => Owner?.RemoveChatAudioAppButton_OnClick(sender, e);

    private void AddSelectedMicrophoneButton_OnClick(object? sender, RoutedEventArgs e)
        => Owner?.AddSelectedMicrophoneButton_OnClick(sender, e);

    private void RemoveMicrophoneButton_OnClick(object? sender, RoutedEventArgs e)
        => Owner?.RemoveMicrophoneButton_OnClick(sender, e);

    private void ToggleMicTestButton_OnClick(object? sender, RoutedEventArgs e)
        => Owner?.ToggleMicTestButton_OnClick(sender, e);
}
