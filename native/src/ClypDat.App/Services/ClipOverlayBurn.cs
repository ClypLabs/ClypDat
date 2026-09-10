using System.Diagnostics;
using Avalonia;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using ClypDat.App.Controls;
using ClypDat.Core.Settings;

namespace ClypDat.App.Services;

/// <summary>One prepared overlay input: a temp video, where it goes in the
/// output frame, and when it is visible.</summary>
public sealed record ClipOverlayBurnLayer(string Path, SpotifyOverlayBounds Bounds, string? Enable, bool StraightAlpha) : IDisposable
{
    public void Dispose() { try { File.Delete(Path); } catch (Exception) { } }
}

/// <summary>
/// Everything an encode needs to burn overlays in. Mirrors
/// <see cref="SpotifyOverlayAnimation"/>'s contract - it owns its temp files and
/// deletes them on dispose - so the export and share call sites keep using
/// <c>using</c> exactly as they already do.
/// </summary>
public sealed class ClipOverlayRender : IDisposable
{
    public SpotifyOverlayAnimation? Spotify { get; init; }
    public ClipOverlayBurnLayer? Camera { get; init; }
    public ClipOverlayBurnLayer? Keyboard { get; init; }
    public bool IsEmpty => Spotify is null && Camera is null && Keyboard is null;
    public void Dispose() { Camera?.Dispose(); Keyboard?.Dispose(); Spotify?.Dispose(); }
}

/// <summary>
/// Keeps one prepared set per output size. Share walks up to four encoder tiers
/// looking for one the machine can actually open, and re-rendering every frame
/// of the overlays for each attempt would multiply the slowest part of the job.
/// </summary>
internal sealed class ClipOverlayRenderCache(Func<int, int, CancellationToken, Task<ClipOverlayRender?>> prepare) : IDisposable
{
    private readonly Dictionary<(int Width, int Height), ClipOverlayRender?> _renders = [];

    public async Task<ClipOverlayRender?> GetAsync(int width, int height, CancellationToken token)
    {
        if (_renders.TryGetValue((width, height), out var cached)) return cached;
        var render = await prepare(width, height, token).ConfigureAwait(false);
        _renders[(width, height)] = render;
        return render;
    }

    public void Dispose() { foreach (var render in _renders.Values) render?.Dispose(); _renders.Clear(); }
}

/// <summary>What to burn, in terms the burn can act on without touching the view model.</summary>
internal sealed record ClipOverlayBurnSpec(
    string LibraryRoot,
    ClipOverlayLayer? Camera, VideoOverlayTransform? CameraTransform,
    ClipOverlayLayer? Peripherals, VideoOverlayTransform? PeripheralTransform,
    InputCaptureIndex? Input,
    double TrimStartSeconds, double TrimEndSeconds, double Speed);

internal static class ClipOverlayBurn
{
    public const double FrameRate = 30;
    private const int CameraWidth = 640, CameraHeight = 360;

    public const string CameraMarker = "CLYPDAT_CAMERA_OVERLAY";
    public const string PeripheralMarker = "CLYPDAT_PERIPHERAL_OVERLAY";

    private static string WorkPath(string purpose, string extension) =>
        Path.Combine(AppDataPaths.Root, "overlay-burn", $"{purpose}-{Guid.NewGuid():N}{extension}");

    /// <summary>Deletes anything a hard-killed process left behind. Camera tracks
    /// are far larger than a Spotify card, so leaking them is not survivable.</summary>
    public static void SweepWorkFiles()
    {
        try
        {
            var root = Path.Combine(AppDataPaths.Root, "overlay-burn");
            if (!Directory.Exists(root)) return;
            foreach (var file in Directory.EnumerateFiles(root))
                if (File.GetLastWriteTimeUtc(file) < DateTime.UtcNow - TimeSpan.FromHours(1)) AudioCapturePipeline.TryDelete(file);
        }
        catch (Exception) { }
    }

