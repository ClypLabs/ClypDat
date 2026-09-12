using System.Diagnostics;
using System.Globalization;
using Avalonia;
using ClypDat.Core.Settings;

namespace ClypDat.App.Services;

/// <summary>
/// Sharp source pixels for the live blur. LibVLC's picture lives in a native
/// child window that nothing can sample, so the overlay gets its own copy:
/// while paused a libvlc snapshot of the exact picture on screen (see
/// <see cref="FromSnapshot"/>); while playing, a decode of just the area the
/// blurs cover, at display resolution, in one-second chunks at a fixed 60fps.
/// The blur itself runs on the GPU in the overlay. Latest request wins; an
/// evicted chunk's decoder is killed, so scrubbing never piles up FFmpeg.
/// </summary>
internal sealed class TimedEffectFrameSource : IDisposable
{
    public const double Fps = 60;
    public const double ChunkSeconds = 1;
    /// <summary>Largest decoded frame. Two chunks of 60 stay under ~150 MB.</summary>
    public const int MaximumFrameBytes = 1_200_000;
    private const double PrefetchSeconds = .5;
    private const double AreaGrid = 16;
    private static long _snapshotIds;
    private readonly object _gate = new();
    private readonly Dictionary<int, Chunk> _chunks = [];
    private string? _key;
    private int _generation;
    private bool _disposed;

    /// <summary>Decoded pixels. <paramref name="Area"/> is the part of the crop
    /// output they cover, normalized 0..1.</summary>
    public readonly record struct Frame(byte[] Pixels, int Width, int Height, long Id, Rect Area);

    /// <summary>What to decode: <paramref name="Source"/> in source pixels, scaled
    /// to <paramref name="Width"/> × <paramref name="Height"/>; <paramref name="Area"/>
    /// is the same rect normalized to the crop output.</summary>
    public readonly record struct DecodeArea(ClipRenderFilters.CropRect Source, int Width, int Height, Rect Area);

    /// <summary>Blur radius in normalized crop-output units, per axis. Export
    /// uses sigma = Strength × height / 1080, and pixels are square.</summary>
    public static (double X, double Y) Sigma(TimedVideoEffect e, ClipRenderFilters.CropRect crop) =>
        (e.Strength / 1080 * crop.Height / Math.Max(1, crop.Width), e.Strength / 1080);

    /// <summary>The effect's box grown by three sigma, where the gaussian has
    /// fallen off to nothing; optionally clamped to the frame.</summary>
    public static Rect Padded(TimedVideoEffect e, ClipRenderFilters.CropRect crop, bool clamp = true)
    {
        var (sx, sy) = Sigma(e, crop);
        var left = e.X - 3 * sx;
        var top = e.Y - 3 * sy;
        var right = e.X + e.Width + 3 * sx;
        var bottom = e.Y + e.Height + 3 * sy;
        if (clamp) { left = Math.Max(0, left); top = Math.Max(0, top); right = Math.Min(1, right); bottom = Math.Min(1, bottom); }
        return new Rect(left, top, right - left, bottom - top);
    }

