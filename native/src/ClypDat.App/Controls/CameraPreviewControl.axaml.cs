using Avalonia.Controls;
using Avalonia.VisualTree;
using Avalonia.Interactivity;
using ClypDat.App.ViewModels;

namespace ClypDat.App.Controls;

public sealed partial class CameraPreviewControl : UserControl
{
    private VideoOverlayViewModel? _preview;

    public CameraPreviewControl()
    {
        InitializeComponent();
        DataContextChanged += (_, _) => UpdatePreviewSubscription();
        AttachedToVisualTree += (_, _) => UpdatePreviewSubscription();
        DetachedFromVisualTree += (_, _) => UpdatePreviewSubscription();
    }

    private void UpdatePreviewSubscription()
    {
        if (_preview is not null) _preview.CameraPreviewFrameUpdated -= Preview_FrameUpdated;
        _preview = VisualExtensions.IsAttachedToVisualTree(this) ? DataContext as VideoOverlayViewModel : null;
        if (_preview is not null) _preview.CameraPreviewFrameUpdated += Preview_FrameUpdated;
    }

    private void Preview_FrameUpdated() => PreviewImage.InvalidateVisual();
    private void ShowPreview_OnClick(object? sender, RoutedEventArgs e) { (DataContext as VideoOverlayViewModel)?.StartCameraPreview(); e.Handled = true; }
    private void HidePreview_OnClick(object? sender, RoutedEventArgs e) { (DataContext as VideoOverlayViewModel)?.StopCameraPreview(); e.Handled = true; }
}