    /// <summary>
    /// Prepares whichever layers are usable. A layer that cannot be built is
    /// dropped, never fatal: losing an overlay is much better than losing the
    /// export. Cancellation still propagates.
    /// </summary>
    public static async Task<(ClipOverlayBurnLayer? Camera, ClipOverlayBurnLayer? Keyboard)> PrepareAsync(
        ClipOverlayBurnSpec spec, int frameWidth, int frameHeight, CancellationToken token)
    {
        if (!FfmpegPathResolver.IsAvailable) return (null, null);
        var camera = await TryPrepareAsync(() => PrepareCameraAsync(spec, frameWidth, frameHeight, token), "camera", token).ConfigureAwait(false);
        var keyboard = await TryPrepareAsync(() => PrepareKeyboardAsync(spec, frameWidth, frameHeight, token), "keyboard", token).ConfigureAwait(false);
        return (camera, keyboard);
    }

    private static async Task<ClipOverlayBurnLayer?> TryPrepareAsync(Func<Task<ClipOverlayBurnLayer?>> prepare, string name, CancellationToken token)
    {
        try { return await prepare().ConfigureAwait(false); }
        catch (OperationCanceledException) { throw; }
        catch (Exception error)
        {
            AppLog.Error($"Overlay burn: preparing the {name} layer failed; exporting without it.", error);
            return null;
        }
    }

    /// <summary>
    /// Assembles the camera segments into one continuous track on the output
    /// timeline. Done here rather than with one FFmpeg input per segment because
    /// a five-minute clip is 150 segments - 150 inputs, 150 decoders, and a
    /// filter graph to match - and because -itsoffset cannot express a segment's
    /// playback rate. Doing the arithmetic here also makes it testable.
    /// </summary>
    private static async Task<ClipOverlayBurnLayer?> PrepareCameraAsync(ClipOverlayBurnSpec spec, int frameWidth, int frameHeight, CancellationToken token)
    {
        if (spec.Camera is not { Available: true, Flattened: false } layer || spec.CameraTransform is null) return null;
        if (!ClipOverlayManifest.IsUsable(spec.LibraryRoot, layer)) return null;
        var coverage = ClipOverlayBurnLayout.Coverage(layer.Assets, spec.TrimStartSeconds, spec.TrimEndSeconds, spec.Speed);
        if (coverage.Count == 0) return null;

        var duration = Math.Max(0, (spec.TrimEndSeconds - spec.TrimStartSeconds) / Math.Max(.01, spec.Speed));
        var path = WorkPath("camera", ".mp4");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var process = new Process { StartInfo = new(FfmpegPathResolver.FfmpegPath)
        { UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true, RedirectStandardError = true } };
        // Opaque H.264 at the capture's own size: gaps are handled by the enable
        // expression rather than by alpha, so this needs no alpha channel, and
        // scaling to the output frame happens in the export's own filter graph.
        foreach (var argument in new[] { "-v", "error", "-y", "-f", "rawvideo", "-pixel_format", "bgra",
            "-video_size", $"{CameraWidth}x{CameraHeight}", "-framerate", FrameRate.ToString("0"), "-i", "pipe:0",
            "-an", "-c:v", "libx264", "-preset", "veryfast", "-crf", "20", "-pix_fmt", "yuv420p", path })
            process.StartInfo.ArgumentList.Add(argument);

        var reader = new CameraSegmentReader(spec.LibraryRoot);
        try
        {
            token.ThrowIfCancellationRequested();
            process.Start();
            var errors = process.StandardError.ReadToEndAsync(token);
            using var cancellation = token.Register(() => { try { if (!process.HasExited) process.Kill(true); } catch (Exception) { } });
            var blank = new byte[CameraWidth * CameraHeight * 4];
            try
            {
                for (var frame = 0; frame < Math.Ceiling(duration * FrameRate); frame++)
                {
                    token.ThrowIfCancellationRequested();
                    var clipSeconds = spec.TrimStartSeconds + frame / FrameRate * spec.Speed;
                    var pixels = await reader.FrameAtAsync(layer, clipSeconds, token).ConfigureAwait(false) ?? blank;
                    await process.StandardInput.BaseStream.WriteAsync(pixels, token).ConfigureAwait(false);
                }
                process.StandardInput.Close();
                await process.WaitForExitAsync(token).ConfigureAwait(false);
                var error = await errors.ConfigureAwait(false);
                if (process.ExitCode != 0) throw new InvalidDataException(error);
            }
            finally
            {
                try { if (!process.HasExited) process.Kill(true); } catch (Exception) { }
                await process.WaitForExitAsync().ConfigureAwait(false);
            }
            return new(path, ClipOverlayBurnLayout.Resolve(spec.CameraTransform, VideoOverlayLayout.CameraAspectRatio, frameWidth, frameHeight),
                ClipOverlayBurnLayout.Enable(coverage, duration), StraightAlpha: false);
        }
        catch { AudioCapturePipeline.TryDelete(path); throw; }
        finally { reader.Dispose(); }
    }

