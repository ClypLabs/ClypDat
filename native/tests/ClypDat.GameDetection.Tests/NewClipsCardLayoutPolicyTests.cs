using ClypDat.App.Services;
using Xunit;

namespace ClypDat.GameDetection.Tests;

public sealed class NewClipsCardLayoutPolicyTests
{
    [Theory]
    [InlineData(1, new[] { 1 })]
    [InlineData(2, new[] { 2 })]
    [InlineData(3, new[] { 3 })]
    [InlineData(4, new[] { 3, 1 })]
    [InlineData(6, new[] { 3, 3 })]
    [InlineData(7, new[] { 3, 3, 1 })]
    public void CreateRowLengths_CapsRowsAtThreeAndLeavesTheFinalRowIntact(int clipCount, int[] expected)
    {
        var rows = NewClipsCardLayoutPolicy.CreateRowLengths(clipCount);

        Assert.Equal(expected, rows);
        Assert.All(rows, row => Assert.InRange(row, 1, NewClipsCardLayoutPolicy.CardsPerRow));
    }
}