    /// <summary>
    /// The union of the blurs' padded boxes, snapped outward to a 1/16 grid so
    /// small drags reuse the decode, at display resolution but never above the
    /// source's and never over <see cref="MaximumFrameBytes"/>. Null when there
    /// is nothing to decode.
    /// </summary>
    public static DecodeArea? PlanArea(IEnumerable<TimedVideoEffect> blurs, ClipRenderFilters.CropRect crop, double displayWidth, double displayHeight)
    {
        Rect? union = null;
        foreach (var e in blurs)
        {
            var padded = Padded(e, crop);
            union = union is { } u ? u.Union(padded) : padded;
        }
        if (union is not { } area || crop.Width <= 0 || crop.Height <= 0) return null;
        static double Down(double v) => Math.Clamp(Math.Floor(v * AreaGrid) / AreaGrid, 0, 1);
        static double Up(double v) => Math.Clamp(Math.Ceiling(v * AreaGrid) / AreaGrid, 0, 1);
        int Even(double v) => (int)Math.Round(v / 2) * 2;
        var x0 = Math.Clamp(Even(crop.X + Down(area.Left) * crop.Width), crop.X, crop.X + crop.Width - 2);
        var y0 = Math.Clamp(Even(crop.Y + Down(area.Top) * crop.Height), crop.Y, crop.Y + crop.Height - 2);
        var x1 = Math.Clamp(Even(crop.X + Up(area.Right) * crop.Width), x0 + 2, crop.X + crop.Width / 2 * 2);
        var y1 = Math.Clamp(Even(crop.Y + Up(area.Bottom) * crop.Height), y0 + 2, crop.Y + crop.Height / 2 * 2);
        var source = new ClipRenderFilters.CropRect(x0, y0, Math.Max(2, x1 - x0), Math.Max(2, y1 - y0));
        // Display pixels per source pixel, stepped by eighths so a window resize
        // does not restart the decode, and never upscaled.
        var scale = Math.Min(1, Math.Ceiling(Math.Min(displayWidth / crop.Width, displayHeight / crop.Height) * 8) / 8);
        var bytes = source.Width * scale * source.Height * scale * 4;
        if (bytes > MaximumFrameBytes) scale *= Math.Sqrt(MaximumFrameBytes / bytes);
        var width = Math.Max(2, (int)(source.Width * scale) / 2 * 2);
        var height = Math.Max(2, (int)(source.Height * scale) / 2 * 2);
        var normalized = new Rect((double)(source.X - crop.X) / crop.Width, (double)(source.Y - crop.Y) / crop.Height,
            (double)source.Width / crop.Width, (double)source.Height / crop.Height);
        return new DecodeArea(source, width, height, normalized);
    }

    public static int ChunkIndex(double seconds) => (int)Math.Floor(Math.Max(0, seconds) / ChunkSeconds);

    /// <summary>Frames are resampled to <see cref="Fps"/>, so frame i of a chunk is
    /// exactly chunk start + i/Fps. Floor, not round: a player shows the last
    /// frame at or before its clock, never the next one.</summary>
    public static int FrameIndex(double seconds, int chunk, int count) =>
        count <= 0 ? 0 : Math.Clamp((int)Math.Floor((seconds - chunk * ChunkSeconds) * Fps + 1e-6), 0, count - 1);

