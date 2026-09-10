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
    // Decode resamples to a fixed cadence, so frame i is exactly source second
    // i/DecodeFps. Capture runs -vsync 0 passthrough, whose spacing is neither
    // uniform nor known here; inferring a rate from the frame count is what
    // made playback drift. It also halves decode time and memory.
    private const double DecodeFps = 30;
    // Enough lead to decode the next segment before playback arrives in it.
    private const double PrefetchSeconds = .75;
    private readonly object _gate = new();
    private readonly Dictionary<string, Segment> _segments = new(StringComparer.OrdinalIgnoreCase);
    // Which segment the playhead is actually inside. A prefetched segment must
    // never paint: it finishes decoding while the previous one is still on
    // screen, and publishing from there threw a frame up to two seconds into
    // the future onto the display for a tick, every couple of seconds.
    private string? _currentPath;
    // One surface for the life of the object. Allocating a WriteableBitmap per
    // published frame was ~28MB/s at playback rate, and the gen2 collections
    // that bought landed as stalls on the UI thread.
    private WriteableBitmap? _surface;
    private bool _disposed;

    public event Action<Bitmap?>? FrameReady;

    public void Request(string libraryRoot, ClipOverlayLayer? layer, double sourceSeconds)
    {
        var assets = layer?.Assets;
        if (assets is null || layer?.Flattened == true) { Publish(null); return; }
        var asset = assets.FirstOrDefault(item => sourceSeconds >= item.StartSeconds && sourceSeconds < item.EndSeconds);
        if (asset is null) { Publish(null); return; }
        var path = ClipOverlayManifest.ResolveAssetPath(libraryRoot, asset.AssetPath);
        if (path is null || !File.Exists(path)) { Publish(null); return; }

        // Start the next segment before playback reaches it. Decoding only on
        // arrival froze the image on its last frame for a whole decode every
        // two seconds of playback, and again after every seek.
        var following = sourceSeconds >= asset.EndSeconds - PrefetchSeconds
            ? assets.Where(item => item.StartSeconds > asset.StartSeconds).OrderBy(item => item.StartSeconds).FirstOrDefault()
            : null;
        var followingPath = following is null ? null : ClipOverlayManifest.ResolveAssetPath(libraryRoot, following.AssetPath);
        if (followingPath is not null && !File.Exists(followingPath)) followingPath = null;

        lock (_gate) _currentPath = path;
        var segment = Acquire(path, followingPath);
        if (segment is null) return;
        if (followingPath is not null) Acquire(followingPath, path);

        segment.RequestedOffset = SourceSeconds(asset, sourceSeconds);
        if (!segment.Completed) return;
        if (segment.Error || segment.Frames.Count == 0) { Publish(null); return; }
        PublishFrame(segment, segment.RequestedOffset);
    }

    /// <summary>Returns the segment for <paramref name="path"/>, decoding it if new.
    /// Segments other than this one and <paramref name="keep"/> are evicted, so a
    /// request never kills useful decode work or launches a second FFmpeg.</summary>
    private Segment? Acquire(string path, string? keep)
    {
        lock (_gate)
        {
            if (_disposed) return null;
            if (_segments.TryGetValue(path, out var existing)) return existing;
            foreach (var stale in _segments
                         .Where(pair => pair.Value.Completed && pair.Key != path && !string.Equals(pair.Key, keep, StringComparison.OrdinalIgnoreCase))
                         .Select(pair => pair.Key).ToArray())
                _segments.Remove(stale);
            var segment = new Segment(path);
            _segments[path] = segment;
            _ = Task.Run(() => Decode(segment));
            return segment;
        }
    }

    private async Task Decode(Segment segment)
    {
        try
        {
            var ffmpeg = Path.Combine(AppContext.BaseDirectory, "ffmpeg", "ffmpeg.exe");
            if (!File.Exists(ffmpeg)) { Complete(segment, true); return; }
            using var process = new Process { StartInfo = new ProcessStartInfo(ffmpeg) { UseShellExecute = false, RedirectStandardOutput = true, CreateNoWindow = true } };
            foreach (var argument in new[] { "-v", "error", "-i", segment.Path, "-map", "0:v:0", "-vf", $"fps={DecodeFps:0.###}", "-vsync", "0", "-f", "rawvideo", "-pix_fmt", "bgra", "-" }) process.StartInfo.ArgumentList.Add(argument);
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
        bool current;
        lock (_gate)
        {
            segment.Error = error; segment.Completed = true;
            current = string.Equals(segment.Path, _currentPath, StringComparison.OrdinalIgnoreCase);
        }
        // A prefetch finishing is not a reason to repaint anything.
        if (!current) return;
        if (error || segment.Frames.Count == 0) Publish(null);
        else PublishFrame(segment, segment.RequestedOffset);
    }
    /// <summary>
    /// Seconds into the segment file for a clip time. This is not the same
    /// quantity as the segment's clip-visible span, which is shorter whenever
    /// the clip starts or ends mid-segment; clamping source time against that
    /// span pinned a partially included first segment to its opening frames.
    /// </summary>
    internal static double SourceSeconds(ClipOverlayAsset asset, double clipSeconds)
    {
        var rate = double.IsFinite(asset.PlaybackRate) && asset.PlaybackRate > 0 ? asset.PlaybackRate : 1;
        return Math.Max(0, asset.SourceOffsetSeconds + (clipSeconds - asset.StartSeconds) * rate);
    }

    /// <summary>Frames are resampled to <see cref="DecodeFps"/> at decode, so frame
    /// i is exactly source second i/DecodeFps and the index is a pure function of
    /// source time - never of how many frames the file happens to hold.</summary>
    internal static int FrameIndex(double sourceSeconds, int frameCount) =>
        frameCount <= 0 ? 0 : Math.Clamp((int)Math.Round(sourceSeconds * DecodeFps), 0, frameCount - 1);

    private void PublishFrame(Segment segment, double sourceSeconds) =>
        Paint(segment.Frames[FrameIndex(sourceSeconds, segment.Frames.Count)]);

    // Painting on the calling thread when it is already the UI thread saves a
    // whole tick of lag: a frame chosen during tick n used to reach the screen
    // during tick n+1.
    private void Paint(byte[] pixels)
    {
        if (!Dispatcher.UIThread.CheckAccess()) { Dispatcher.UIThread.Post(() => Paint(pixels)); return; }
        if (_disposed) return;
        _surface ??= new WriteableBitmap(new PixelSize(Width, Height), new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Unpremul);
        using (var target = _surface.Lock())
            System.Runtime.InteropServices.Marshal.Copy(pixels, 0, target.Address, pixels.Length);
        FrameReady?.Invoke(_surface);
    }

    private void Publish(Bitmap? bitmap)
    {
        if (!Dispatcher.UIThread.CheckAccess()) { Dispatcher.UIThread.Post(() => Publish(bitmap)); return; }
        if (!_disposed) FrameReady?.Invoke(bitmap);
    }

    public void Dispose()
    {
        lock (_gate) { _disposed = true; _segments.Clear(); _currentPath = null; }
        var surface = _surface;
        _surface = null;
        if (surface is null) return;
        if (Dispatcher.UIThread.CheckAccess()) surface.Dispose();
        else Dispatcher.UIThread.Post(surface.Dispose);
    }
    private sealed class Segment(string path) { public string Path { get; } = path; public List<byte[]> Frames { get; } = []; public double RequestedOffset { get; set; } public bool Completed { get; set; } public bool Error { get; set; } }
}
