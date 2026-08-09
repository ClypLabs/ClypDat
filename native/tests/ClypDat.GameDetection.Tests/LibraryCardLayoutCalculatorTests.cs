using ClypDat.App.ViewModels;
using Xunit;

namespace ClypDat.GameDetection.Tests;

public sealed class LibraryCardLayoutCalculatorTests
{
    [Theory]
    [InlineData(1200)]
    [InlineData(1200.5)]
    [InlineData(1600)]
    [InlineData(1600.75)]
    [InlineData(2400)]
    [InlineData(2400.25)]
    public void Calculate_FullRowsFitInsideViewportWithSafetyReserve(double viewportWidth)
    {
        var layout = LibraryCardLayoutCalculator.Calculate(viewportWidth, scaleWithWindow: true);

        var usedWidth = layout.Columns * (layout.Width + LibraryCardLayoutCalculator.HorizontalMargin);
        Assert.True(usedWidth <= viewportWidth - 1, $"Used {usedWidth}; viewport {viewportWidth}.");
    }

    [Theory]
    [InlineData(799.99, 2)]
    [InlineData(800, 2)]
    [InlineData(1199.99, 2)]
    [InlineData(1200, 3)]
    [InlineData(1599.99, 3)]
    [InlineData(1600, 4)]
    public void Calculate_ScaledModeKeepsExistingColumnThresholds(double viewportWidth, int expectedColumns)
    {
        Assert.Equal(expectedColumns, LibraryCardLayoutCalculator.Calculate(viewportWidth, scaleWithWindow: true).Columns);
    }

    [Theory]
    [InlineData(1000)]
    [InlineData(1200.5)]
    [InlineData(2400)]
    public void Calculate_FixedModeRemainsThreeColumns(double viewportWidth)
    {
        var layout = LibraryCardLayoutCalculator.Calculate(viewportWidth, scaleWithWindow: false);

        Assert.Equal(3, layout.Columns);
        Assert.True(3 * (layout.Width + LibraryCardLayoutCalculator.HorizontalMargin) <= viewportWidth - 1);
    }

    [Fact]
    public void Calculate_IsStableAcrossRepeatedResizeAndSettingChanges()
    {
        var narrow = LibraryCardLayoutCalculator.Calculate(1200.5, scaleWithWindow: true);
        var wide = LibraryCardLayoutCalculator.Calculate(2400.5, scaleWithWindow: true);
        var restored = LibraryCardLayoutCalculator.Calculate(1200.5, scaleWithWindow: true);
        var fixedLayout = LibraryCardLayoutCalculator.Calculate(1200.5, scaleWithWindow: false);

        Assert.Equal(narrow, restored);
        Assert.True(wide.Columns > narrow.Columns);
        Assert.Equal(3, fixedLayout.Columns);
    }
}
