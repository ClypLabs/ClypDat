using System.Diagnostics;
using Avalonia.Controls;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using ClypDat.App.Services;
using ClypDat.App.ViewModels;

namespace ClypDat.App.Views;

public sealed partial class SharedClipsView : UserControl
{
    public SharedClipsView() => InitializeComponent();

    private MainWindowViewModel? ViewModel => DataContext as MainWindowViewModel;

    private async void Refresh_OnClick(object? sender, RoutedEventArgs e)
    {
        if (ViewModel is { } vm) await vm.LoadSharedClipsAsync();
    }

    private void Open_OnClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: SharedClipViewModel clip }) OpenClip(clip);
    }

    // The thumbnail opens the clip too, like a library tile opens its clip.
    private void Thumbnail_OnPointerPressed(object? sender, Avalonia.Input.PointerPressedEventArgs e)
    {
        if (sender is not Control { Tag: SharedClipViewModel clip } control || !e.GetCurrentPoint(control).Properties.IsLeftButtonPressed) return;
        e.Handled = true;
        OpenClip(clip);
    }

    private static void OpenClip(SharedClipViewModel clip)
    {
        if (!Uri.TryCreate(clip.Url, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps) return;
        try { Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true }); }
        catch (Exception error) { AppLog.Error("Shared clips: could not open the clip page", error); }
    }

    private async void Copy_OnClick(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: SharedClipViewModel clip } || TopLevel.GetTopLevel(this)?.Clipboard is not { } clipboard) return;
        try
        {
            await clipboard.SetTextAsync(clip.Url);
            clip.Copied = true;
            await Task.Delay(TimeSpan.FromSeconds(2));
            clip.Copied = false;
        }
        catch (Exception error)
        {
            AppLog.Error("Shared clips: could not copy the link", error);
        }
    }

    private async void Delete_OnClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: SharedClipViewModel clip } && ViewModel is { } vm) await vm.DeleteSharedClipAsync(clip);
    }
}