    /// <summary>
    /// Rasterizes the keyboard board once per distinct pressed set rather than
    /// once per frame - the set only changes on a key edge, a few dozen times in
    /// a clip, while the frames are 30 a second.
    /// </summary>
    private static async Task<ClipOverlayBurnLayer?> PrepareKeyboardAsync(ClipOverlayBurnSpec spec, int frameWidth, int frameHeight, CancellationToken token)
    {
        if (spec.Peripherals is not { Available: true, Flattened: false } layer || spec.PeripheralTransform is null) return null;
        if (spec.Input is null || !ClipOverlayManifest.IsUsable(spec.LibraryRoot, layer)) return null;
        var aspect = KeyboardOverlayCatalog.Get(layer.Source).AspectRatio;
        var bounds = ClipOverlayBurnLayout.Resolve(spec.PeripheralTransform, aspect, frameWidth, frameHeight);
        var duration = Math.Max(0, (spec.TrimEndSeconds - spec.TrimStartSeconds) / Math.Max(.01, spec.Speed));
        if (duration <= 0 || bounds.Width < 2 || bounds.Height < 2) return null;

        var path = WorkPath("keyboard", ".mkv");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        KeyboardOverlayFrames? renderer = null;
        using var process = new Process { StartInfo = new(FfmpegPathResolver.FfmpegPath)
        { UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true, RedirectStandardError = true } };
        try
        {
            renderer = await Dispatcher.UIThread.InvokeAsync(() => new KeyboardOverlayFrames(layer.Source, bounds.Width, bounds.Height));
            foreach (var argument in new[] { "-v", "error", "-y", "-f", "rawvideo", "-pixel_format", "bgra",
                "-video_size", $"{bounds.Width}x{bounds.Height}", "-framerate", FrameRate.ToString("0"), "-i", "pipe:0",
                "-an", "-c:v", "ffv1", "-level", "3", "-pix_fmt", "bgra", path })
                process.StartInfo.ArgumentList.Add(argument);
            token.ThrowIfCancellationRequested();
            process.Start();
            var errors = process.StandardError.ReadToEndAsync(token);
            using var cancellation = token.Register(() => { try { if (!process.HasExited) process.Kill(true); } catch (Exception) { } });
            try
            {
                IReadOnlySet<string>? drawn = null;
                for (var frame = 0; frame < Math.Ceiling(duration * FrameRate); frame++)
                {
                    token.ThrowIfCancellationRequested();
                    var clipSeconds = spec.TrimStartSeconds + frame / FrameRate * spec.Speed;
                    var pressed = ClipInputIndex.PressedAt(spec.Input, clipSeconds);
                    if (drawn is null || !drawn.SetEquals(pressed))
                    {
                        var render = pressed;
                        await Dispatcher.UIThread.InvokeAsync(() => { renderer.Render(render); renderer.CopyStraightPixels(); }, DispatcherPriority.Background);
                        drawn = pressed;
                    }
                    await process.StandardInput.BaseStream.WriteAsync(renderer.Pixels, token).ConfigureAwait(false);
                }
                process.StandardInput.Close();
                await process.WaitForExitAsync(token).ConfigureAwait(false);
                var error = await errors.ConfigureAwait(false);
                if (process.ExitCode != 0) throw new InvalidDataException(error);
            }
            finally
            {
                try { if (!process.HasExited) process.Kill(true); } catch (Exception) { }
                await process.WaitForExitAsync().ConfigureAwait(false);
            }
            return new(path, bounds, null, StraightAlpha: true);
        }
        catch { AudioCapturePipeline.TryDelete(path); throw; }
        finally { if (renderer is not null) await Dispatcher.UIThread.InvokeAsync(renderer.Dispose); }
    }

    /// <summary>
    /// Walks the camera segments in order, decoding each one once and streaming
    /// it, so a long clip never holds more than a segment at a time.
    /// </summary>
    private sealed class CameraSegmentReader(string libraryRoot) : IDisposable
    {
        private string? _path;
        private List<byte[]>? _frames;

