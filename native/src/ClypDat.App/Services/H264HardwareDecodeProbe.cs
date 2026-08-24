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
    private static readonly ConcurrentDictionary<string, bool> Cache = new(StringComparer.OrdinalIgnoreCase);

    internal static bool HasOnlyIdrRandomAccessPoints(string path)
    {
        try
        {
            var info = new FileInfo(path);
            var key = $"{Path.GetFullPath(path)}|{info.Length}|{info.LastWriteTimeUtc.Ticks}";
            return Cache.GetOrAdd(key, _ => Probe(path));
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
            using var process = Process.Start(new ProcessStartInfo
            {
                FileName = "ffprobe",
                Arguments = $"-v error -select_streams v:0 -show_packets -show_entries packet=flags,data -show_data -of json \"{path.Replace("\"", "\\\"")}\"",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            });
            if (process is null) return false;
            var output = process.StandardOutput.ReadToEnd();
            if (!process.WaitForExit(5000) || process.ExitCode != 0) return false;
            using var document = JsonDocument.Parse(output);
            if (!document.RootElement.TryGetProperty("packets", out var packets)) return false;
            var keyframes = 0;
            foreach (var packet in packets.EnumerateArray())
            {
                if (!packet.TryGetProperty("flags", out var flags) || !flags.GetString()!.Contains('K')) continue;
                keyframes++;
                if (!packet.TryGetProperty("data", out var data) || !ContainsIdr(data.GetString())) return false;
            }
            return keyframes > 0;
        }
        catch (Exception error)
        {
            AppLog.Debug($"Editor H.264 hardware-decode probe failed; using software decode: {error.Message}");
            return false;
        }
    }

    private static bool ContainsIdr(string? dump)
    {
        if (string.IsNullOrEmpty(dump)) return false;
        var hex = new string(dump.Where(Uri.IsHexDigit).ToArray());
        if (hex.Length < 2 || hex.Length % 2 != 0) return false;
        return ContainsIdrPayload(Convert.FromHexString(hex));
    }

    internal static bool ContainsIdrPayload(ReadOnlySpan<byte> bytes)
    {
        for (var i = 0; i + 4 < bytes.Length; i++)
        {
            var start = bytes[i] == 0 && bytes[i + 1] == 0 && (bytes[i + 2] == 1 || (bytes[i + 2] == 0 && bytes[i + 3] == 1));
            if (!start) continue;
            var nal = bytes[i + (bytes[i + 2] == 1 ? 3 : 4)] & 0x1f;
            if (nal == 5) return true;
        }
        for (var offset = 0; offset + 4 <= bytes.Length;)
        {
            var length = (bytes[offset] << 24) | (bytes[offset + 1] << 16) | (bytes[offset + 2] << 8) | bytes[offset + 3];
            offset += 4;
            if (length <= 0 || offset + length > bytes.Length) break;
            if ((bytes[offset] & 0x1f) == 5) return true;
            offset += length;
        }
        return false;
    }
}
