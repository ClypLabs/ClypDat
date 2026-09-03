using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using ClypDat.App.ViewModels;

namespace ClypDat.App.Views.Settings;

public sealed partial class OverlaysSection : UserControl
{
    public OverlaysSection()
    {
        InitializeComponent();
    }

    private void OverlayPreview_OnClick(object? sender, RoutedEventArgs e)
        => (DataContext as MainWindowViewModel)?.RequestOverlayPreview();
}
