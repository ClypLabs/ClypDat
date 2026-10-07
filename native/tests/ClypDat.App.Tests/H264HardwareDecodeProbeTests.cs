using ClypDat.App.Services;
using Xunit;

namespace ClypDat.App.Tests;

public sealed class H264HardwareDecodeProbeTests
{
    [Theory]
    [InlineData("avc1")]
    [InlineData("AVC3")]
    public void Mp4SampleEntriesAreLengthPrefixed(string tag) =>
        Assert.Equal(H264PacketFormat.Avcc, H264HardwareDecodeProbe.PacketFormatFor(tag));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("[27][0][0][0]")]
    public void UnknownContainersAreNotGuessed(string? tag) =>
        Assert.Null(H264HardwareDecodeProbe.PacketFormatFor(tag));

    // The shape of a ClypDat replay key packet: two SEI units, then the IDR slice.
    [Fact]
    public void FindsIdrAfterLeadingSeiUnits()
    {
        byte[] packet = [0, 0, 0, 2, 0x06, 0xAA, 0, 0, 0, 2, 0x06, 0xBB, 0, 0, 0, 3, 0x65, 0x88, 0x80];
        Assert.True(H264HardwareDecodeProbe.ContainsIdrPayload(packet, H264PacketFormat.Avcc));
    }

    // Only a prefix of each key packet is read; the IDR slice runs past it.
    [Fact]
    public void FindsIdrWhoseSliceRunsPastThePrefix()
    {
        byte[] prefix = [0, 0, 0, 2, 0x06, 0xAA, 0, 0x01, 0x80, 0x00, 0x65, 0x88, 0x80];
        Assert.True(H264HardwareDecodeProbe.ContainsIdrPayload(prefix, H264PacketFormat.Avcc));
    }

    [Fact]
    public void RecoveryPointIntraFrameIsNotIdr()
    {
        byte[] packet = [0, 0, 0, 2, 0x06, 0xAA, 0, 0, 0, 3, 0x41, 0x9A, 0x00];
        Assert.False(H264HardwareDecodeProbe.ContainsIdrPayload(packet, H264PacketFormat.Avcc));
    }
}
