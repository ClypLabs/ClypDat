using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using ClypDat.App.ViewModels;

namespace ClypDat.App.Views;

/// <summary>
/// Where the Spotify track sits on a clip, and whether it is drawn at all.
///
/// A dialog rather than another card in Connected Accounts: this is one
/// connection's own output settings, and the accounts page is a list of
/// connections, not a place options accumulate under whichever provider
/// happened to bring them.
/// </summary>
public partial class SpotifyOverlayDialog : Window
{
    // Avalonia's XAML loader needs a parameterless constructor to accept this
    // as a top-level control; the one below is what actually opens.
    public SpotifyOverlayDialog() => InitializeComponent();

    public SpotifyOverlayDialog(MainWindowViewModel viewModel) : this()
    {
        DataContext = viewModel;
    }

    // The window has no system chrome, so the title bar drags it.
    private void TitleBar_OnPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) BeginMoveDrag(e);
    }

    private void SpotifyOverlayDialog_OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape) return;
        e.Handled = true;
        Close();
    }

    // Every control writes straight through to settings, so leaving is the only
    // thing left to do - there is nothing here to apply or discard.
    private void CloseButton_OnClick(object? sender, RoutedEventArgs e) => Close();
}
