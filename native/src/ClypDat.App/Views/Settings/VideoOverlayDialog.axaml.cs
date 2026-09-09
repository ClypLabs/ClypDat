using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using ClypDat.App.ViewModels;
using ClypDat.Core.Settings;

namespace ClypDat.App.Views.Settings;

public sealed partial class VideoOverlayDialog : Window
{
    private string? _dragLayer;
    private VideoOverlayManipulationMode _dragMode;
    private Point _lastPointer;

    public VideoOverlayDialog() => InitializeComponent();

    public VideoOverlayDialog(VideoOverlayViewModel model) : this()
    {
        DataContext = model;
        model.SetPreviewSize(518, 291);
        Closed += (_, _) => { EndDrag(); model.ClosePreview(); };
    }

    private void TitleBar_OnPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) BeginMoveDrag(e);
    }

    private void Dialog_OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape) return;
        e.Handled = true;
        Close();
    }

    private void Done_OnClick(object? sender, RoutedEventArgs e) => Close();
    private void Refresh_OnClick(object? sender, RoutedEventArgs e) => _ = (DataContext as VideoOverlayViewModel)?.RefreshCamerasAsync();

    private void Layer_OnPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.Source is Button) return;
        if (sender is not Border { Tag: string layer } border || DataContext is not VideoOverlayViewModel model || !e.GetCurrentPoint(border).Properties.IsLeftButtonPressed) return;
        var point = e.GetPosition(border);
        const double handle = 14;
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

    private void PreviewCanvas_OnPointerMoved(object? sender, PointerEventArgs e)
    {
        if (_dragLayer is null || DataContext is not VideoOverlayViewModel model) return;
        var point = e.GetPosition(PreviewCanvas);
        model.Manipulate(_dragLayer, _dragMode, (point.X - _lastPointer.X) / 518, (point.Y - _lastPointer.Y) / 291);
        _lastPointer = point;
        e.Handled = true;
    }

    private void PreviewCanvas_OnPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        EndDrag();
        e.Pointer.Capture(null);
        e.Handled = true;
    }

    private void PreviewCanvas_OnPointerCaptureLost(object? sender, PointerCaptureLostEventArgs e) => EndDrag();
    private void EndDrag() { if (_dragLayer is null) return; (DataContext as VideoOverlayViewModel)?.CommitManipulation(); _dragLayer = null; }
}
