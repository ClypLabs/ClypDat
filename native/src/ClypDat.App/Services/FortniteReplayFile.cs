using System.Text;

namespace ClypDat.App.Services;

/// <summary>
/// What a Fortnite .replay says about itself before any of it is decoded:
/// when recording started, how long it ran, and whether Fortnite has
/// finished writing it. Read from the first few hundred bytes.
/// </summary>
internal sealed record FortniteReplayHeader(
    string Path,
    long SizeBytes,
    DateTime LastWriteUtc,
    // The header stamps local time; converted with this PC's time zone rules
    // for that date (so a replay from before a daylight-saving change still
    // lines up).
    DateTime StartUtc,
    double LengthSeconds,
    // Fortnite flips this off when the match ends and it writes the final
    // chunks. A live replay is incomplete and, when encrypted, has no key yet.
    bool IsLive,
    bool IsEncrypted,
    long InfoBytes,
    int EncryptionKeyBytes = 0)
{
    public string FileName => System.IO.Path.GetFileName(Path);
    public DateTime EndUtc => StartUtc.AddSeconds(LengthSeconds);
    // A parsed summary is reused while all three still match.
    private string FileKey => $"{FileName}|{SizeBytes}|{LastWriteUtc.Ticks}";
    public string CacheKey => $"{FileKey}|parser-{FortniteReplayParser.CurrentVersion}";
    // Parser upgrades need a refresh, but the old summary still describes
    // this unchanged file and remains usable while decoding is deferred.
    public bool MatchesFile(string? sourceKey) => string.Equals(sourceKey, FileKey, StringComparison.OrdinalIgnoreCase) ||
        sourceKey?.StartsWith($"{FileKey}|parser-", StringComparison.OrdinalIgnoreCase) == true;
    public bool IsReady => !IsLive && LengthSeconds > 0 && StartUtc != default &&
        (!IsEncrypted || EncryptionKeyBytes is 16 or 24 or 32);
}

// Reads Fortnite replays without ever writing to them. Every open allows
// Fortnite to keep writing (and deleting: it prunes old replays itself), and
// only the parts needed are read: the header, then the small event chunks
// (tens of KB in a 20 MB replay) and, for the playlist, one network chunk.
internal static class FortniteReplayFile
{
    private const uint FileMagic = 0x1CA2E27F;
    private const uint ChunkHeader = 0, ChunkReplayData = 1, ChunkEvent = 3;
    private const int MaximumChunkBytes = 64 * 1024 * 1024;
    private const int MaximumKeptBytes = 16 * 1024 * 1024;

    public static string DefaultFolder => System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FortniteGame", "Saved", "Demos");

    public static FileStream OpenShared(string path) =>
        new(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 64 * 1024, FileOptions.SequentialScan);

