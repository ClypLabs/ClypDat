using System.Buffers.Binary;
using System.Text;
using ClypDat.App.Services;
using Xunit;

namespace ClypDat.App.Tests;

public sealed class OscPacketDecoderTests
{
    [Theory]
    [InlineData(",", 0)]
    [InlineData(",T", 0)]
    [InlineData(",i", 1)]
    [InlineData(",i", 120)]
    [InlineData(",f", 0.5)]
    public void ClipPressesSaveWithoutDurationOverride(string tags, double value)
    {
        Assert.True(OscPacketDecoder.TryDecode(OscTestPackets.Message("/clypdat/clip", tags, value), out var commands));
        Assert.Equal(new OscCommand(OscCommandKind.Clip), Assert.Single(commands));
    }

    [Theory]
    [InlineData(",F", 0)]
    [InlineData(",i", 0)]
    [InlineData(",i", -1)]
    [InlineData(",f", 0)]
    [InlineData(",f", -0.5)]
    public void ClipReleasesAreIgnored(string tags, double value)
    {
        Assert.True(OscPacketDecoder.TryDecode(OscTestPackets.Message("/clypdat/clip", tags, value), out var commands));
        Assert.Empty(commands);
    }

    [Theory]
    [InlineData(30)] [InlineData(60)] [InlineData(120)]
    [InlineData(180)] [InlineData(240)] [InlineData(300)]
    public void DurationAcceptsOnlyPresetsAsIntOrIntegralFloat(int seconds)
    {
        foreach (var tags in new[] { ",i", ",f" })
        {
            Assert.True(OscPacketDecoder.TryDecode(OscTestPackets.Duration(seconds, tags), out var commands));
            Assert.Equal(new OscCommand(OscCommandKind.ReplayDuration, seconds), Assert.Single(commands));
        }
    }

    [Theory]
    [InlineData(0)] [InlineData(-30)] [InlineData(31)] [InlineData(60.5)]
    [InlineData(double.NaN)] [InlineData(double.PositiveInfinity)]
    public void InvalidDurationRejectsWholePacket(double value) => Rejected(OscTestPackets.Duration(value, ",f"));

    [Theory]
    [InlineData("/clypdat/clip", ",s")]
    [InlineData("/clypdat/clip", ",b")]
    [InlineData("/clypdat/clip", ",ii")]
    [InlineData("/clypdat/replay-duration", ",")]
    [InlineData("/clypdat/replay-duration", ",T")]
    [InlineData("/clypdat/replay-duration", ",s")]
    [InlineData("/clypdat/clip", ",d")]
    public void WrongArgumentsRejectWholePacket(string address, string tags) => Rejected(OscTestPackets.Message(address, tags, 60));

    [Fact]
    public void NonFiniteClipPressesRejectWholePacket()
    {
        Rejected(OscTestPackets.Message("/clypdat/clip", ",f", double.NaN));
        Rejected(OscTestPackets.Message("/clypdat/clip", ",f", double.PositiveInfinity));
    }

    [Theory]
    [InlineData("/ClypDat/clip")]
    [InlineData("/clypdat/Clip")]
    [InlineData("/clypdat/*")]
    [InlineData("/other/clip")]
    public void AddressesAreExactAndCaseSensitive(string address)
    {
        Assert.True(OscPacketDecoder.TryDecode(OscTestPackets.Message(address), out var commands));
        Assert.Empty(commands);
    }

    [Fact]
    public void ImmediateNestedBundlesPreserveOrder()
    {
        var packet = OscTestPackets.Bundle(OscTestPackets.Duration(30),
            OscTestPackets.Bundle(OscTestPackets.Clip(), OscTestPackets.Duration(120)), OscTestPackets.Clip());
        Assert.True(OscPacketDecoder.TryDecode(packet, out var commands));
        Assert.Equal(new[] { new OscCommand(OscCommandKind.ReplayDuration, 30), new(OscCommandKind.Clip),
            new(OscCommandKind.ReplayDuration, 120), new(OscCommandKind.Clip) }, commands);
    }

    [Fact]
    public void ScheduledBundlesIncludingNestedOnesRejectEarlierCommands()
    {
        foreach (var timetag in new ulong[] { 0, 2, ulong.MaxValue })
        {
            var scheduled = OscTestPackets.Bundle(OscTestPackets.Clip());
            BinaryPrimitives.WriteUInt64BigEndian(scheduled.AsSpan(8, 8), timetag);
            Rejected(scheduled);
            Rejected(OscTestPackets.Bundle(OscTestPackets.Duration(30), scheduled));
        }
    }

