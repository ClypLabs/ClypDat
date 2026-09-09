using Avalonia.Controls;
using Avalonia.Threading;
using ClypDat.App.Controls;
using ClypDat.App.Services;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia;
using Avalonia.Media;
using ClypDat.App.ViewModels;
using ClypDat.Core.Settings;

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
    private const int PreviewWidth = 518, PreviewHeight = 291;
    private SpotifyCardPreview? _preview;
    private SpotifyOverlayAdorner? _adorner;
    private SpotifyOverlayTransform? _transform;
    private SpotifyOverlayDragMode? _dragMode;
    private Point _dragStart;
    private SpotifyOverlayTransform? _dragTransform;
    // Avalonia's XAML loader needs a parameterless constructor to accept this
    // as a top-level control; the one below is what actually opens.
    public SpotifyOverlayDialog() => InitializeComponent();

    public SpotifyOverlayDialog(MainWindowViewModel viewModel) : this()
    {
        DataContext = viewModel;
        var preview = new SpotifyCardPreview { IsHitTestVisible = false };
        _preview = preview;
        _adorner = new SpotifyOverlayAdorner { IsHitTestVisible = false };
        var artwork = new SpotifyPreviewArtCache();
        SpotifyPreviewCanvas.Children.Add(preview);
        SpotifyPreviewCanvas.Children.Add(_adorner);
        _transform = ResolveTransform(viewModel);
        var clock = System.Diagnostics.Stopwatch.StartNew();
        string? track = null;
        var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1.0 / 30) };
        timer.Tick += (_, _) =>
        {
            var now = viewModel.SpotifyDialogNowPlaying;
            var artPath = string.IsNullOrWhiteSpace(now.Track) ? null : artwork.Get(now.ArtUrl);
            var spec = viewModel.SpotifyDialogSpec(now, artPath) with { Transform = _transform };
            if (spec.LegacyCard?.Track != track) { track = spec.LegacyCard?.Track; clock.Restart(); }
            var raster = SpotifyOverlayLayout.ResolveRenderBounds(PreviewWidth, PreviewHeight, spec.Position, _transform);
            var cardBounds = SpotifyOverlayLayout.Resolve(PreviewWidth, PreviewHeight, spec.Position, _transform);
            preview.Width = raster.Width;
            preview.Height = raster.Height;
            Canvas.SetLeft(preview, raster.X);
            Canvas.SetTop(preview, raster.Y);
            _adorner!.Width = raster.Width;
            _adorner.Height = raster.Height;
            Canvas.SetLeft(_adorner, raster.X);
            Canvas.SetTop(_adorner, raster.Y);
            _adorner.CardBounds = new Rect(cardBounds.X - raster.X, cardBounds.Y - raster.Y, cardBounds.Width, cardBounds.Height);
            _adorner.RotationDegrees = _transform?.RotationDegrees ?? 0;
            _adorner.InvalidateVisual();
            preview.Update(spec, clock.Elapsed.TotalSeconds, PreviewWidth, PreviewHeight);
        };
        Opened += (_, _) => timer.Start();
        Closed += (_, _) =>
        {
            SaveCompletedPreviewDrag();
            EndPreviewDrag(null);
            timer.Stop();
            preview.Dispose();
            artwork.Dispose();
        };
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

    private SpotifyOverlayTransform ResolveTransform(MainWindowViewModel model)
    {
        if (model.Settings.SpotifyOverlayDefaultTransform is { } value)
            return SpotifyOverlayLayout.Normalize(PreviewWidth, PreviewHeight, value);
        var legacy = SpotifyOverlayLayout.Resolve(PreviewWidth, PreviewHeight, model.Settings.SpotifyOverlayPosition);
        return new((double)legacy.X / PreviewWidth, (double)legacy.Y / PreviewHeight, (double)legacy.Width / PreviewWidth);
    }

    private void Preview_OnPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (DataContext is not MainWindowViewModel model || !e.GetCurrentPoint(SpotifyPreviewCanvas).Properties.IsLeftButtonPressed || _adorner is null) return;
        _dragStart = e.GetPosition(SpotifyPreviewCanvas);
        var local = _dragStart - new Point(Canvas.GetLeft(_adorner), Canvas.GetTop(_adorner));
        if (!_adorner.TryHitTest(local, out var mode)) return;
        _dragMode = mode;
        _dragTransform = _transform ?? ResolveTransform(model);
        e.Pointer.Capture(SpotifyPreviewCanvas);
        e.Handled = true;
    }

    private void Preview_OnPointerMoved(object? sender, PointerEventArgs e)
    {
        if (_dragMode is not { } mode || _dragTransform is not { } start || DataContext is not MainWindowViewModel model) return;
        ApplyPreviewDrag(e.GetPosition(SpotifyPreviewCanvas), start, mode, model);
        e.Handled = true;
    }

    private void Preview_OnPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (_dragMode is not { } mode || _dragTransform is not { } start || DataContext is not MainWindowViewModel model) return;
        ApplyPreviewDrag(e.GetPosition(SpotifyPreviewCanvas), start, mode, model);
        SaveCompletedPreviewDrag();
        EndPreviewDrag(e.Pointer);
        e.Handled = true;
    }

    private void Preview_OnPointerCaptureLost(object? sender, PointerCaptureLostEventArgs e)
    {
        SaveCompletedPreviewDrag();
        EndPreviewDrag(e.Pointer);
    }

    private void ApplyPreviewDrag(Point point, SpotifyOverlayTransform start, SpotifyOverlayDragMode mode, MainWindowViewModel model)
    {
        _transform = mode == SpotifyOverlayDragMode.Rotate
            ? SpotifyOverlayManipulation.Rotate(start, _dragStart, point, PreviewWidth, PreviewHeight)
            : SpotifyOverlayManipulation.Apply(start, mode, point.X - _dragStart.X, point.Y - _dragStart.Y, PreviewWidth, PreviewHeight);
        model.Settings.SpotifyOverlayDefaultTransform = _transform;
        model.RaiseSpotifyOverlayPreviewChanged();
    }

    private void SaveCompletedPreviewDrag()
    {
        if (_dragMode is not null && DataContext is MainWindowViewModel model) model.SaveSettings();
    }

    private void EndPreviewDrag(IPointer? pointer)
    {
        _dragMode = null;
        _dragTransform = null;
        pointer?.Capture(null);
    }
}
