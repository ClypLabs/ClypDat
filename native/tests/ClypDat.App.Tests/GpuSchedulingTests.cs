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

    [Fact]
    public void CaptureDevicePriority_UsesMaximumDxgiPriority()
    {
        // Device priority is intentionally default-on; only process-wide
        // scheduling needs the explicit environment opt-in above.
        Assert.Equal(7, GpuScheduling.CaptureDevicePriority);
    }
}