    [Fact]
    public void TruncatedMessagesBundlesAndNonZeroPaddingNeverPartlyDispatch()
    {
        var valid = OscTestPackets.Bundle(OscTestPackets.Duration(30), OscTestPackets.Clip());
        for (var length = 0; length < valid.Length; length++)
        {
            // The prefix ending just after a complete first element is a valid
            // smaller bundle; every other truncation is malformed.
            if (length == 16 || length == 20 + OscTestPackets.Duration(30).Length) continue;
            Rejected(valid[..length]);
        }
        var badPadding = OscTestPackets.Clip();
        badPadding[^1] = 1;
        Rejected(OscTestPackets.Bundle(OscTestPackets.Duration(30), badPadding));
        var trailing = OscTestPackets.Clip().Concat(new byte[4]).ToArray();
        Rejected(trailing);
        var missingTypes = OscTestPackets.Clip()[..16];
        Rejected(missingTypes);
        var badSize = (byte[])valid.Clone();
        BinaryPrimitives.WriteInt32BigEndian(badSize.AsSpan(16, 4), int.MaxValue);
        Rejected(badSize);
        BinaryPrimitives.WriteInt32BigEndian(badSize.AsSpan(16, 4), 0);
        Rejected(badSize);
    }

    [Fact]
    public void PacketMessageAndNestingLimitsAreEnforced()
    {
        Rejected(new byte[OscPacketDecoder.MaximumPacketBytes + 4]);
        var thirtyTwo = OscTestPackets.Bundle(Enumerable.Range(0, 32).Select(_ => OscTestPackets.Clip()).ToArray());
        Assert.True(OscPacketDecoder.TryDecode(thirtyTwo, out var commands));
        Assert.Equal(32, commands.Count);
        Rejected(OscTestPackets.Bundle(Enumerable.Range(0, 33).Select(_ => OscTestPackets.Clip()).ToArray()));
        var nested = OscTestPackets.Clip();
        for (var i = 0; i < 8; i++) nested = OscTestPackets.Bundle(nested);
        Assert.True(OscPacketDecoder.TryDecode(nested, out commands));
        Assert.Single(commands);
        Rejected(OscTestPackets.Bundle(nested));
    }

    [Fact]
    public void RandomMalformedTrafficDoesNotThrowOrExceedCommandLimit()
    {
        var random = new Random(9041);
        for (var i = 0; i < 1000; i++)
        {
            var bytes = new byte[random.Next(0, 4200)];
            random.NextBytes(bytes);
            OscPacketDecoder.TryDecode(bytes, out var commands);
            Assert.InRange(commands.Count, 0, 32);
        }
    }

    private static void Rejected(byte[] bytes)
    {
        Assert.False(OscPacketDecoder.TryDecode(bytes, out var commands));
        Assert.Empty(commands);
    }
}

internal static class OscTestPackets
{
    internal static byte[] Clip() => Message("/clypdat/clip");
    internal static byte[] Duration(double value, string tags = ",i") => Message("/clypdat/replay-duration", tags, value);

    internal static byte[] Message(string address, string tags = ",", double value = 0)
    {
        using var stream = new MemoryStream();
        WriteString(stream, address);
        WriteString(stream, tags);
        foreach (var tag in tags.AsSpan(1))
        {
            if (tag is 'i' or 'f') WriteInt(stream, tag == 'i' ? (int)value : BitConverter.SingleToInt32Bits((float)value));
            else if (tag == 's') WriteString(stream, "60");
            else if (tag == 'b') { WriteInt(stream, 1); stream.Write([1, 0, 0, 0]); }
            else if (tag == 'd') stream.Write(new byte[8]);
        }
        return stream.ToArray();
    }

    internal static byte[] Bundle(params byte[][] elements)
    {
        using var stream = new MemoryStream();
        WriteString(stream, "#bundle");
        stream.Write([0, 0, 0, 0, 0, 0, 0, 1]);
        foreach (var element in elements) { WriteInt(stream, element.Length); stream.Write(element); }
        return stream.ToArray();
    }

    private static void WriteString(Stream stream, string value)
    {
        var bytes = Encoding.ASCII.GetBytes(value);
        stream.Write(bytes);
        stream.Write(new byte[4 - bytes.Length % 4]);
    }

    private static void WriteInt(Stream stream, int value)
    {
        Span<byte> bytes = stackalloc byte[4];
        BinaryPrimitives.WriteInt32BigEndian(bytes, value);
        stream.Write(bytes);
    }
}
