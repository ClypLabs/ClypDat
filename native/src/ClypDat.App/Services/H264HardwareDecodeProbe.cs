using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;

namespace ClypDat.App.Services;

// Older ClypDat H.264 files occasionally marked a non-IDR packet as a seek
// point. GPU decode then reused a stale surface after a seek. New replay clips
// force IDRs, but inspect the actual packets before enabling hardware decode so
// legacy files retain the known-safe software path.
internal static class H264HardwareDecodeProbe
{
    private const int ProbeTimeoutMilliseconds = 250;
    private const int MaximumPacketPrefixBytes = 64 * 1024;
    // Bounded: the key is path|length|mtime, so a library browsed over a long session
    // - or one whose clips are re-encoded, changing their mtime - grows this
    // indefinitely for the life of the process. Cheap eviction: once the cap is hit,
    // clear and start again. The probe costs one bounded ffprobe run to repopulate.
    private const int MaximumCacheEntries = 4096;
    private static readonly ConcurrentDictionary<string, bool> Cache = new(StringComparer.OrdinalIgnoreCase);

    private static void CacheResult(string key, bool value)
    {
        if (Cache.Count >= MaximumCacheEntries) Cache.Clear();
        Cache[key] = value;
    }

    internal static bool HasOnlyIdrRandomAccessPoints(string path)
    {
        try
        {
            var info = new FileInfo(path);
            var key = $"{Path.GetFullPath(path)}|{info.Length}|{info.LastWriteTimeUtc.Ticks}";
            if (Cache.TryGetValue(key, out var cached)) return cached;
            var probed = Probe(path);
            CacheResult(key, probed);
            return probed;
        }
        catch
        {
            return false;
        }
    }

    private static bool Probe(string path)
    {
        try
        {
            var metadata = ReadPacketIndex(path);
            return metadata is not null && HasOnlyIdrRandomAccessPoints(path, metadata.Value.Format, metadata.Value.KeyPackets);
        }
        catch (Exception error)
        {
            AppLog.Debug($"Editor H.264 hardware-decode probe failed; using software decode: {error.Message}");
            return false;
        }
    }

