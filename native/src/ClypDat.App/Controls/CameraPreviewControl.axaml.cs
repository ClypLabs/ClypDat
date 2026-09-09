using Avalonia.Controls;
using Avalonia.Interactivity;
using ClypDat.App.ViewModels;

namespace ClypDat.App.Controls;

public sealed partial class CameraPreviewControl : UserControl
{
    public CameraPreviewControl() => InitializeComponent();
    private void ShowPreview_OnClick(object? sender, RoutedEventArgs e) { (DataContext as VideoOverlayViewModel)?.StartCameraPreview(); e.Handled = true; }
    private void HidePreview_OnClick(object? sender, RoutedEventArgs e) { (DataContext as VideoOverlayViewModel)?.StopCameraPreview(); e.Handled = true; }
}
