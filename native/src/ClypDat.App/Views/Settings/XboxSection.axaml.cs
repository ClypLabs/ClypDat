using Avalonia.Controls;
using ClypDat.App.ViewModels;

namespace ClypDat.App.Views.Settings;

public sealed partial class XboxSection : UserControl
{
    public XboxSection() => InitializeComponent();
    private async void LinkButton_OnClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (DataContext is MainWindowViewModel vm) await vm.LinkXboxAsync();
    }
    private void UnlinkButton_OnClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (DataContext is MainWindowViewModel vm) vm.UnlinkXbox();
    }
}
