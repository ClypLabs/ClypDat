using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using ClypDat.App.ViewModels;
using ClypDat.Core.Settings;

namespace ClypDat.App.Views.Settings;

public sealed partial class VideoOverlaysSection : UserControl
{
    private MainWindowViewModel? _owner;
    private string? _dragLayer;
    private VideoOverlayManipulationMode _dragMode;
    private Point _lastPointer;

    public VideoOverlaysSection() { InitializeComponent(); AttachedToVisualTree += Attached; DetachedFromVisualTree += Detached; SizeChanged += (_, _) => UpdateNarrowLayout(); }

    private void Attached(object? sender, Avalonia.VisualTreeAttachmentEventArgs e)
    {
        _owner = (TopLevel.GetTopLevel(this) as MainWindow)?.DataContext as MainWindowViewModel;
        if (_owner is null) return;
        DataContext = new VideoOverlayViewModel(_owner.Settings.VideoOverlays, _owner.SaveSettings,
            () => _ = (TopLevel.GetTopLevel(this) as MainWindow)?.UpdateVideoOverlaySettingsAsync());
        _owner.PropertyChanged += OwnerChanged;
        UpdateVisibility();
        UpdateNarrowLayout();
    }

    private void Detached(object? sender, Avalonia.VisualTreeAttachmentEventArgs e)
    {
        EndDrag();
        (DataContext as IDisposable)?.Dispose();
        DataContext = null;
        if (_owner is not null) _owner.PropertyChanged -= OwnerChanged;
        _owner = null;
    }

    private void OwnerChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(MainWindowViewModel.SelectedSettingsSection) or nameof(MainWindowViewModel.SettingsSearchText)) UpdateVisibility();
    }

    // This section stays attached to the visual tree while hidden, so Detached
    // is not the signal for "the user left" - visibility is. The input hook
    // must not outlive the preview that needs it.
    private void UpdateVisibility()
    {
        IsVisible = _owner?.SelectedSettingsSection == "Video Overlays" || !string.IsNullOrWhiteSpace(_owner?.SettingsSearchText);
        if (DataContext is not VideoOverlayViewModel model) return;
        if (IsVisible) model.StartInputPreview(); else model.StopInputPreview();
    }

    private void Refresh_OnClick(object? sender, RoutedEventArgs e) => _ = (DataContext as VideoOverlayViewModel)?.RefreshCamerasAsync();

    // A window is the layer, so pressing one both selects it and starts the
    // drag. The corners resize; anything else moves.
    private void Window_OnPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.Source is Button) return;
        if (sender is not Border { DataContext: VideoOverlaySlotViewModel { Layer: { } layer } } border ||
            DataContext is not VideoOverlayViewModel model ||
            !e.GetCurrentPoint(border).Properties.IsLeftButtonPressed) return;
        var point = e.GetPosition(border);
        var handle = Math.Min(18, Math.Min(border.Bounds.Width, border.Bounds.Height) / 4);
        _dragMode = point.X < handle
            ? point.Y < handle ? VideoOverlayManipulationMode.TopLeft : point.Y > border.Bounds.Height - handle ? VideoOverlayManipulationMode.BottomLeft : VideoOverlayManipulationMode.Move
            : point.X > border.Bounds.Width - handle
                ? point.Y < handle ? VideoOverlayManipulationMode.TopRight : point.Y > border.Bounds.Height - handle ? VideoOverlayManipulationMode.BottomRight : VideoOverlayManipulationMode.Move
                : VideoOverlayManipulationMode.Move;
        _dragLayer = layer;
        _lastPointer = e.GetPosition(PreviewCanvas);
        model.SelectLayer(layer);
        e.Pointer.Capture(PreviewCanvas);
        e.Handled = true;
    }

    private void PreviewCanvas_OnPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (_dragLayer is null && DataContext is VideoOverlayViewModel model)
        {
            model.DeselectLayer();
            Focus();
        }
    }

    private void Section_OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape) return;
        (DataContext as VideoOverlayViewModel)?.DeselectLayer();
        e.Handled = true;
    }

    private void PreviewCanvas_OnPointerMoved(object? sender, PointerEventArgs e)
    {
        if (_dragLayer is null || DataContext is not VideoOverlayViewModel model) return;
        var point = e.GetPosition(PreviewCanvas);
        var size = PreviewCanvas.Bounds.Size;
        if (size.Width <= 0 || size.Height <= 0) return;
        model.Manipulate(_dragLayer, _dragMode, (point.X - _lastPointer.X) / size.Width, (point.Y - _lastPointer.Y) / size.Height);
        _lastPointer = point;
        e.Handled = true;
    }

    private void PreviewCanvas_OnPointerReleased(object? sender, PointerReleasedEventArgs e) { EndDrag(); e.Pointer.Capture(null); }
    private void PreviewCanvas_OnPointerCaptureLost(object? sender, PointerCaptureLostEventArgs e) => EndDrag();
    private void ResetCamera_OnClick(object? sender, RoutedEventArgs e) => (DataContext as VideoOverlayViewModel)?.ResetToCorner("Camera");
    private void ResetKeyboard_OnClick(object? sender, RoutedEventArgs e) => (DataContext as VideoOverlayViewModel)?.ResetToCorner("Keyboard");
    private void EndDrag() { if (_dragLayer is null) return; (DataContext as VideoOverlayViewModel)?.CommitManipulation(); _dragLayer = null; }
    // The windows are laid out in canvas pixels, so the view model measures in
    // the canvas the user is actually looking at rather than a fixed 960x540.
    private void PreviewCanvas_OnSizeChanged(object? sender, SizeChangedEventArgs e) =>
        (DataContext as VideoOverlayViewModel)?.SetPreviewSize(e.NewSize.Width, e.NewSize.Height);

    private void UpdateNarrowLayout()
    {
        if (DataContext is VideoOverlayViewModel model && PreviewCanvas.Bounds is { Width: > 0, Height: > 0 } bounds)
            model.SetPreviewSize(bounds.Width, bounds.Height);
        WidePreview.IsVisible = Bounds.Width >= 600;
        NarrowPickers.IsVisible = Bounds.Width < 600;
    }
}
