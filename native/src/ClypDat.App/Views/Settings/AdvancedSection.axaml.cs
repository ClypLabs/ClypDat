using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using ClypDat.App.ViewModels;

namespace ClypDat.App.Views.Settings;

public sealed partial class AdvancedSection : UserControl
{
    public AdvancedSection()
    {
        InitializeComponent();
        OscPortBox.AddHandler(TextInputEvent, OscPort_OnTextInput, RoutingStrategies.Tunnel);
    }

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

    private void OscPort_OnLostFocus(object? sender, RoutedEventArgs e)
        => (DataContext as MainWindowViewModel)?.CommitOscPortText();

    private void OscPort_OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) (DataContext as MainWindowViewModel)?.CommitOscPortText();
    }

    private void OscPort_OnTextInput(object? sender, TextInputEventArgs e)
    {
        if (e.Text is { Length: > 0 } text && !text.All(char.IsAsciiDigit)) e.Handled = true;
    }

    private void LicenseLinkText_OnPointerPressed(object? sender, PointerPressedEventArgs e)
        => Owner?.LicenseLinkText_OnPointerPressed(sender, e);
}
