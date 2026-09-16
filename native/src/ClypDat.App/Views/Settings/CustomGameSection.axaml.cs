using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using ClypDat.App.ViewModels;

namespace ClypDat.App.Views.Settings;

public sealed partial class CustomGameSection : UserControl
{
    public CustomGameSection()
    {
        InitializeComponent();
    }

    // Settings markup lives here, but the handlers still belong to
    // MainWindow - their bodies reach all over its state. These forward
    // to the owning window rather than duplicating any of it.
    private MainWindow? Owner => TopLevel.GetTopLevel(this) as MainWindow;

    private void AddCustomGameComboBox_OnSelectionChanged(object? sender, SelectionChangedEventArgs e)
        => Owner?.AddCustomGameComboBox_OnSelectionChanged(sender, e);

    private void CustomGameTab_OnClick(object? sender, RoutedEventArgs e)
        => Owner?.CustomGameTab_OnClick(sender, e);

    private void RemoveCustomGameGroupButton_OnClick(object? sender, RoutedEventArgs e)
        => Owner?.RemoveCustomGameGroupButton_OnClick(sender, e);

    private void AddCustomGameSettingButton_OnClick(object? sender, RoutedEventArgs e)
        => Owner?.AddCustomGameSettingButton_OnClick(sender, e);

    private void AddCustomGameGroupMenuItem_OnClick(object? sender, RoutedEventArgs e)
        => Owner?.AddCustomGameGroupMenuItem_OnClick(sender, e);

    private void DeleteCustomGameButton_OnClick(object? sender, RoutedEventArgs e)
        => Owner?.DeleteCustomGameButton_OnClick(sender, e);

    private void HotkeyCaptureButton_OnClick(object? sender, RoutedEventArgs e)
        => Owner?.HotkeyCaptureButton_OnClick(sender, e);

    private void ResetGameAudioVolumeButton_OnClick(object? sender, RoutedEventArgs e)
        => Owner?.ResetGameAudioVolumeButton_OnClick(sender, e);

    private void ResetMicrophoneVolumeButton_OnClick(object? sender, RoutedEventArgs e)
        => Owner?.ResetMicrophoneVolumeButton_OnClick(sender, e);

    private void ResetAppVolumeButton_OnClick(object? sender, RoutedEventArgs e)
        => Owner?.ResetAppVolumeButton_OnClick(sender, e);

    // The game tab strip scrolls sideways, and a strip that scrolls has to say
    // so: without this the tab under the viewport edge is sliced mid-word and
    // reads as a rendering fault rather than as "there are more games". An
    // opacity mask rather than a gradient overlay, because the page background
    // is recoloured per theme - a faked fade would only match one of them.
    private const double TabStripFadeWidth = 28;

    private void GameTabScroll_OnScrollChanged(object? sender, ScrollChangedEventArgs e) => UpdateTabStripFade();

    private void GameTabScroll_OnSizeChanged(object? sender, SizeChangedEventArgs e) => UpdateTabStripFade();

    private void UpdateTabStripFade()
    {
        var scroll = GameTabScroll;
        var width = scroll.Viewport.Width;
        var maxOffset = scroll.Extent.Width - width;

        // Nothing hidden means nothing to fade, and a mask that is on while
        // everything fits would just dim the first and last tab for no reason.
        if (width <= 0 || maxOffset <= 0.5)
        {
            scroll.OpacityMask = null;
            return;
        }

        var fade = Math.Min(TabStripFadeWidth, width / 3) / width;
        var fadeLeft = scroll.Offset.X > 0.5;
        var fadeRight = scroll.Offset.X < maxOffset - 0.5;

        scroll.OpacityMask = new LinearGradientBrush
        {
            StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
            EndPoint = new RelativePoint(1, 0, RelativeUnit.Relative),
            GradientStops =
            {
                new GradientStop(fadeLeft ? Colors.Transparent : Colors.White, 0),
                new GradientStop(Colors.White, fadeLeft ? fade : 0),
                new GradientStop(Colors.White, fadeRight ? 1 - fade : 1),
                new GradientStop(fadeRight ? Colors.Transparent : Colors.White, 1)
            }
        };
    }
}
