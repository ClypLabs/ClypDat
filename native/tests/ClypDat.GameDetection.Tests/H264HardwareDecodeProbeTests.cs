using ClypDat.App.Services;
using Xunit;

namespace ClypDat.GameDetection.Tests;

public sealed class H264HardwareDecodeProbeTests
{
    [Fact]
    public void DetectsAnnexBIdr()
    {
        Assert.True(H264HardwareDecodeProbe.ContainsIdrPayload(new byte[] { 0, 0, 0, 1, 0x65, 0x88 }));
    }

    [Fact]
    public void DetectsAvccIdr()
    {
        Assert.True(H264HardwareDecodeProbe.ContainsIdrPayload(new byte[] { 0, 0, 0, 2, 0x65, 0x88 }));
    }

    [Fact]
    public void RejectsNonIdrRandomAccessPayload()
    {
        Assert.False(H264HardwareDecodeProbe.ContainsIdrPayload(new byte[] { 0, 0, 0, 2, 0x41, 0x88 }));
    }
}
