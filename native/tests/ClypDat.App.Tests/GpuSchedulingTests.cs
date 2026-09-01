using ClypDat.App.Services;
using Xunit;

namespace ClypDat.App.Tests;

public sealed class GpuSchedulingTests
{
    [Theory]
    [InlineData(null, false)]
    [InlineData("", false)]
    [InlineData("0", false)]
    [InlineData("true", false)]
    [InlineData("1", true)]
    public void PriorityElevationRequiresExplicitOptIn(string? value, bool expected)
    {
        Assert.Equal(expected, GpuScheduling.IsPriorityElevationEnabled(value));
    }
}