        public async Task<byte[]?> FrameAtAsync(ClipOverlayLayer layer, double clipSeconds, CancellationToken token)
        {
            var asset = layer.Assets?.FirstOrDefault(item => clipSeconds >= item.StartSeconds && clipSeconds < item.EndSeconds);
            if (asset is null) return null;
            var path = ClipOverlayManifest.ResolveAssetPath(libraryRoot, asset.AssetPath);
            if (path is null || !File.Exists(path)) return null;
            if (!string.Equals(path, _path, StringComparison.OrdinalIgnoreCase))
            {
                _frames = await DecodeAsync(path, token).ConfigureAwait(false);
                _path = path;
            }
            if (_frames is not { Count: > 0 }) return null;
            var source = CapturedOverlayPlayback.SourceSeconds(asset, clipSeconds);
            return _frames[CapturedOverlayPlayback.FrameIndex(source, _frames.Count)];
        }

        private static async Task<List<byte[]>> DecodeAsync(string path, CancellationToken token)
        {
            var frames = new List<byte[]>();
            using var process = new Process { StartInfo = new(FfmpegPathResolver.FfmpegPath)
            { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true } };
            foreach (var argument in new[] { "-v", "error", "-i", path, "-map", "0:v:0", "-vf", $"fps={FrameRate:0}",
                "-vsync", "0", "-f", "rawvideo", "-pix_fmt", "bgra", "-" })
                process.StartInfo.ArgumentList.Add(argument);
            process.Start();
            var stream = process.StandardOutput.BaseStream;
            var size = CameraWidth * CameraHeight * 4;
            while (true)
            {
                var pixels = new byte[size];
                var offset = 0;
                while (offset < size)
                {
                    var read = await stream.ReadAsync(pixels.AsMemory(offset), token).ConfigureAwait(false);
                    if (read == 0) break;
                    offset += read;
                }
                if (offset != size) break;
                frames.Add(pixels);
            }
            await process.WaitForExitAsync(token).ConfigureAwait(false);
            return frames;
        }

        public void Dispose() { _frames = null; _path = null; }
    }
}

/// <summary>
/// Draws the keyboard board offscreen, one frame at a time into a reused
/// surface. Same shape as SpotifyCardFrames, including the premultiplied to
/// straight conversion - the board draws opaque keycaps on a transparent ground,
/// which would fringe without it.
/// </summary>
internal sealed class KeyboardOverlayFrames : IDisposable
{
    private readonly KeyboardOverlayPreview _control;
    public RenderTargetBitmap Bitmap { get; }
    public byte[] Pixels { get; }
    public int Width { get; }
    public int Height { get; }

    public KeyboardOverlayFrames(string layout, int width, int height)
    {
        Dispatcher.UIThread.VerifyAccess();
        Width = width; Height = height;
        // Render() measures against Bounds, which stays empty on a control that
        // was never laid out - it would draw nothing at all.
        _control = new KeyboardOverlayPreview { Layout = layout };
        _control.Measure(new Size(width, height));
        _control.Arrange(new Rect(0, 0, width, height));
        Bitmap = new RenderTargetBitmap(new PixelSize(width, height), new Vector(96, 96));
        Pixels = new byte[width * height * 4];
    }

    public void Render(IReadOnlySet<string> pressed)
    {
        _control.PressedKeys = pressed;
        using var context = Bitmap.CreateDrawingContext();
        _control.Render(context);
    }

    public unsafe void CopyStraightPixels()
    {
        fixed (byte* pointer = Pixels)
        {
            using var buffer = new LockedFramebuffer((nint)pointer, Bitmap.PixelSize, Width * 4, new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Premul, null);
            Bitmap.CopyPixels(buffer);
        }
        for (var i = 0; i < Pixels.Length; i += 4)
        {
            var alpha = Pixels[i + 3];
            if (alpha == 0) { Pixels[i] = Pixels[i + 1] = Pixels[i + 2] = 0; continue; }
            for (var c = 0; c < 3; c++) Pixels[i + c] = (byte)Math.Min(255, (Pixels[i + c] * 255 + alpha / 2) / alpha);
        }
    }

    public void Dispose() => Bitmap.Dispose();
}
