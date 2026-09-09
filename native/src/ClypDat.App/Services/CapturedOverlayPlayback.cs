using System.Diagnostics;
using Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;

namespace ClypDat.App.Services;

/// <summary>Latest-request camera frame decoder used by the editor overlay host.
/// Segment time is explicit in the manifest: output playback never depends on
/// filesystem timestamps or on the order FFmpeg happened to close a segment.</summary>
internal sealed class CapturedOverlayPlayback : IDisposable
{
    private readonly object _gate = new();
    private int _generation;
    private bool _disposed;
    private string? _lastKey;

    public event Action<Bitmap?>? FrameReady;

    public void Request(string libraryRoot, ClipOverlayLayer? layer, double sourceSeconds)
    {
        var asset = layer?.Assets?.FirstOrDefault(item => sourceSeconds >= item.StartSeconds && sourceSeconds < item.EndSeconds);
        if (asset is null || layer?.Flattened == true)
        {
            Publish(null, Interlocked.Increment(ref _generation));
            return;
        }
        var path = ClipOverlayManifest.ResolveAssetPath(libraryRoot, asset.AssetPath);
        if (path is null || !File.Exists(path)) { Publish(null, Interlocked.Increment(ref _generation)); return; }
        // Camera is encoded at 15fps but spawning ffmpeg for every editor
        // paint takes longer than a 30fps paint interval. Coalesce requests to
        // a stable preview cadence: one decode may complete before its result
        // becomes obsolete, while playback remains visibly live.
        var offset = Math.Floor(Math.Max(0, sourceSeconds - asset.StartSeconds) * 10) / 10;
        var key = $"{path}|{offset:0.0}";
        lock (_gate)
        {
            if (_disposed || key == _lastKey) return;
            _lastKey = key;
        }
        var generation = Interlocked.Increment(ref _generation);
        _ = Task.Run(() => Decode(path, offset, generation));
    }

    private async Task Decode(string path, double offset, int generation)
    {
        try
        {
            var ffmpeg = Path.Combine(AppContext.BaseDirectory, "ffmpeg", "ffmpeg.exe");
            if (!File.Exists(ffmpeg)) { Publish(null, generation); return; }
            using var process = new Process { StartInfo = new ProcessStartInfo(ffmpeg) { UseShellExecute = false, RedirectStandardOutput = true, CreateNoWindow = true } };
            foreach (var argument in new[] { "-v", "error", "-ss", offset.ToString("0.000", System.Globalization.CultureInfo.InvariantCulture), "-i", path, "-frames:v", "1", "-f", "rawvideo", "-pix_fmt", "bgra", "-" })
                process.StartInfo.ArgumentList.Add(argument);
            process.Start();
            var pixels = new byte[640 * 360 * 4];
            var read = 0;
            while (read < pixels.Length)
            {
                var count = await process.StandardOutput.BaseStream.ReadAsync(pixels.AsMemory(read));
                if (count == 0) break;
                read += count;
            }
            await process.WaitForExitAsync();
            if (read != pixels.Length || process.ExitCode != 0) { Publish(null, generation); return; }
            Dispatcher.UIThread.Post(() =>
            {
                if (_disposed || generation != Volatile.Read(ref _generation)) return;
                var bitmap = new WriteableBitmap(new PixelSize(640, 360), new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Unpremul);
                using var target = bitmap.Lock();
                System.Runtime.InteropServices.Marshal.Copy(pixels, 0, target.Address, pixels.Length);
                FrameReady?.Invoke(bitmap);
            });
        }
        catch { Publish(null, generation); }
    }

    private void Publish(Bitmap? bitmap, int generation) => Dispatcher.UIThread.Post(() =>
    {
        if (_disposed || generation != Volatile.Read(ref _generation)) return;
        FrameReady?.Invoke(bitmap);
    });

    public void Dispose() { lock (_gate) { _disposed = true; _lastKey = null; } Interlocked.Increment(ref _generation); }
}
