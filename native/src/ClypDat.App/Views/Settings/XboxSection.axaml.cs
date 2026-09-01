using Avalonia.Controls;
using ClypDat.App.ViewModels;

namespace ClypDat.App.Views.Settings;

public sealed partial class XboxSection : UserControl
{
    public XboxSection() => InitializeComponent();
    private async void LinkClypDatButton_OnClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (DataContext is MainWindowViewModel vm) await vm.LinkClypDatAccountAsync();
    }
    private void CreateClypDatButton_OnClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (DataContext is MainWindowViewModel vm) vm.OpenClypDatAccount();
    }
    private void UnlinkClypDatButton_OnClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (DataContext is MainWindowViewModel vm) vm.SignOutClypDatAccount();
    }
    private async void UnlinkClypDatXboxButton_OnClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (DataContext is MainWindowViewModel vm) await vm.UnlinkClypDatXboxAsync();
    }

    private async void RefreshClypDatButton_OnClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (DataContext is MainWindowViewModel vm) await vm.RefreshClypDatAccountAsync();
    }

    private void OpenSocialAccountButton_OnClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (sender is Button { Tag: string provider } && DataContext is MainWindowViewModel vm) vm.OpenSocialAccount(provider);
    }
}
