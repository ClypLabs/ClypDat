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

    [Fact]
    public void PacketIndexProbe_HandlesAvccAndReadsOnlyBoundedPrefixes()
    {
        var path = Path.GetTempFileName();
        try
        {
            var packet = new byte[70 * 1024];
            packet[3] = 2;
            packet[4] = 0x65;
            File.WriteAllBytes(path, packet);

            Assert.True(H264HardwareDecodeProbe.HasOnlyIdrRandomAccessPoints(
                path, H264PacketFormat.Avcc, new[] { new H264KeyPacket(0, packet.Length) }));
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void PacketIndexProbe_RejectsMalformedPacketMetadata()
    {
        var path = Path.GetTempFileName();
        try
        {
            File.WriteAllBytes(path, new byte[] { 0, 0, 0, 2, 0x65, 0x88 });
            Assert.False(H264HardwareDecodeProbe.HasOnlyIdrRandomAccessPoints(
                path, H264PacketFormat.Avcc, new[] { new H264KeyPacket(100, 6) }));
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void PacketIndexProbe_RejectsNonIdrAnnexBKeyPacket()
    {
        var path = Path.GetTempFileName();
        try
        {
            File.WriteAllBytes(path, new byte[] { 0, 0, 0, 1, 0x41, 0x88 });
            Assert.False(H264HardwareDecodeProbe.HasOnlyIdrRandomAccessPoints(
                path, H264PacketFormat.AnnexB, new[] { new H264KeyPacket(0, 6) }));
        }
        finally { File.Delete(path); }
    }
}