    /// <summary>Null when the file is not a replay, is locked, or is too short to have a header yet.</summary>
    public static FortniteReplayHeader? TryReadHeader(string path)
    {
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists) return null;
            using var stream = OpenShared(path);
            using var reader = new BinaryReader(stream, Encoding.ASCII, leaveOpen: true);
            var (lengthMs, timestamp, live, encrypted, keyBytes) = ReadInfo(reader);
            return new FortniteReplayHeader(path, info.Length, info.LastWriteTimeUtc, LocalToUtc(timestamp),
                lengthMs / 1000.0, live, encrypted, stream.Position, keyBytes);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidDataException or EndOfStreamException or ArgumentException)
        {
            return null;
        }
    }

    // The replay's own clock is local wall time with no zone attached.
    internal static DateTime LocalToUtc(DateTime local, TimeZoneInfo? zone = null)
    {
        if (local == default) return default;
        var unspecified = DateTime.SpecifyKind(local, DateTimeKind.Unspecified);
        zone ??= TimeZoneInfo.Local;
        // A skipped local time has no corresponding UTC instant. Reject it
        // rather than silently linking the replay to a clip an hour away.
        if (zone.IsInvalidTime(unspecified)) throw new ArgumentException("Invalid replay local time.");
        return TimeZoneInfo.ConvertTimeToUtc(unspecified, zone);
    }

    /// <summary>
    /// The replay's info block, header chunk and event chunks, plus the first
    /// <paramref name="dataChunks"/> network chunks: everything else is
    /// skipped by seeking, so a 20 MB replay costs well under 1 MB of memory.
    /// </summary>
    public static MemoryStream ReadForParsing(Stream stream, int dataChunks, bool includeEvents)
        => ReadForParsing(stream, dataChunks, includeEvents, out _);

    public static MemoryStream ReadForParsing(Stream stream, int dataChunks, bool includeEvents, out bool truncated)
    {
        truncated = false;
        using var reader = new BinaryReader(stream, Encoding.ASCII, leaveOpen: true);
        stream.Position = 0;
        ReadInfo(reader);
        var infoBytes = stream.Position;
        var output = new MemoryStream();
        stream.Position = 0;
        CopyBytes(stream, output, infoBytes);
        var keptData = 0;
        while (stream.Position + 8 <= stream.Length)
        {
            var type = reader.ReadUInt32();
            var size = reader.ReadInt32();
            if (size <= 0 || size > MaximumChunkBytes || stream.Position + size > stream.Length)
                throw new InvalidDataException("Incomplete or invalid replay chunk.");
            var keep = type == ChunkHeader || includeEvents && type == ChunkEvent || type == ChunkReplayData && keptData++ < dataChunks;
            if (!keep || output.Length + size > MaximumKeptBytes)
            {
                if (keep) truncated = true;
                stream.Seek(size, SeekOrigin.Current);
                continue;
            }
            output.Write(BitConverter.GetBytes(type));
            output.Write(BitConverter.GetBytes(size));
            CopyBytes(stream, output, size);
        }
        if (stream.Position != stream.Length) throw new InvalidDataException("Incomplete replay chunk header.");
        output.Position = 0;
        return output;
    }

    private static void CopyBytes(Stream from, Stream to, long count)
    {
        var buffer = new byte[(int)Math.Min(count, 81920)];
        while (count > 0)
        {
            var read = from.Read(buffer, 0, (int)Math.Min(buffer.Length, count));
            if (read <= 0) throw new EndOfStreamException();
            to.Write(buffer, 0, read);
            count -= read;
        }
    }

    // Unreal's FLocalFileReplayInfo, as LocalFileNetworkReplayStreaming writes it.
    private static (uint LengthMs, DateTime Timestamp, bool IsLive, bool IsEncrypted, int KeyBytes) ReadInfo(BinaryReader reader)
    {
        if (reader.ReadUInt32() != FileMagic) throw new InvalidDataException("Not a replay.");
        var version = reader.ReadUInt32();
        if (version is < 1 or > 7) throw new InvalidDataException("Unsupported replay file version.");
        if (version >= 7)
        {
            var customVersions = reader.ReadInt32();
            if (customVersions is < 0 or > 4096) throw new InvalidDataException("Bad custom versions.");
            reader.BaseStream.Seek(customVersions * 20L, SeekOrigin.Current);
        }
        var lengthMs = reader.ReadUInt32();
        reader.ReadUInt32(); // network version
        reader.ReadUInt32(); // changelist
        SkipFString(reader);
        var live = reader.ReadUInt32() != 0;
        var timestamp = version >= 3 ? DateTime.FromBinary(reader.ReadInt64()) : default;
        if (version >= 2) reader.ReadUInt32(); // compressed
        var encrypted = false;
        var keyBytes = 0;
        if (version >= 6)
        {
            encrypted = reader.ReadUInt32() != 0;
            var declaredKeyBytes = reader.ReadUInt32();
            if (declaredKeyBytes > 1024) throw new InvalidDataException("Bad key size.");
            keyBytes = (int)declaredKeyBytes;
            reader.BaseStream.Seek(keyBytes, SeekOrigin.Current);
        }
        if (reader.BaseStream.Position > reader.BaseStream.Length) throw new EndOfStreamException();
        return (lengthMs, timestamp, live, encrypted, keyBytes);
    }

    private static void SkipFString(BinaryReader reader)
    {
        var length = reader.ReadInt32();
        var bytes = length < 0 ? -2L * length : length;
        if (bytes > 8192) throw new InvalidDataException("Bad string.");
        reader.BaseStream.Seek(bytes, SeekOrigin.Current);
    }
}