    // Kept internal so the bounded, filesystem-free part of the probe has a
    // deterministic regression seam. `pos` is ffprobe's file position; only
    // a small prefix is read from every key packet.
    internal static bool HasOnlyIdrRandomAccessPoints(string path, H264PacketFormat format, IReadOnlyList<H264KeyPacket> keyPackets)
    {
        if (keyPackets.Count == 0) return false;
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            foreach (var packet in keyPackets)
            {
                if (packet.Position < 0 || packet.Size <= 0 || packet.Position >= stream.Length) return false;
                stream.Position = packet.Position;
                var bytesToRead = (int)Math.Min(Math.Min(packet.Size, MaximumPacketPrefixBytes), stream.Length - packet.Position);
                if (bytesToRead <= 0) return false;
                var bytes = new byte[bytesToRead];
                var read = 0;
                while (read < bytes.Length)
                {
                    var count = stream.Read(bytes, read, bytes.Length - read);
                    if (count == 0) break;
                    read += count;
                }
                if (read != bytes.Length || !ContainsIdrPayload(bytes, format)) return false;
            }
            return true;
        }
        catch { return false; }
    }

    private static (H264PacketFormat Format, IReadOnlyList<H264KeyPacket> KeyPackets)? ReadPacketIndex(string path)
    {
        var probeStartInfo = new ProcessStartInfo
        {
            FileName = FfmpegPathResolver.FfprobePath,
            UseShellExecute = false, RedirectStandardOutput = true,
            RedirectStandardError = true, CreateNoWindow = true,
            WorkingDirectory = FfmpegPathResolver.WorkingDirectory,
        };
        // ArgumentList rather than a hand-quoted Arguments string: this was the only
        // site in the codebase building one by hand, and its escaping was wrong for a
        // path ending in a backslash, which would escape the closing quote.
        // Deliberately omit packet=data. Only compact index metadata and
        // bounded codec configuration reach managed memory.
        foreach (var argument in new[]
        {
            "-v", "error",
            "-select_streams", "v:0",
            "-show_entries", "stream=codec_name,extradata:packet=flags,pos,size",
            "-show_data",
            "-of", "json",
            path,
        })
        {
            probeStartInfo.ArgumentList.Add(argument);
        }

        using var process = Process.Start(probeStartInfo);
        if (process is null) return null;
        var outputTask = process.StandardOutput.ReadToEndAsync();
        var errorTask = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit(ProbeTimeoutMilliseconds))
        {
            try { process.Kill(entireProcessTree: true); } catch { }
            // A timeout is deliberately a safe software-decode fallback. Do
            // not turn it into an unbounded wait by synchronously draining the
            // killed process's pipes: that kept the editor's first frame
            // stalled for 500ms+ despite the 250ms timeout. Observe faults
            // asynchronously so no unobserved task exception is left behind.
            ObserveFault(outputTask);
            ObserveFault(errorTask);
            return null;
        }
        var output = outputTask.GetAwaiter().GetResult();
        _ = errorTask.GetAwaiter().GetResult();
        if (process.ExitCode != 0 || output.Length > 2 * 1024 * 1024) return null;
        using var document = JsonDocument.Parse(output);
        if (!document.RootElement.TryGetProperty("streams", out var streams) || streams.GetArrayLength() != 1 ||
            !streams[0].TryGetProperty("codec_name", out var codec) || !string.Equals(codec.GetString(), "h264", StringComparison.OrdinalIgnoreCase) ||
            !document.RootElement.TryGetProperty("packets", out var packets)) return null;
        var format = streams[0].TryGetProperty("extradata", out var extra) && ContainsStartCode(extra.GetString())
            ? H264PacketFormat.AnnexB : H264PacketFormat.Avcc;
        var result = new List<H264KeyPacket>();
        foreach (var packet in packets.EnumerateArray())
        {
            if (!packet.TryGetProperty("flags", out var flags) || !(flags.GetString()?.Contains('K') ?? false)) continue;
            if (!TryGetInt64(packet, "pos", out var position) || !TryGetInt64(packet, "size", out var size)) return null;
            result.Add(new H264KeyPacket(position, size));
        }
        return (format, result);
    }

    private static void ObserveFault(Task task) =>
        _ = task.ContinueWith(
            completed => _ = completed.Exception,
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);

    private static bool TryGetInt64(JsonElement packet, string name, out long value)
    {
        value = 0;
        return packet.TryGetProperty(name, out var field) && long.TryParse(field.GetString(), out value);
    }

    private static bool ContainsStartCode(string? dump)
    {
        if (string.IsNullOrEmpty(dump)) return false;
        var hex = new string(dump.Where(Uri.IsHexDigit).ToArray());
        return hex.Contains("000001", StringComparison.Ordinal);
    }

    internal static bool ContainsIdrPayload(ReadOnlySpan<byte> bytes) => ContainsIdrPayload(bytes, H264PacketFormat.Auto);

    internal static bool ContainsIdrPayload(ReadOnlySpan<byte> bytes, H264PacketFormat format)
    {
        if (format is H264PacketFormat.Auto or H264PacketFormat.AnnexB && ContainsAnnexBIdr(bytes)) return true;
        return format is H264PacketFormat.Auto or H264PacketFormat.Avcc && ContainsAvccIdr(bytes);
    }

    private static bool ContainsAnnexBIdr(ReadOnlySpan<byte> bytes)
    {
        for (var i = 0; i + 4 < bytes.Length; i++)
        {
            var start = bytes[i] == 0 && bytes[i + 1] == 0 && (bytes[i + 2] == 1 || (bytes[i + 2] == 0 && bytes[i + 3] == 1));
            if (start && (bytes[i + (bytes[i + 2] == 1 ? 3 : 4)] & 0x1f) == 5) return true;
        }
        return false;
    }

    private static bool ContainsAvccIdr(ReadOnlySpan<byte> bytes)
    {
        for (var offset = 0; offset + 4 <= bytes.Length;)
        {
            var length = (bytes[offset] << 24) | (bytes[offset + 1] << 16) | (bytes[offset + 2] << 8) | bytes[offset + 3];
            offset += 4;
            // Subtract rather than add: offset + length overflows int for a length near
            // 0x7FFFFFFF - which the four length bytes of a crafted MP4 can supply
            // directly - wrapping negative and passing the guard, after which
            // offset += length also goes negative. The span's own bounds check turned
            // that into an IndexOutOfRangeException rather than a read out of bounds,
            // but the arithmetic was wrong and this path parses untrusted media.
            if (length <= 0 || length > bytes.Length - offset) return false;
            if ((bytes[offset] & 0x1f) == 5) return true;
            offset += length;
        }
        return false;
    }
}

internal enum H264PacketFormat { Auto, Avcc, AnnexB }
internal readonly record struct H264KeyPacket(long Position, long Size);
