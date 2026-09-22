using ClypDat.App.Services;
using ClypDat.Capture.Abstractions;
using System.Runtime.InteropServices;
using Vortice.DXGI;
using Xunit;

namespace ClypDat.App.Tests;

public sealed class HdrCaptureCompatibilityTests
{
    [Theory]
    [InlineData(1)]
    public void DetectsHdrColorSpaces(int value) =>
        Assert.True(HdrCaptureCompatibility.IsHdrColorSpace((ColorSpaceType)value));

    [Theory]
    [InlineData(0)]
    public void DoesNotDetectSdrColorSpaces(int value) =>
        Assert.False(HdrCaptureCompatibility.IsHdrColorSpace((ColorSpaceType)value));

    [Theory]
    [InlineData(ReplayHdrCompatibilityStatus.Unavailable, true, "HDR status unknown")]
    public void PresentsWorkerHdrStatus(ReplayHdrCompatibilityStatus status, bool enabled, string expected) =>
        Assert.Equal(expected, HdrCompatibilityPresentation.Resolve(status, enabled));

    [Fact]
    public void DisplayConfigStructsMatchWin32Layouts()
    {
        Assert.Equal(20, Marshal.SizeOf<HdrCaptureCompatibility.DisplayConfigSourceInfo>());
        Assert.Equal(48, Marshal.SizeOf<HdrCaptureCompatibility.DisplayConfigTargetInfo>());
        Assert.Equal(72, Marshal.SizeOf<HdrCaptureCompatibility.DisplayConfigPathInfo>());
        Assert.Equal(64, Marshal.SizeOf<HdrCaptureCompatibility.DisplayConfigModeInfo>());
        Assert.Equal(84, Marshal.SizeOf<HdrCaptureCompatibility.DisplayConfigSourceName>());
        Assert.Equal(24, Marshal.SizeOf<HdrCaptureCompatibility.DisplayConfigSdrWhiteLevel>());
        Assert.Equal(32, Marshal.SizeOf<HdrCaptureCompatibility.DisplayConfigAdvancedColorInfo>());
        Assert.Equal(20, (int)Marshal.OffsetOf<HdrCaptureCompatibility.DisplayConfigPathInfo>(nameof(HdrCaptureCompatibility.DisplayConfigPathInfo.TargetInfo)));
    }

    [Theory]
    [InlineData(0x3u, true)]
    public void HdrFollowsAdvancedColourEnabledNotAutoColourManagement(uint value, bool expected) =>
        Assert.Equal(expected, HdrCaptureCompatibility.IsHdrActive(value));
}
