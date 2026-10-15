using System.Buffers.Binary;

namespace ClypDat.App.Services;

/// <summary>
/// Waveform peaks the native recorder took from the exact PCM it encoded,
/// written beside each saved clip ("CDPK" v1, see recording_save.cpp). With
/// these, opening a just-saved clip draws its waveform without decoding any
/// audio. Clips without one (imports, exports, older saves) still decode.
/// </summary>
internal static class ClipPeaksSidecar
{
    internal const string Suffix = ".peaks";
    private static ReadOnlySpan<byte> Magic => "CDPK"u8;
    // A minute of five tracks is ~120 KB; anything far past an hour of
    // several tracks is not one of ours.
    private const long MaximumBytes = 64L * 1024 * 1024;

    internal static string PathFor(string libraryRoot, string clipPath) => LibraryLayout.SidecarPath(libraryRoot, clipPath, Suffix);

    /// <summary>Moves the recorder's file from beside the clip into the clip's sidecar folder.</summary>
    internal static void FileWithClip(string libraryRoot, string clipPath)
    {
        var written = clipPath + Suffix;
        try
        {
            if (!File.Exists(written)) return;
            var destination = PathFor(libraryRoot, clipPath);
            if (!LibraryPathGuard.IsWithin(libraryRoot, destination)) { File.Delete(written); return; }
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            File.Move(written, destination, overwrite: true);
        }
        catch (Exception error)
        {
            AppLog.Info($"Waveform peaks could not be filed for {clipPath}: {error.Message}");
            try { File.Delete(written); } catch { }
        }
    }

    internal static void Delete(string libraryRoot, string clipPath)
    {
        var path = PathFor(libraryRoot, clipPath);
        try { if (LibraryPathGuard.IsWithin(libraryRoot, path) && File.Exists(path)) File.Delete(path); }
        catch (Exception error) { AppLog.Info($"Waveform peaks delete failed: {path}: {error.Message}"); }
    }

    /// <summary>
    /// Peaks for every requested stream on a <paramref name="bucketCount"/>
    /// grid, or null when the file is missing, foreign, lacks a stream, or no
    /// longer matches the clip's duration (a trim saved over it).
    /// </summary>
    internal static Dictionary<int, IReadOnlyList<double>>? TryLoad(string libraryRoot, string clipPath, IReadOnlyList<int> streams, TimeSpan duration, int bucketCount)
    {
        try
        {
            var path = PathFor(libraryRoot, clipPath);
            if (!File.Exists(path) || new FileInfo(path).Length > MaximumBytes) return null;
            return Parse(File.ReadAllBytes(path), streams, duration, bucketCount);
        }
        catch (Exception error)
        {
            AppLog.Info($"Waveform peaks unreadable for {clipPath}: {error.Message}");
            return null;
        }
    }

    internal static Dictionary<int, IReadOnlyList<double>>? Parse(ReadOnlySpan<byte> data, IReadOnlyList<int> streams, TimeSpan duration, int bucketCount)
    {
        if (data.Length < 20 || !data[..4].SequenceEqual(Magic)) return null;
        if (BinaryPrimitives.ReadUInt32LittleEndian(data[4..]) != 1) return null;
        var sliceFrames = BinaryPrimitives.ReadUInt32LittleEndian(data[8..]);
        var sampleRate = BinaryPrimitives.ReadUInt32LittleEndian(data[12..]);
        var count = BinaryPrimitives.ReadUInt32LittleEndian(data[16..]);
        if (sliceFrames == 0 || sampleRate == 0 || count is 0 or > 64 || bucketCount <= 0) return null;

        var rows = new Dictionary<int, byte[]>();
        var offset = 20;
        for (var i = 0; i < count; i++)
        {
            if (data.Length < offset + 8) return null;
            var index = (int)BinaryPrimitives.ReadUInt32LittleEndian(data[offset..]);
            var length = (int)BinaryPrimitives.ReadUInt32LittleEndian(data[(offset + 4)..]);
            offset += 8;
            if (length < 0 || data.Length < offset + length) return null;
            rows[index] = data.Slice(offset, length).ToArray();
            offset += length;
        }

        var sliceSeconds = sliceFrames / (double)sampleRate;
        var result = new Dictionary<int, IReadOnlyList<double>>();
        foreach (var stream in streams)
        {
            if (!rows.TryGetValue(stream, out var row) || row.Length == 0) return null;
            // A trim or re-encode saved over the clip changes its length; peaks
            // for the old timeline must not be drawn over the new one.
            if (Math.Abs(row.Length * sliceSeconds - duration.TotalSeconds) > 0.5) return null;
            var peaks = new double[bucketCount];
            for (var bucket = 0; bucket < bucketCount; bucket++)
            {
                var first = (int)((long)bucket * row.Length / bucketCount);
                var last = Math.Max(first + 1, (int)((long)(bucket + 1) * row.Length / bucketCount));
                byte peak = 0;
                for (var slice = first; slice < last && slice < row.Length; slice++) peak = Math.Max(peak, row[slice]);
                peaks[bucket] = peak / 255d;
            }
            result[stream] = peaks;
        }
        return result;
    }
}
