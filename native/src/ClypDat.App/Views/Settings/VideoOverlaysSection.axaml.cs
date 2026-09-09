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

    public VideoOverlaysSection() { InitializeComponent(); AttachedToVisualTree += Attached; DetachedFromVisualTree += Detached; }
    private void Attached(object? sender, Avalonia.VisualTreeAttachmentEventArgs e)
    {
        _owner = (TopLevel.GetTopLevel(this) as MainWindow)?.DataContext as MainWindowViewModel;
        if (_owner is null) return;
        DataContext = new VideoOverlayViewModel(_owner.Settings.VideoOverlays, _owner.SaveSettings);
        _owner.PropertyChanged += OwnerChanged; UpdateVisibility();
    }
    private void Detached(object? sender, Avalonia.VisualTreeAttachmentEventArgs e)
    {
        EndDrag(); (DataContext as VideoOverlayViewModel)?.ClosePreview();
        if (_owner is not null) _owner.PropertyChanged -= OwnerChanged; _owner = null;
    }
    private void OwnerChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(MainWindowViewModel.SelectedSettingsSection) or nameof(MainWindowViewModel.SettingsSearchText)) UpdateVisibility();
    }
    private void UpdateVisibility()
    {
        var visible = _owner?.SelectedSettingsSection == "Video Overlays" || !string.IsNullOrWhiteSpace(_owner?.SettingsSearchText);
        IsVisible = visible;
        if (!visible) (DataContext as VideoOverlayViewModel)?.ClosePreview();
    }
    private void Refresh_OnClick(object? sender, RoutedEventArgs e) => _ = (DataContext as VideoOverlayViewModel)?.RefreshCamerasAsync();
    private void Preview_OnClick(object? sender, RoutedEventArgs e) => (DataContext as VideoOverlayViewModel)?.TogglePreview();
    private void PreviewCanvas_OnSizeChanged(object? sender, SizeChangedEventArgs e) => (DataContext as VideoOverlayViewModel)?.SetPreviewSize(e.NewSize.Width, e.NewSize.Height);
    private void Layer_OnPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is not Border { Tag: string layer } border || DataContext is not VideoOverlayViewModel model) return;
        var point = e.GetPosition(border); const double handle = 14;
        _dragMode = point.X < handle ? point.Y < handle ? VideoOverlayManipulationMode.TopLeft : point.Y > border.Bounds.Height - handle ? VideoOverlayManipulationMode.BottomLeft : VideoOverlayManipulationMode.Move : point.X > border.Bounds.Width - handle ? point.Y < handle ? VideoOverlayManipulationMode.TopRight : point.Y > border.Bounds.Height - handle ? VideoOverlayManipulationMode.BottomRight : VideoOverlayManipulationMode.Move : VideoOverlayManipulationMode.Move;
        _dragLayer = layer; _lastPointer = e.GetPosition(PreviewCanvas); model.SelectLayer(layer);
        e.Pointer.Capture(PreviewCanvas); e.Handled = true;
    }
    private void PreviewCanvas_OnPointerMoved(object? sender, PointerEventArgs e)
    {
        if (_dragLayer is null || DataContext is not VideoOverlayViewModel model || PreviewCanvas.Bounds.Width <= 0 || PreviewCanvas.Bounds.Height <= 0) return;
        var point = e.GetPosition(PreviewCanvas);
        model.Manipulate(_dragLayer, _dragMode, (point.X - _lastPointer.X) / PreviewCanvas.Bounds.Width, (point.Y - _lastPointer.Y) / PreviewCanvas.Bounds.Height);
        _lastPointer = point;
    }
    private void PreviewCanvas_OnPointerReleased(object? sender, PointerReleasedEventArgs e) { EndDrag(); e.Pointer.Capture(null); }
    private void PreviewCanvas_OnPointerCaptureLost(object? sender, PointerCaptureLostEventArgs e) => EndDrag();
    private void EndDrag() { if (_dragLayer is null) return; (DataContext as VideoOverlayViewModel)?.CommitManipulation(); _dragLayer = null; }
}
