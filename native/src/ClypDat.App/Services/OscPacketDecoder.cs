using System.Buffers.Binary;
using System.Text;

namespace ClypDat.App.Services;

internal enum OscCommandKind { Clip, ReplayDuration }
internal readonly record struct OscCommand(OscCommandKind Kind, int Seconds = 0);

// Decode the entire datagram before exposing any commands. An invalid bundle
// element must never leave an earlier duration or clip partly applied.
internal static class OscPacketDecoder
{
    internal const int MaximumPacketBytes = 4096;
    internal const int MaximumBundleDepth = 8;
    internal const int MaximumCommands = 32;

    public static bool TryDecode(ReadOnlySpan<byte> packet, out IReadOnlyList<OscCommand> commands)
    {
        commands = Array.Empty<OscCommand>();
        if (packet.Length == 0 || packet.Length > MaximumPacketBytes || packet.Length % 4 != 0) return false;
        var decoded = new List<OscCommand>();
        var messageCount = 0;
        if (!ReadPacket(packet, 0, decoded, ref messageCount)) return false;
        commands = decoded;
        return true;
    }

    private static bool ReadPacket(ReadOnlySpan<byte> packet, int bundleDepth, List<OscCommand> commands, ref int messageCount)
    {
        var offset = 0;
        if (!ReadString(packet, ref offset, out var address)) return false;
        if (address == "#bundle")
        {
            if (bundleDepth >= MaximumBundleDepth || packet.Length - offset < 8 ||
                BinaryPrimitives.ReadUInt64BigEndian(packet.Slice(offset, 8)) != 1) return false;
            offset += 8;
            while (offset < packet.Length)
            {
                if (packet.Length - offset < 4) return false;
                var size = BinaryPrimitives.ReadInt32BigEndian(packet.Slice(offset, 4));
                offset += 4;
                if (size <= 0 || size % 4 != 0 || size > packet.Length - offset ||
                    !ReadPacket(packet.Slice(offset, size), bundleDepth + 1, commands, ref messageCount)) return false;
                offset += size;
            }
            return true;
        }

        if (!address.StartsWith('/') || ++messageCount > MaximumCommands ||
            !ReadString(packet, ref offset, out var tags) || !tags.StartsWith(',')) return false;

        double number = 0;
        foreach (var tag in tags.AsSpan(1))
        {
            switch (tag)
            {
                case 'i':
                case 'f':
                    if (packet.Length - offset < 4) return false;
                    var bits = BinaryPrimitives.ReadInt32BigEndian(packet.Slice(offset, 4));
                    number = tag == 'i' ? bits : BitConverter.Int32BitsToSingle(bits);
                    offset += 4;
                    break;
                case 'T': case 'F': break;
                case 's':
                    if (!ReadString(packet, ref offset, out _)) return false;
                    break;
                case 'b':
                    if (packet.Length - offset < 4) return false;
                    var size = BinaryPrimitives.ReadInt32BigEndian(packet.Slice(offset, 4));
                    offset += 4;
                    if (size < 0 || size > packet.Length - offset) return false;
                    var paddedSize = (size + 3) & ~3;
                    if (paddedSize > packet.Length - offset) return false;
                    for (var i = size; i < paddedSize; i++) if (packet[offset + i] != 0) return false;
                    offset += paddedSize;
                    break;
                default: return false;
            }
        }
        if (offset != packet.Length) return false;

        if (address == "/clypdat/clip")
        {
            if (tags is "," or ",T") commands.Add(new(OscCommandKind.Clip));
            else if (tags == ",F") { }
            else if (tags is ",i" or ",f" && double.IsFinite(number))
            {
                if (number > 0) commands.Add(new(OscCommandKind.Clip));
            }
            else return false;
        }
        else if (address == "/clypdat/replay-duration")
        {
            if (tags is not (",i" or ",f") || number is not (30 or 60 or 120 or 180 or 240 or 300)) return false;
            commands.Add(new(OscCommandKind.ReplayDuration, (int)number));
        }
        return true;
    }

    private static bool ReadString(ReadOnlySpan<byte> packet, ref int offset, out string value)
    {
        value = string.Empty;
        var start = offset;
        if (start >= packet.Length) return false;
        var length = packet[start..].IndexOf((byte)0);
        if (length < 0) return false;
        var paddedLength = (length + 4) & ~3;
        if (paddedLength > packet.Length - start) return false;
        for (var i = 0; i < length; i++) if (packet[start + i] > 127) return false;
        for (var i = length; i < paddedLength; i++) if (packet[start + i] != 0) return false;
        value = Encoding.ASCII.GetString(packet.Slice(start, length));
        offset += paddedLength;
        return true;
    }
}