    /// <summary>Snapshot width that puts the crop output at display resolution,
    /// never above the source's own.</summary>
    public static uint SnapshotWidth(double displayWidth, int sourceWidth, ClipRenderFilters.CropRect crop) =>
        (uint)Math.Clamp((int)Math.Round(displayWidth * sourceWidth / Math.Max(1, crop.Width)), 2, Math.Max(2, sourceWidth));

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
        return new Frame(pixels, w, h, long.MinValue + Interlocked.Increment(ref _snapshotIds), new Rect(0, 0, 1, 1));
    }

    /// <summary>
    /// Copies <paramref name="area"/> (normalized crop output, may extend past
    /// the frame) out of <paramref name="frame"/> at the frame's resolution.
    /// Pixels beyond what the frame holds repeat its nearest edge, so a blur near
    /// the picture's border darkens nothing. Returns the exact area the pixels
    /// cover after snapping to whole pixels.
    /// </summary>
    public static (byte[] Pixels, int Width, int Height, Rect Area) Extract(Frame frame, Rect area)
    {
        var a = frame.Area;
        var perX = frame.Width / Math.Max(1e-9, a.Width);
        var perY = frame.Height / Math.Max(1e-9, a.Height);
        var left = (int)Math.Floor((area.Left - a.X) * perX);
        var top = (int)Math.Floor((area.Top - a.Y) * perY);
        var width = Math.Clamp((int)Math.Ceiling((area.Right - a.X) * perX) - left, 1, 4096);
        var height = Math.Clamp((int)Math.Ceiling((area.Bottom - a.Y) * perY) - top, 1, 4096);
        var pixels = new byte[width * height * 4];
        // Columns inside the frame copy as one run; the rest repeat an edge.
        var runStart = Math.Clamp(-left, 0, width);
        var runEnd = Math.Clamp(frame.Width - left, runStart, width);
        for (var row = 0; row < height; row++)
        {
            var sourceRow = Math.Clamp(top + row, 0, frame.Height - 1) * frame.Width * 4;
            var targetRow = row * width * 4;
            for (var column = 0; column < runStart; column++)
                Buffer.BlockCopy(frame.Pixels, sourceRow, pixels, targetRow + column * 4, 4);
            if (runEnd > runStart)
                Buffer.BlockCopy(frame.Pixels, sourceRow + (left + runStart) * 4, pixels, targetRow + runStart * 4, (runEnd - runStart) * 4);
            for (var column = Math.Max(runEnd, runStart); column < width; column++)
                Buffer.BlockCopy(frame.Pixels, sourceRow + (frame.Width - 1) * 4, pixels, targetRow + column * 4, 4);
        }
        var covered = new Rect(a.X + left / perX, a.Y + top / perY, width / perX, height / perY);
        return (pixels, width, height, covered);
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
            try { bitmap.CopyPixels(new PixelRect(0, 0, width, height), handle.AddrOfPinnedObject(), pixels.Length, width * 4); }
            finally { handle.Free(); }
            if (bitmap.Format is { } format && format == Avalonia.Platform.PixelFormat.Rgba8888)
                for (var i = 0; i < pixels.Length; i += 4) (pixels[i], pixels[i + 2]) = (pixels[i + 2], pixels[i]);
            return (pixels, width, height);
        }
        catch (Exception error) { AppLog.Error("Blur snapshot could not be read", error); return null; }
        finally { try { File.Delete(path); } catch { } }
    }

    /// <summary>The decoded frame at or just before <paramref name="seconds"/>,
    /// or null while its chunk has not produced that frame yet.</summary>
    public Frame? Request(string path, DecodeArea area, double seconds, double duration)
    {
        var index = ChunkIndex(seconds);
        var next = (index + 1) * ChunkSeconds < duration && seconds >= (index + 1) * ChunkSeconds - PrefetchSeconds ? index + 1 : -1;
        lock (_gate)
        {
            if (_disposed) return null;
            var key = $"{path}|{area.Source}|{area.Width}x{area.Height}";
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
            var chunk = Acquire(path, area, index);
            if (next >= 0) Acquire(path, area, next);
            var available = chunk.Frames.Count;
            if (available == 0) return null;
            var frame = FrameIndex(seconds, index, chunk.Completed ? available : int.MaxValue);
            if (frame >= available) return null;
            return new Frame(chunk.Frames[frame], area.Width, area.Height, ((long)_generation << 32) | ((long)index << 10) | (uint)frame, area.Area);
        }
    }

    private Chunk Acquire(string path, DecodeArea area, int index)
    {
        if (_chunks.TryGetValue(index, out var existing)) return existing;
        var chunk = new Chunk();
        _chunks[index] = chunk;
        _ = Task.Run(() => Decode(chunk, path, area, index));
        return chunk;
    }

    private async Task Decode(Chunk chunk, string path, DecodeArea area, int index)
    {
        try
        {
            var info = new ProcessStartInfo(FfmpegPathResolver.FfmpegPath)
            { UseShellExecute = false, RedirectStandardOutput = true, CreateNoWindow = true, WorkingDirectory = FfmpegPathResolver.WorkingDirectory };
            foreach (var argument in Arguments(path, area, index)) info.ArgumentList.Add(argument);
            using var process = Process.Start(info);
            if (process is null) return;
            try { process.PriorityClass = ProcessPriorityClass.BelowNormal; } catch { }
            lock (_gate)
            {
                if (chunk.Stopped) { TryKill(process); return; }
                chunk.Process = process;
            }
            var stream = process.StandardOutput.BaseStream;
            var size = area.Width * area.Height * 4;
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

    public static IReadOnlyList<string> Arguments(string path, DecodeArea area, int index)
    {
        var source = area.Source;
        return
        [
            "-hide_banner", "-loglevel", "error", "-ss", (index * ChunkSeconds).ToString(CultureInfo.InvariantCulture), "-i", path,
            "-t", ChunkSeconds.ToString(CultureInfo.InvariantCulture), "-map", "0:v:0", "-an", "-sn", "-dn",
            "-vf", $"fps={Fps.ToString(CultureInfo.InvariantCulture)},crop={source.Width}:{source.Height}:{source.X}:{source.Y}:exact=1,scale={area.Width}:{area.Height}:flags=bicubic",
            "-pix_fmt", "bgra", "-f", "rawvideo", "pipe:1"
        ];
    }

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
