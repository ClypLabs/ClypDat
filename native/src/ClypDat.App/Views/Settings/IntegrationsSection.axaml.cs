using Avalonia.Controls;
using ClypDat.App.ViewModels;

namespace ClypDat.App.Views.Settings;

public sealed partial class IntegrationsSection : UserControl
{
    public IntegrationsSection() => InitializeComponent();
    private async void LinkClypDatButton_OnClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (DataContext is MainWindowViewModel vm) await vm.LinkClypDatAccountAsync();
    }
    private void ConnectSpotifyButton_OnClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (DataContext is MainWindowViewModel vm) _ = vm.ConnectSpotifyAsync();
    }
    private void ConfigureSpotifyButton_OnClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (DataContext is not MainWindowViewModel vm) return;
        if (Avalonia.Controls.TopLevel.GetTopLevel(this) is not Window owner) return;
        _ = new ClypDat.App.Views.SpotifyOverlayDialog(vm).ShowDialog(owner);
    }

    private void DisconnectSpotifyButton_OnClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (DataContext is MainWindowViewModel vm) vm.DisconnectSpotify();
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

    private async void UnlinkSocialAccountButton_OnClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (sender is Button { Tag: string provider } && DataContext is MainWindowViewModel vm) await vm.UnlinkSocialAccountAsync(provider);
    }

    private void OpenSocialAccountButton_OnClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (sender is Button { Tag: string provider } && DataContext is MainWindowViewModel vm) vm.OpenSocialAccount(provider);
    }
}
