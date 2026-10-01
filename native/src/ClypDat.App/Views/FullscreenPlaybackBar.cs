using Avalonia;
using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Layout;
using Avalonia.Media;
using ClypDat.App.Services;

namespace ClypDat.App.Views;

// Reuses the editor's transport and volume controls. Each group has its own
// grid space; a long title or time readout cannot cover a playback button.
internal sealed class FullscreenPlaybackBar : Grid
{
    private readonly Control _time;
    private readonly Control _volume;

    internal FullscreenPlaybackBar(Control seek, Control transport, Control time,
        Control volume, Control exit)
    {
        _time = time;
        _volume = volume;
        Margin = new Thickness(18, 10);
        RowDefinitions = new RowDefinitions("Auto,16,Auto,Auto,Auto,Auto");
        ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto");

        var title = new TextBlock
        {
            Foreground = AppThemeService.Brush("Text_C4D0DF", "#C4D0DF"),
            FontSize = 12,
            FontWeight = FontWeight.SemiBold,
            TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 12, 0),
        };
        title.Bind(TextBlock.TextProperty, new Binding("EditorTitle"));
        Grid.SetColumnSpan(title, 2);
        Grid.SetColumn(exit, 2);
        Grid.SetRow(seek, 1);
        Grid.SetColumnSpan(seek, 3);
        Grid.SetRow(transport, 2);
        transport.Margin = default;
        transport.HorizontalAlignment = HorizontalAlignment.Left;

        var pan = new Slider { Minimum = -1, Maximum = 1, VerticalAlignment = VerticalAlignment.Center };
        pan.Bind(Slider.ValueProperty, new Binding("VideoPanY") { Mode = BindingMode.TwoWay });
        var panRow = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("Auto,*"),
            Margin = new Thickness(0, 6, 0, 0),
            Children =
            {
                new TextBlock
                {
                    Text = "Vertical pan",
                    FontSize = 12,
                    Foreground = AppThemeService.Brush("Text_C8D6E6", "#C8D6E6"),
                    VerticalAlignment = VerticalAlignment.Center,
                    Margin = new Thickness(0, 0, 12, 0),
                },
                pan,
            },
        };
        Grid.SetColumn(pan, 1);
        panRow.Bind(IsVisibleProperty, new Binding("IsVideoZoomed"));
        Grid.SetRow(panRow, 5);
        Grid.SetColumnSpan(panRow, 3);
        Children.Add(title);
        Children.Add(exit);
        Children.Add(seek);
        Children.Add(transport);
        Children.Add(time);
        Children.Add(volume);
        Children.Add(panRow);
        SetAvailableWidth(FullscreenControlsGeometry.MaximumWidth);
    }

    internal void SetAvailableWidth(double width)
    {
        var volumeBelow = FullscreenControlsGeometry.StackVolume(width);
        var timeBelow = FullscreenControlsGeometry.StackTime(width);
        Grid.SetRow(_time, timeBelow ? 3 : 2);
        Grid.SetColumn(_time, timeBelow ? 0 : 1);
        Grid.SetColumnSpan(_time, timeBelow ? 3 : volumeBelow ? 2 : 1);
        _time.HorizontalAlignment = timeBelow ? HorizontalAlignment.Center : HorizontalAlignment.Right;
        _time.Margin = timeBelow ? new Thickness(0, 4) : new Thickness(12, 0, volumeBelow ? 0 : 16, 0);
        Grid.SetRow(_volume, volumeBelow ? timeBelow ? 4 : 3 : 2);
        Grid.SetColumn(_volume, volumeBelow ? 0 : 2);
        Grid.SetColumnSpan(_volume, volumeBelow ? 3 : 1);
        _volume.HorizontalAlignment = volumeBelow ? HorizontalAlignment.Center : HorizontalAlignment.Right;
        _volume.Margin = volumeBelow ? new Thickness(0, 4, 0, 0) : default;
    }
}
