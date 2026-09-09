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

    private void UpdateVisibility() => IsVisible = _owner?.SelectedSettingsSection == "Video Overlays" || !string.IsNullOrWhiteSpace(_owner?.SettingsSearchText);

    private void Refresh_OnClick(object? sender, RoutedEventArgs e) => _ = (DataContext as VideoOverlayViewModel)?.RefreshCamerasAsync();

    private void Layer_OnPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.Source is Button) return;
        if (sender is not Border { Tag: string layer } border || DataContext is not VideoOverlayViewModel model || !e.GetCurrentPoint(border).Properties.IsLeftButtonPressed) return;
        var point = e.GetPosition(border);
        const double handle = 18;
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

    private void PickerPreview_OnPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is Border { Tag: string corner } && DataContext is VideoOverlayViewModel model &&
            e.GetCurrentPoint(sender as Visual).Properties.IsLeftButtonPressed)
        {
            model.SelectSourceAt(corner);
            Focus();
            e.Handled = true;
        }
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
        model.Manipulate(_dragLayer, _dragMode, (point.X - _lastPointer.X) / 960, (point.Y - _lastPointer.Y) / 540);
        _lastPointer = point;
        e.Handled = true;
    }

    private void PreviewCanvas_OnPointerReleased(object? sender, PointerReleasedEventArgs e) { EndDrag(); e.Pointer.Capture(null); }
    private void PreviewCanvas_OnPointerCaptureLost(object? sender, PointerCaptureLostEventArgs e) => EndDrag();
    private void ResetCamera_OnClick(object? sender, RoutedEventArgs e) => (DataContext as VideoOverlayViewModel)?.ResetToCorner("Camera");
    private void ResetKeyboard_OnClick(object? sender, RoutedEventArgs e) => (DataContext as VideoOverlayViewModel)?.ResetToCorner("Keyboard");
    private void EndDrag() { if (_dragLayer is null) return; (DataContext as VideoOverlayViewModel)?.CommitManipulation(); _dragLayer = null; }
    private void UpdateNarrowLayout()
    {
        if (DataContext is VideoOverlayViewModel model) model.SetPreviewSize(960, 540);
        WidePickers.IsVisible = Bounds.Width >= 600;
        NarrowPickers.IsVisible = Bounds.Width < 600;
    }
}
