using System.Diagnostics;
using System.Globalization;

namespace ClypDat.App.Services;

/// <summary>
/// Low-resolution copy of the playing clip, cut to the crop output, which
/// the editor blurs live under each blur effect. LibVLC's picture lives in a
/// native child window that nothing can sample, so the overlay decodes its own
/// frames while playing: one-second chunks at a fixed 60fps, the current one
/// plus the next once playback nears its end. Latest request wins. An evicted
/// chunk's decoder is killed, so fast scrubbing never piles up FFmpeg
/// processes. While paused the layer uses a libvlc snapshot instead (see
/// <see cref="FromSnapshot"/>), because only that is the exact picture shown.
/// </summary>
internal sealed class TimedEffectFrameSource : IDisposable
{
    public const double Fps = 60;
    public const double ChunkSeconds = 1;
    public const int DecodeHeight = 270;
    private const double PrefetchSeconds = .5;
    private static long _snapshotIds;
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
    /// exactly chunk start + i/Fps. Floor, not round: a player shows the last
    /// frame at or before its clock, never the next one.</summary>
    public static int FrameIndex(double seconds, int chunk, int count) =>
        count <= 0 ? 0 : Math.Clamp((int)Math.Floor((seconds - chunk * ChunkSeconds) * Fps + 1e-6), 0, count - 1);

    /// <summary>Snapshot width that lands the crop output near <see cref="DecodeHeight"/> lines.</summary>
    public static uint SnapshotWidth(int sourceWidth, int sourceHeight, ClipRenderFilters.CropRect crop) =>
        (uint)Math.Clamp((int)Math.Round(sourceWidth * (double)Math.Min(DecodeHeight, crop.Height) / Math.Max(1, crop.Height)), 2, Math.Min(1920, Math.Max(2, sourceWidth)));

    /// <summary>
    /// Cuts the crop output out of a full-frame snapshot. The snapshot is the
    /// source scaled to width × height, so the crop rect is scaled by the same
    /// factor on each axis.
    /// </summary>
    public static Frame? FromSnapshot(byte[] bgra, int width, int height, ClipRenderFilters.CropRect crop, int sourceWidth, int sourceHeight)
    {
        if (width <= 0 || height <= 0 || sourceWidth <= 0 || sourceHeight <= 0 || bgra.Length < width * height * 4) return null;
        var sx = (double)width / sourceWidth;
        var sy = (double)height / sourceHeight;
        var x = Math.Clamp((int)Math.Round(crop.X * sx), 0, width - 1);
        var y = Math.Clamp((int)Math.Round(crop.Y * sy), 0, height - 1);
        var w = Math.Clamp((int)Math.Round(crop.Width * sx), 1, width - x);
        var h = Math.Clamp((int)Math.Round(crop.Height * sy), 1, height - y);
        var pixels = new byte[w * h * 4];
        for (var row = 0; row < h; row++)
            Buffer.BlockCopy(bgra, ((y + row) * width + x) * 4, pixels, row * w * 4, w * 4);
        return new Frame(pixels, w, h, long.MinValue + Interlocked.Increment(ref _snapshotIds));
    }

    /// <summary>Reads a snapshot PNG as BGRA and deletes it.</summary>
    public static (byte[] Pixels, int Width, int Height)? LoadSnapshot(string path)
    {
        try
        {
            using var bitmap = new Avalonia.Media.Imaging.Bitmap(path);
            var width = bitmap.PixelSize.Width;
            var height = bitmap.PixelSize.Height;
            var pixels = new byte[width * height * 4];
            var handle = System.Runtime.InteropServices.GCHandle.Alloc(pixels, System.Runtime.InteropServices.GCHandleType.Pinned);
            try { bitmap.CopyPixels(new Avalonia.PixelRect(0, 0, width, height), handle.AddrOfPinnedObject(), pixels.Length, width * 4); }
            finally { handle.Free(); }
            if (bitmap.Format is { } format && format == Avalonia.Platform.PixelFormat.Rgba8888)
                for (var i = 0; i < pixels.Length; i += 4) (pixels[i], pixels[i + 2]) = (pixels[i + 2], pixels[i]);
            return (pixels, width, height);
        }
        catch (Exception error) { AppLog.Error("Blur snapshot could not be read", error); return null; }
        finally { try { File.Delete(path); } catch { } }
    }

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
