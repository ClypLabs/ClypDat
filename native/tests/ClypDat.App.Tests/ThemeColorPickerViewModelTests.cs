using Avalonia.Media;
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
    public void NewSwatchShowsThePaintedColour()
    {
        var picker = new ThemeColorPickerViewModel("#112233", _ => { });

        picker.ShowPainted("#0A0B0C");
        Assert.Equal(Color.Parse("#0A0B0C"), picker.ShownColor);

        picker.MarkCurrent();
        Assert.Equal(Color.Parse("#0A0B0C"), picker.CurrentColor);
    }
}
