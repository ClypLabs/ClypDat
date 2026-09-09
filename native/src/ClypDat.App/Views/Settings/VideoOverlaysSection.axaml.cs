using System.ComponentModel;
using Avalonia.Controls;
using Avalonia.Interactivity;
using ClypDat.App.ViewModels;

namespace ClypDat.App.Views.Settings;

public sealed partial class VideoOverlaysSection : UserControl
{
    private MainWindowViewModel? _owner;
    public VideoOverlaysSection() { InitializeComponent(); AttachedToVisualTree += Attached; DetachedFromVisualTree += Detached; }
    private void Attached(object? sender, Avalonia.VisualTreeAttachmentEventArgs e)
    {
        _owner = (TopLevel.GetTopLevel(this) as MainWindow)?.DataContext as MainWindowViewModel;
        if (_owner is null) return;
        DataContext = new VideoOverlayViewModel(_owner.Settings.VideoOverlays, _owner.SaveSettings);
        _owner.PropertyChanged += OwnerChanged;
        UpdateVisibility();
    }
    private void Detached(object? sender, Avalonia.VisualTreeAttachmentEventArgs e)
    { if (_owner is not null) _owner.PropertyChanged -= OwnerChanged; _owner = null; }
    private void OwnerChanged(object? sender, PropertyChangedEventArgs e) { if (e.PropertyName == nameof(MainWindowViewModel.SelectedSettingsSection) || e.PropertyName == nameof(MainWindowViewModel.SettingsSearchText)) UpdateVisibility(); }
    private void UpdateVisibility() => IsVisible = _owner?.SelectedSettingsSection == "Video Overlays" || !string.IsNullOrWhiteSpace(_owner?.SettingsSearchText);
    private void Refresh_OnClick(object? sender, RoutedEventArgs e) => _ = (DataContext as VideoOverlayViewModel)?.RefreshCamerasAsync();
    private void Preview_OnClick(object? sender, RoutedEventArgs e) => (DataContext as VideoOverlayViewModel)?.TogglePreview();
    private void CameraCorner_OnClick(object? sender, RoutedEventArgs e) { if (sender is Button { Tag: string corner }) (DataContext as VideoOverlayViewModel)?.SetCorner(corner, true); }
}
