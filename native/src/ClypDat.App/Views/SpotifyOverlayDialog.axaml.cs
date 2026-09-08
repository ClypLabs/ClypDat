using Avalonia.Controls;
using Avalonia.Threading;
using ClypDat.App.Controls;
using ClypDat.App.Services;
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
        var preview = new SpotifyCardPreview();
        var artwork = new SpotifyPreviewArtCache();
        SpotifyPreviewCanvas.Children.Add(preview);
        var clock = System.Diagnostics.Stopwatch.StartNew();
        string? track = null;
        var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1.0 / 30) };
        timer.Tick += (_, _) =>
        {
            var now = viewModel.SpotifyDialogNowPlaying;
            var artPath = string.IsNullOrWhiteSpace(now.Track) ? null : artwork.Get(now.ArtUrl);
            var spec = viewModel.SpotifyDialogSpec(now, artPath);
            if (spec.LegacyCard?.Track != track) { track = spec.LegacyCard?.Track; clock.Restart(); }
            var scale = SpotifyOverlayCardRenderer.Scale(518, 291);
            preview.Width = 406 * scale;
            preview.Height = 140 * scale;
            Canvas.SetLeft(preview, spec.Position.EndsWith("Right", StringComparison.OrdinalIgnoreCase) ? 518 - preview.Width : 0);
            Canvas.SetTop(preview, spec.Position.StartsWith("Top", StringComparison.OrdinalIgnoreCase) ? 14 * 291 / 1080.0 :
                spec.Position.StartsWith("Center", StringComparison.OrdinalIgnoreCase) ? (291 - preview.Height) / 2 : 291 - preview.Height - 14 * 291 / 1080.0);
            preview.Update(spec, clock.Elapsed.TotalSeconds, 518, 291);
        };
        Opened += (_, _) => timer.Start();
        Closed += (_, _) => { timer.Stop(); preview.Dispose(); artwork.Dispose(); };
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
