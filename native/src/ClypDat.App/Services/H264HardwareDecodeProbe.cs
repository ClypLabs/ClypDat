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
    // Background-only (see QualifyWhenIdle), so this guards a hung ffprobe, not
    // an open. Listing a 186MB clip's packets takes ~220ms; at the old 250ms
    // most probes timed out and every clip stayed on software decode.
    private const int ProbeTimeoutMilliseconds = 5000;
    private const int MaximumPacketPrefixBytes = 64 * 1024;
    // Bounded: the key is path|length|mtime, so a library browsed over a long session
    // - or one whose clips are re-encoded, changing their mtime - grows this
    // indefinitely for the life of the process. Cheap eviction: once the cap is hit,
    // clear and start again. The probe costs one bounded ffprobe run to repopulate.
    private const int MaximumCacheEntries = 4096;
    // Persisted, because qualification only ever helps the NEXT open of a file:
    // in memory alone, every restart put each clip's first open back on
    // software decode. The key carries length and mtime, so a changed file
    // simply misses.
    private static readonly Lazy<ConcurrentDictionary<string, bool>> LoadedCache = new(LoadCache);
    private static ConcurrentDictionary<string, bool> Cache => LoadedCache.Value;
    // Bump the version when the verdict logic changes, so old verdicts are dropped.
    private static string CachePath => Path.Combine(ClypDat.Core.Settings.AppDataPaths.Root, "h264-hardware-decode-v2.json");
    private static readonly ConcurrentDictionary<string, byte> PendingQualifications = new(StringComparer.OrdinalIgnoreCase);
    private static readonly SemaphoreSlim QualificationGate = new(1, 1);

    // Only called under QualificationGate, so saves never race each other.
    private static void CacheResult(string key, bool value)
    {
        if (Cache.Count >= MaximumCacheEntries) Cache.Clear();
        Cache[key] = value;
        try
        {
            var temporary = CachePath + ".tmp";
            File.WriteAllText(temporary, JsonSerializer.Serialize(new Dictionary<string, bool>(Cache)));
            File.Move(temporary, CachePath, overwrite: true);
        }
        catch (Exception error)
        {
            AppLog.Debug($"Editor H.264 hardware-decode cache save failed: {error.Message}");
        }
    }

    private static ConcurrentDictionary<string, bool> LoadCache()
    {
        try
        {
            if (File.Exists(CachePath) &&
                JsonSerializer.Deserialize<Dictionary<string, bool>>(File.ReadAllText(CachePath)) is { Count: <= MaximumCacheEntries } saved)
                return new ConcurrentDictionary<string, bool>(saved, StringComparer.OrdinalIgnoreCase);
        }
        catch (Exception error)
        {
            AppLog.Debug($"Editor H.264 hardware-decode cache load failed: {error.Message}");
        }
        return new ConcurrentDictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
    }

    // Never scan the complete packet index while a user waits for a clip to
    // open. A cache miss deliberately takes the established safe fallback
    // (software decode); qualification resumes once editor foreground work is
    // done and enables hardware decode on a later open of that unchanged file.
    internal static bool TryGetCachedResult(string path, out bool safeForHardwareDecode)
    {
        safeForHardwareDecode = false;
        try
        {
            var key = CacheKey(path);
            return Cache.TryGetValue(key, out safeForHardwareDecode);
        }
        catch
        {
            return false;
        }
    }

    internal static Task QualifyWhenIdle(string path)
    {
        string key;
        try
        {
            key = CacheKey(path);
            if (Cache.ContainsKey(key)) return Task.CompletedTask;
        }
        catch
        {
            return Task.CompletedTask;
        }

        if (!PendingQualifications.TryAdd(key, 0)) return Task.CompletedTask;

        return Task.Run(async () =>
        {
            try
            {
                // Do not trade an open-delay for playback/capture contention.
                await EditorForegroundWork.ParkWhileActiveAsync(CancellationToken.None).ConfigureAwait(false);
                await QualificationGate.WaitAsync().ConfigureAwait(false);
                try
                {
                    // A newer open may have started while this request waited
                    // its turn, so yield once more immediately before ffprobe.
                    await EditorForegroundWork.ParkWhileActiveAsync(CancellationToken.None).ConfigureAwait(false);
                    // An inconclusive probe is not a verdict: leave it uncached
                    // so the next open of this file tries again.
                    if (Probe(path) is { } safe) CacheResult(key, safe);
                }
                finally
                {
                    QualificationGate.Release();
                }
            }
            finally
            {
                PendingQualifications.TryRemove(key, out _);
            }
        });
    }

    private static string CacheKey(string path)
    {
        var info = new FileInfo(path);
        return $"{Path.GetFullPath(path)}|{info.Length}|{info.LastWriteTimeUtc.Ticks}";
    }

    private static bool? Probe(string path)
    {
        try
        {
            var clock = Stopwatch.StartNew();
            var metadata = ReadPacketIndex(path);
            var safe = metadata is not null && HasOnlyIdrRandomAccessPoints(path, metadata.Value.Format, metadata.Value.KeyPackets);
            AppLog.Debug($"Editor H.264 hardware-decode probe: safe={safe}, keyPackets={metadata?.KeyPackets.Count ?? 0}, ms={clock.ElapsedMilliseconds}, file={Path.GetFileName(path)}.");
            return safe;
        }
        catch (TimeoutException)
        {
            AppLog.Debug($"Editor H.264 hardware-decode probe timed out after {ProbeTimeoutMilliseconds}ms; will retry: {Path.GetFileName(path)}.");
            return null;
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
        // No -show_data: it hex-dumps every packet even when only flags, pos and
        // size are printed, which took a 186MB clip from ~220ms to 9s. The
        // packet format comes from the codec tag instead of the extradata dump.
        foreach (var argument in new[]
        {
            "-v", "error",
            "-select_streams", "v:0",
            "-show_entries", "stream=codec_name,codec_tag_string:packet=flags,pos,size",
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
            throw new TimeoutException();
        }
        var output = outputTask.GetAwaiter().GetResult();
        _ = errorTask.GetAwaiter().GetResult();
        if (process.ExitCode != 0 || output.Length > 2 * 1024 * 1024) return null;
        using var document = JsonDocument.Parse(output);
        if (!document.RootElement.TryGetProperty("streams", out var streams) || streams.GetArrayLength() != 1 ||
            !streams[0].TryGetProperty("codec_name", out var codec) || !string.Equals(codec.GetString(), "h264", StringComparison.OrdinalIgnoreCase) ||
            !document.RootElement.TryGetProperty("packets", out var packets)) return null;
        // MP4's avc1/avc3 always carry length-prefixed (AVCC) samples. Other
        // containers are not guessed at: they keep the software path.
        if (PacketFormatFor(streams[0].TryGetProperty("codec_tag_string", out var tag) ? tag.GetString() : null) is not { } format)
            return null;
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

    internal static H264PacketFormat? PacketFormatFor(string? codecTag) =>
        string.Equals(codecTag, "avc1", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(codecTag, "avc3", StringComparison.OrdinalIgnoreCase)
            ? H264PacketFormat.Avcc
            : null;

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
            if (length <= 0 || offset >= bytes.Length) return false;
            // Type before fit: only a 64KB prefix is read, and an IDR slice is
            // usually bigger, so requiring it to fit rejected every real clip.
            if ((bytes[offset] & 0x1f) == 5) return true;
            if (length > bytes.Length - offset) return false;
            offset += length;
        }
        return false;
    }
}

internal enum H264PacketFormat { Auto, Avcc, AnnexB }
internal readonly record struct H264KeyPacket(long Position, long Size);
