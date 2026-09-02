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
        Assert.Equal(1, GpuScheduling.CaptureDevicePriority);
    }

    [Theory]
    [InlineData(null, 1)]
    [InlineData("-7", -7)]
    [InlineData("7", 7)]
    [InlineData("8", 1)]
    public void DevicePriorityClampsDiagnosticOverride(string? value, int expected) =>
        Assert.Equal(expected, GpuScheduling.ResolveDevicePriority(value));

    [Theory]
    [InlineData(null, "REALTIME")]
    [InlineData("realtime", "REALTIME")]
    [InlineData("high", "HIGH")]
    [InlineData("1", "HIGH")]
    [InlineData("above-normal", "ABOVE_NORMAL")]
    [InlineData("normal", null)]
    public void ResolvesWorkerProcessPriority(string? value, string? expected) =>
        Assert.Equal(expected, GpuScheduling.ResolveProcessPriority(value));

    [Fact]
    public void RecorderUsesDistinctAppHostWhenPackaged()
    {
        const string app = @"C:\ClypDat\ClypDat.exe";
        Assert.Equal(@"C:\ClypDat\ClypDatRecorder.exe",
            CaptureWorkerExecutable.Resolve(app, path => path.EndsWith(CaptureWorkerExecutable.FileName, StringComparison.Ordinal)));
        Assert.Equal(app, CaptureWorkerExecutable.Resolve(app, _ => false));
    }
}
