using ClypDat.App.Services;
using Vortice.DXGI;
using Xunit;

namespace ClypDat.App.Tests;

public sealed class HdrCaptureCompatibilityTests
{
    [Theory]
    [InlineData(1)]
    [InlineData(12)]
    [InlineData(14)]
    [InlineData(18)]
    [InlineData(25)]
    public void DetectsHdrColorSpaces(int value) =>
        Assert.True(HdrCaptureCompatibility.IsHdrColorSpace((ColorSpaceType)value));

    [Theory]
    [InlineData(0)]
    [InlineData(2)]
    [InlineData(9)]
    [InlineData(17)]
    public void DoesNotDetectSdrColorSpaces(int value) =>
        Assert.False(HdrCaptureCompatibility.IsHdrColorSpace((ColorSpaceType)value));
}
