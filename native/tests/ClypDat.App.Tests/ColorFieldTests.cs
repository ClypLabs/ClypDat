using Avalonia;
using ClypDat.App.Controls;
using Xunit;

namespace ClypDat.App.Tests;

public sealed class HsvColorFieldTests
{
    private static readonly Size Field = new(232, 232);

    [Fact]
    public void CornersMapToTheCroppedRange()
    {
        // The dark base field: full saturation range, value 3-30%.
        Assert.Equal((0, 0.30), SaturationValueField.At(new Point(0, 0), Field, 0, 100, 3, 30));
        Assert.Equal((1, 0.03), Round(SaturationValueField.At(new Point(232, 232), Field, 0, 100, 3, 30)));
    }

    [Fact]
    public void SamePointAlwaysGivesSameColour()
    {
        var first = SaturationValueField.At(new Point(117.4, 3.2), Field, 0, 100, 3, 30);
        for (var i = 0; i < 100; i++) Assert.Equal(first, SaturationValueField.At(new Point(117.4, 3.2), Field, 0, 100, 3, 30));
    }

    [Fact]
    public void DragPastTheEdgeHoldsTheEdge()
    {
        // Pointer captured and dragged above and right of the field, as along
        // the top edge: value stays at the top of the band, never drops.
        for (var x = -40; x <= 280; x += 7)
            Assert.Equal(0.30, SaturationValueField.At(new Point(x, -25), Field, 0, 100, 3, 30).V, 6);
        Assert.Equal(1, SaturationValueField.At(new Point(400, 100), Field, 0, 100, 3, 30).S);
    }

    [Fact]
    public void ValueRisesMonotonicallyUpTheField()
    {
        var previous = -1.0;
        for (var y = 232; y >= 0; y--)
        {
            var v = SaturationValueField.At(new Point(100, y), Field, 0, 100, 3, 30).V;
            Assert.True(v >= previous);
            previous = v;
        }
    }

    [Fact]
    public void HueRunsFromTopToBottomWithoutWrapping()
    {
        Assert.True(HueStrip.At(0, 232) is > 359 and < 360);
        Assert.Equal(180, HueStrip.At(116, 232), 6);
        Assert.Equal(0, HueStrip.At(232, 232));
        Assert.Equal(0, HueStrip.At(500, 232));
    }

    private static (double, double) Round((double S, double V) value) => (Math.Round(value.S, 6), Math.Round(value.V, 6));
}
