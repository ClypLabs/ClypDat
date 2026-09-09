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
    private const int Width = 640, Height = 360, FrameBytes = Width * Height * 4;
    private readonly object _gate = new();
    private readonly Dictionary<string, Segment> _segments = new(StringComparer.OrdinalIgnoreCase);
    private bool _disposed;

    public event Action<Bitmap?>? FrameReady;

    public void Request(string libraryRoot, ClipOverlayLayer? layer, double sourceSeconds)
    {
        var asset = layer?.Assets?.FirstOrDefault(item => sourceSeconds >= item.StartSeconds && sourceSeconds < item.EndSeconds);
        if (asset is null || layer?.Flattened == true) { Publish(null); return; }
        var path = ClipOverlayManifest.ResolveAssetPath(libraryRoot, asset.AssetPath);
        if (path is null || !File.Exists(path)) { Publish(null); return; }
        Segment segment;
        lock (_gate)
        {
            if (_disposed) return;
            if (!_segments.TryGetValue(path, out segment!))
            {
                // Keep only current and next completed segment. Requests never
                // kill useful decode work or launch another FFmpeg process.
                foreach (var stale in _segments.Where(pair => pair.Value.Completed && pair.Key != path).Select(pair => pair.Key).ToArray()) _segments.Remove(stale);
                segment = new Segment(path, Math.Max(.001, asset.EndSeconds - asset.StartSeconds));
                _segments[path] = segment;
                _ = Task.Run(() => Decode(segment));
            }
        }
        var playbackRate = double.IsFinite(asset.PlaybackRate) && asset.PlaybackRate > 0 ? asset.PlaybackRate : 1;
        var requested = asset.SourceOffsetSeconds + (sourceSeconds - asset.StartSeconds) * playbackRate;
        segment.RequestedOffset = Math.Clamp(requested, 0, segment.Duration);
        if (!segment.Completed) return;
        if (segment.Error || segment.Frames.Count == 0) { Publish(null); return; }
        PublishFrame(segment, segment.RequestedOffset);
    }

    private async Task Decode(Segment segment)
    {
        try
        {
            var ffmpeg = Path.Combine(AppContext.BaseDirectory, "ffmpeg", "ffmpeg.exe");
            if (!File.Exists(ffmpeg)) { Complete(segment, true); return; }
            using var process = new Process { StartInfo = new ProcessStartInfo(ffmpeg) { UseShellExecute = false, RedirectStandardOutput = true, CreateNoWindow = true } };
            foreach (var argument in new[] { "-v", "error", "-i", segment.Path, "-map", "0:v:0", "-vsync", "0", "-f", "rawvideo", "-pix_fmt", "bgra", "-" }) process.StartInfo.ArgumentList.Add(argument);
            process.Start();
            var stream = process.StandardOutput.BaseStream;
            while (true)
            {
                var pixels = new byte[FrameBytes]; var offset = 0;
                while (offset < pixels.Length) { var read = await stream.ReadAsync(pixels.AsMemory(offset)).ConfigureAwait(false); if (read == 0) break; offset += read; }
                if (offset == 0) break;
                if (offset != pixels.Length) { Complete(segment, true); return; }
                lock (_gate) { if (_disposed) return; segment.Frames.Add(pixels); }
            }
            await process.WaitForExitAsync().ConfigureAwait(false);
            Complete(segment, process.ExitCode != 0);
        }
        catch { Complete(segment, true); }
    }

    private void Complete(Segment segment, bool error)
    {
        lock (_gate) { segment.Error = error; segment.Completed = true; }
        if (error || segment.Frames.Count == 0) Publish(null);
        else PublishFrame(segment, segment.RequestedOffset);
    }
    private void PublishFrame(Segment segment, double offset)
    {
        var index = Math.Min(segment.Frames.Count - 1, (int)Math.Floor(offset / segment.Duration * segment.Frames.Count));
        Publish(ToBitmap(segment.Frames[index]));
    }
    private Bitmap ToBitmap(byte[] pixels)
    {
        var bitmap = new WriteableBitmap(new PixelSize(Width, Height), new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Unpremul);
        using var target = bitmap.Lock(); System.Runtime.InteropServices.Marshal.Copy(pixels, 0, target.Address, pixels.Length); return bitmap;
    }
    private void Publish(Bitmap? bitmap) => Dispatcher.UIThread.Post(() => { if (_disposed) { bitmap?.Dispose(); return; } FrameReady?.Invoke(bitmap); });
    public void Dispose() { lock (_gate) { _disposed = true; _segments.Clear(); } }
    private sealed class Segment(string path, double duration) { public string Path { get; } = path; public double Duration { get; } = duration; public List<byte[]> Frames { get; } = []; public double RequestedOffset { get; set; } public bool Completed { get; set; } public bool Error { get; set; } }
}
