using ClypDat.App.Services;
using ClypDat.Capture.Abstractions;
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

    [Theory]
    [InlineData(ReplayHdrCompatibilityStatus.Unavailable, true, "Unavailable")]
    [InlineData(ReplayHdrCompatibilityStatus.SdrDisplay, true, "SDR display")]
    [InlineData(ReplayHdrCompatibilityStatus.ConversionActive, true, "HDR conversion active")]
    [InlineData(ReplayHdrCompatibilityStatus.SdrDisplay, false, "Off")]
    public void PresentsWorkerHdrStatus(ReplayHdrCompatibilityStatus status, bool enabled, string expected) =>
        Assert.Equal(expected, HdrCompatibilityPresentation.Resolve(status, enabled));

    [Fact]
    public void MissingWorkerHdrStatusStaysUnavailableAfterSerialization()
    {
        var received = System.Text.Json.JsonSerializer.Deserialize<ReplayCaptureHealth>("{}")!;

        Assert.Equal(ReplayHdrCompatibilityStatus.Unavailable, received.HdrCompatibilityStatus);
        Assert.Equal("Unavailable", HdrCompatibilityPresentation.Resolve(received.HdrCompatibilityStatus, true));
    }

    [Fact]
    public void ClearedOrReconnectedHealthDoesNotRetainSdrStatus()
    {
        var cleared = ReplayCaptureHealth.Unknown("Worker");

        Assert.Equal("Unavailable", HdrCompatibilityPresentation.Resolve(cleared.HdrCompatibilityStatus, true));
    }
}
