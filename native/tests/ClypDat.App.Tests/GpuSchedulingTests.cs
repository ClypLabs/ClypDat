using ClypDat.App.Services;
using Xunit;

namespace ClypDat.App.Tests;

public sealed class GpuSchedulingTests
{
    [Theory]
    [InlineData(null, true)]
    [InlineData("", true)]
    [InlineData("0", false)]
    [InlineData("normal", false)]
    [InlineData("above-normal", true)]
    [InlineData("high", true)]
    [InlineData("1", true)]
    public void WorkerPriorityUsesNamedDefaultsAndAliases(string? value, bool expected)
    {
        Assert.Equal(expected, GpuScheduling.IsPriorityElevationEnabled(value));
    }

    [Fact]
    public void CaptureDevicePriority_UsesConservativeDxgiPriority()
    {
        // Device priority is intentionally default-on; only process-wide
        // scheduling needs the explicit environment opt-in above.
        Assert.Equal(1, GpuScheduling.CaptureDevicePriority);
    }

    [Theory]
    [InlineData(null, 1)]
    [InlineData("-7", -7)]
    [InlineData("7", 7)]
    [InlineData("8", 1)]
    public void DevicePriorityClampsDiagnosticOverride(string? value, int expected) =>
        Assert.Equal(expected, GpuScheduling.ResolveDevicePriority(value));
}
