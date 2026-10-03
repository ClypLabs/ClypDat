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
        OscPortBox.AddHandler(TextInputEvent, OscPort_OnTextInput, RoutingStrategies.Tunnel);
    }

    // Settings markup lives here, but the handlers still belong to
    // MainWindow - their bodies reach all over its state. These forward
    // to the owning window rather than duplicating any of it.
    private void UseMkv_OnClick(object? sender, RoutedEventArgs e) => (DataContext as MainWindowViewModel)?.UseMkv();
    private void IgnoreFullSessionMp4Warning_OnClick(object? sender, RoutedEventArgs e) => (DataContext as MainWindowViewModel)?.IgnoreFullSessionMp4Warning();
    private void HideFullSessionMp4Warning_OnClick(object? sender, RoutedEventArgs e) => (DataContext as MainWindowViewModel)?.HideFullSessionMp4Warning();

    private MainWindow? Owner => TopLevel.GetTopLevel(this) as MainWindow;

    private void OscGuide_OnClick(object? sender, RoutedEventArgs e)
    {
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(
                "https://www.clypdat.xyz/docs/osc") { UseShellExecute = true });
        }
        catch (Exception error) { Services.AppLog.Error("Opening OSC setup guide failed", error); }
    }

    // Leaving the port field with an invalid value restores the working port.
    private void OscPort_OnLostFocus(object? sender, RoutedEventArgs e)
        => (DataContext as MainWindowViewModel)?.CommitOscPortText();

    private void OscPort_OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) (DataContext as MainWindowViewModel)?.CommitOscPortText();
    }

    // Digits only: typed letters and symbols never reach the field. Tunnelled,
    // so it runs before TextBox inserts the text. Pasted text still arrives,
    // and the view model's validation warns about it instead.
    private void OscPort_OnTextInput(object? sender, TextInputEventArgs e)
    {
        if (e.Text is { Length: > 0 } text && !text.All(char.IsAsciiDigit)) e.Handled = true;
    }

    private void HotkeyCaptureButton_OnClick(object? sender, RoutedEventArgs e)
        => Owner?.HotkeyCaptureButton_OnClick(sender, e);

    private void ApplyReplayBitrateRecommendationButton_OnClick(object? sender, RoutedEventArgs e)
        => Owner?.ApplyReplayBitrateRecommendationButton_OnClick(sender, e);
}
