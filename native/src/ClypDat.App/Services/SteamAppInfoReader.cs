using System.Collections.Frozen;
using System.Security.Cryptography;
using System.Text;

namespace ClypDat.App.Services;

public enum SteamAppKind { Unknown, Game, NonGame }

// Format: https://github.com/ValveResourceFormat/SteamAppInfo/blob/master/README.md
// Parse a private, bounded copy. Never publish a partly read classification table.
internal static class SteamAppInfoReader
{
    internal static FrozenDictionary<int, SteamAppKind> Read(string path)
    {
        using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        if (file.Length > 512 * 1024 * 1024) throw new InvalidDataException("Steam appinfo is too large.");
        var modified = File.GetLastWriteTimeUtc(path);
        var bytes = new byte[checked((int)file.Length)];
        file.ReadExactly(bytes);
        if (file.Length != bytes.Length || File.GetLastWriteTimeUtc(path) != modified)
            throw new IOException("Steam appinfo changed during reading.");
        return Parse(bytes);
    }

    internal static FrozenDictionary<int, SteamAppKind> Parse(byte[] bytes)
    {
        using var stream = new MemoryStream(bytes, false);
        using var reader = new BinaryReader(stream, new UTF8Encoding(false, true));
        var version = reader.ReadUInt32();
        if (version is < 0x07564427 or > 0x07564429) throw new InvalidDataException("Unsupported Steam appinfo format.");
        reader.ReadUInt32(); // universe
        string[]? strings = null;
        long recordsEnd = stream.Length;
        if (version == 0x07564429)
        {
            recordsEnd = reader.ReadInt64();
            if (recordsEnd < stream.Position + 4 || recordsEnd > stream.Length - 4) throw new InvalidDataException("Invalid Steam string table offset.");
            var start = stream.Position;
            stream.Position = recordsEnd;
            var count = reader.ReadUInt32();
            if (count > 2_000_000 || count > stream.Length - stream.Position) throw new InvalidDataException("Invalid Steam string count.");
            strings = new string[count];
            for (var i = 0; i < strings.Length; i++) strings[i] = ReadString(reader);
            stream.Position = start;
        }

        var kinds = new Dictionary<int, SteamAppKind>();
        while (stream.Position <= recordsEnd - 4)
        {
            var appId = reader.ReadInt32();
            if (appId == 0) return kinds.ToFrozenDictionary();
            if (appId < 0 || stream.Position > recordsEnd - 4) throw new InvalidDataException("Invalid Steam app ID.");
            var size = reader.ReadUInt32();
            var headerSize = version >= 0x07564428 ? 60 : 40;
            if (size < headerSize || size > recordsEnd - stream.Position) throw new InvalidDataException("Truncated Steam app record.");
            var end = stream.Position + size;
            stream.Position += 40; // state, timestamp, token, text hash, change number
            var hash = version >= 0x07564428 ? reader.ReadBytes(20) : null;
            var data = reader.ReadBytes(checked((int)(end - stream.Position)));
            if (hash is not null && !SHA1.HashData(data).AsSpan().SequenceEqual(hash))
                throw new InvalidDataException("Steam app record hash mismatch.");
            using var record = new BinaryReader(new MemoryStream(data, false), new UTF8Encoding(false, true));
            string? type = null;
            ReadObject(record, strings, "", 0, ref type);
            if (record.BaseStream.Position != data.Length) throw new InvalidDataException("Invalid Steam record ending.");
            kinds[appId] = type?.ToLowerInvariant() switch
            {
                "game" or "demo" or "beta" => SteamAppKind.Game,
                null or "" => SteamAppKind.Unknown,
                _ => SteamAppKind.NonGame
            };
        }
        throw new InvalidDataException("Missing Steam appinfo footer.");
    }

    private static void ReadObject(BinaryReader reader, string[]? strings, string path, int depth, ref string? appType)
    {
        if (depth > 64) throw new InvalidDataException("Steam VDF nesting limit exceeded.");
        while (true)
        {
            var type = reader.ReadByte();
            if (type == 8) return;
            string key;
            if (strings is null) key = ReadString(reader);
            else
            {
                var index = reader.ReadUInt32();
                if (index >= strings.Length) throw new InvalidDataException("Invalid Steam string index.");
                key = strings[index];
            }
            var childPath = path.Length == 0 ? key : path + "/" + key;
            switch (type)
            {
                case 0: ReadObject(reader, strings, childPath, depth + 1, ref appType); break;
                case 1:
                    var value = ReadString(reader);
                    if (childPath.Equals("appinfo/common/type", StringComparison.OrdinalIgnoreCase)) appType = value;
                    break;
                case 2: case 3: case 4: case 6: reader.ReadUInt32(); break;
                case 7: case 10: reader.ReadUInt64(); break;
                default: throw new InvalidDataException("Unsupported Steam VDF value type.");
            }
        }
    }

    private static string ReadString(BinaryReader reader)
    {
        using var bytes = new MemoryStream();
        byte value;
        while ((value = reader.ReadByte()) != 0)
        {
            if (bytes.Length >= 1024 * 1024) throw new InvalidDataException("Steam VDF string limit exceeded.");
            bytes.WriteByte(value);
        }
        return new UTF8Encoding(false, true).GetString(bytes.GetBuffer(), 0, (int)bytes.Length);
    }
}
