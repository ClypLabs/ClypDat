using Avalonia;
using Avalonia.Media;
using ClypDat.App.Services;
using ClypDat.App.ViewModels;
using ClypDat.Core.Settings;
using Xunit;

namespace ClypDat.App.Tests;

public sealed class ThemeColorPickerViewModelTests
{
    [Fact]
    public void CurrentSwatchKeepsTheOpeningColourAndRestoresIt()
    {
        var committed = new List<string>();
        var picker = new ThemeColorPickerViewModel("#112233", committed.Add);

        picker.Set(new ThemeColor(0xAA, 0xBB, 0xCC));
        Assert.Equal(Color.Parse("#AABBCC"), picker.ShownColor);
        Assert.Equal(Color.Parse("#112233"), picker.CurrentColor);

        picker.RestoreCurrent();
        Assert.Equal("#112233", picker.HexText);
        Assert.Equal("#112233", committed[^1]);
    }

    [Fact]
    public void LoadDoesNotMoveTheCurrentSwatchUntilMarked()
    {
        var picker = new ThemeColorPickerViewModel("#112233", _ => { });

        picker.Load("#445566");
        Assert.Equal(Color.Parse("#112233"), picker.CurrentColor);

        picker.MarkCurrent();
        Assert.Equal(Color.Parse("#445566"), picker.CurrentColor);
    }

    [Fact]
    public void LoadAtShowsTheFieldColourThereWithoutReporting()
    {
        var committed = new List<string>();
        var picker = new ThemeColorPickerViewModel("#112233", committed.Add) { Model = AppThemeService.CustomBaseField(light: false) };

        var shown = picker.LoadAt(new Point(0.5, 0.5));
        Assert.Equal(picker.Model!.At(picker.Hue, 0.5, 0.5), shown);
        Assert.Equal(shown, picker.ShownColor);
        Assert.Equal(new Point(0.5, 0.5), picker.Position);
        Assert.Empty(committed);

        picker.MarkCurrent();
        Assert.Equal(shown, picker.CurrentColor);
    }
}
