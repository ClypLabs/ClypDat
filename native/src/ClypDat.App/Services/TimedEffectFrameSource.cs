using System.Diagnostics;
using System.Globalization;

namespace ClypDat.App.Services;

/// <summary>
/// Low-resolution copy of the playing clip, cut to the crop output, which
/// the editor blurs live under each blur effect. LibVLC's picture lives in a
/// native child window that nothing can sample, so the overlay decodes its own
/// frames: two-second chunks at a fixed 30fps, the current one plus the next
/// once playback nears its end. Latest request wins. An evicted chunk's decoder
/// is killed, so fast scrubbing never piles up FFmpeg processes.
/// </summary>
internal sealed class TimedEffectFrameSource : IDisposable
{
    public const double Fps = 30;
    public const double ChunkSeconds = 2;
    public const int DecodeHeight = 270;
    private const double PrefetchSeconds = .75;
    private readonly object _gate = new();
    private readonly Dictionary<int, Chunk> _chunks = [];
    private string? _key;
    private int _generation;
    private bool _disposed;

    public readonly record struct Frame(byte[] Pixels, int Width, int Height, long Id);

    /// <summary>Decode size for a crop output: at most 270 lines, even sides.</summary>
    public static (int Width, int Height) DecodeSize(int width, int height)
    {
        var h = Math.Max(2, Math.Min(DecodeHeight, height) / 2 * 2);
        var w = Math.Max(2, (int)Math.Round(width * (double)h / Math.Max(1, height) / 2) * 2);
        return (w, h);
    }

    public static int ChunkIndex(double seconds) => (int)Math.Floor(Math.Max(0, seconds) / ChunkSeconds);

    /// <summary>Frames are resampled to <see cref="Fps"/>, so frame i of a chunk is
    /// exactly chunk start + i/Fps.</summary>
    public static int FrameIndex(double seconds, int chunk, int count) =>
        count <= 0 ? 0 : Math.Clamp((int)Math.Round((seconds - chunk * ChunkSeconds) * Fps), 0, count - 1);

    /// <summary>The decoded frame nearest <paramref name="seconds"/>, or null
    /// while its chunk has not produced that frame yet.</summary>
    public Frame? Request(string path, ClipRenderFilters.CropRect crop, double seconds, double duration)
    {
        var (width, height) = DecodeSize(crop.Width, crop.Height);
        var index = ChunkIndex(seconds);
        var next = (index + 1) * ChunkSeconds < duration && seconds >= (index + 1) * ChunkSeconds - PrefetchSeconds ? index + 1 : -1;
        Chunk chunk;
        lock (_gate)
        {
            if (_disposed) return null;
            var key = $"{path}|{crop}";
            if (_key != key)
            {
                foreach (var stale in _chunks.Values) stale.Stop();
                _chunks.Clear();
                _key = key;
                _generation++;
            }
            foreach (var stale in _chunks.Where(pair => pair.Key != index && pair.Key != next).ToArray())
            {
                stale.Value.Stop();
                _chunks.Remove(stale.Key);
            }
            chunk = Acquire(path, crop, index, width, height);
            if (next >= 0) Acquire(path, crop, next, width, height);
            var available = chunk.Frames.Count;
            if (available == 0) return null;
            var frame = FrameIndex(seconds, index, chunk.Completed ? available : int.MaxValue);
            if (frame >= available) return null;
            return new Frame(chunk.Frames[frame], width, height, ((long)_generation << 32) | ((long)index << 10) | (uint)frame);
        }
    }

    private Chunk Acquire(string path, ClipRenderFilters.CropRect crop, int index, int width, int height)
    {
        if (_chunks.TryGetValue(index, out var existing)) return existing;
        var chunk = new Chunk();
        _chunks[index] = chunk;
        _ = Task.Run(() => Decode(chunk, path, crop, index, width, height));
        return chunk;
    }

    private async Task Decode(Chunk chunk, string path, ClipRenderFilters.CropRect crop, int index, int width, int height)
    {
        try
        {
            var info = new ProcessStartInfo(FfmpegPathResolver.FfmpegPath)
            { UseShellExecute = false, RedirectStandardOutput = true, CreateNoWindow = true, WorkingDirectory = FfmpegPathResolver.WorkingDirectory };
            foreach (var argument in Arguments(path, crop, index, width, height)) info.ArgumentList.Add(argument);
            using var process = Process.Start(info);
            if (process is null) return;
            try { process.PriorityClass = ProcessPriorityClass.BelowNormal; } catch { }
            lock (_gate)
            {
                if (chunk.Stopped) { TryKill(process); return; }
                chunk.Process = process;
            }
            var stream = process.StandardOutput.BaseStream;
            var size = width * height * 4;
            while (true)
            {
                var pixels = new byte[size];
                var offset = 0;
                while (offset < size)
                {
                    var read = await stream.ReadAsync(pixels.AsMemory(offset)).ConfigureAwait(false);
                    if (read == 0) break;
                    offset += read;
                }
                if (offset < size) break;
                lock (_gate) { if (chunk.Stopped) return; chunk.Frames.Add(pixels); }
            }
            await process.WaitForExitAsync().ConfigureAwait(false);
        }
        catch (Exception error) { AppLog.Error($"Blur preview decode failed: {path}", error); }
        finally { lock (_gate) { chunk.Completed = true; chunk.Process = null; } }
    }

    public static IReadOnlyList<string> Arguments(string path, ClipRenderFilters.CropRect crop, int index, int width, int height) =>
    [
        "-hide_banner", "-loglevel", "error", "-ss", (index * ChunkSeconds).ToString(CultureInfo.InvariantCulture), "-i", path,
        "-t", ChunkSeconds.ToString(CultureInfo.InvariantCulture), "-map", "0:v:0", "-an", "-sn", "-dn",
        "-vf", $"fps={Fps.ToString(CultureInfo.InvariantCulture)},crop={crop.Width}:{crop.Height}:{crop.X}:{crop.Y},scale={width}:{height}:flags=bilinear",
        "-pix_fmt", "bgra", "-f", "rawvideo", "pipe:1"
    ];

    private static void TryKill(Process process) { try { if (!process.HasExited) process.Kill(true); } catch { } }

    /// <summary>Drops every chunk and kills their decoders; the next request starts fresh.</summary>
    public void Reset()
    {
        lock (_gate)
        {
            foreach (var chunk in _chunks.Values) chunk.Stop();
            _chunks.Clear();
            _key = null;
        }
    }

    public void Dispose()
    {
        lock (_gate) _disposed = true;
        Reset();
    }

    private sealed class Chunk
    {
        public List<byte[]> Frames { get; } = [];
        public Process? Process { get; set; }
        public bool Completed { get; set; }
        public bool Stopped { get; private set; }
        public void Stop()
        {
            Stopped = true;
            Frames.Clear();
            if (Process is { } process) TryKill(process);
        }
    }
}
