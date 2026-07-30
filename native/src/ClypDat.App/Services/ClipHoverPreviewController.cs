using System.Diagnostics;
using System.Globalization;
using Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using ClypDat.App.ViewModels;

namespace ClypDat.App.Services;

// One low-priority FFmpeg raw-video session for every library card. Sessions
// serialize through _sessionLock so a fast card sweep never leaves decoders
// competing in the background.
internal sealed class ClipHoverPreviewController : IDisposable
{
    internal const int Width = 480;
    internal const int Height = 270;
    internal const int MaximumFramesPerSecond = 60;
    internal const int DefaultFramesPerSecond = 30;
    internal static readonly TimeSpan HoverDelay = TimeSpan.FromMilliseconds(125);
    private const int FrameBytes = Width * Height * 4;

    private readonly object _stateLock = new();
    private readonly SemaphoreSlim _sessionLock = new(1, 1);
    private CancellationTokenSource? _cancellation;
    private Process? _process;
    private ClipCardViewModel? _clip;
    private WriteableBitmap? _bitmap;
    private Action? _requestRepaint;
    private int _generation;
    private bool _disposed;

    public void Request(ClipCardViewModel clip, bool enabled, Action? requestRepaint)
    {
        if (!enabled || requestRepaint is null || !File.Exists(clip.Path)) return;
        Stop("replaced");
        CancellationToken token;
        int generation;
        lock (_stateLock)
        {
            if (_disposed) return;
            _clip = clip;
            _requestRepaint = requestRepaint;
            _cancellation = new CancellationTokenSource();
            token = _cancellation.Token;
            generation = _generation;
        }
        _ = RunAsync(clip, generation, token);
    }

    public void Stop(string reason)
    {
        ClipCardViewModel? clip;
        WriteableBitmap? bitmap;
        Process? process;
        CancellationTokenSource? cancellation;
        var wasActive = false;
        lock (_stateLock)
        {
            _generation++;
            clip = _clip;
            bitmap = _bitmap;
            process = _process;
            cancellation = _cancellation;
            wasActive = clip is not null || process is not null || cancellation is not null;
            _clip = null;
            _bitmap = null;
            _process = null;
            _cancellation = null;
            _requestRepaint = null;
        }
        cancellation?.Cancel();
        Kill(process);
        if (clip is not null && bitmap is not null)
        {
            // UI detach happens before disposal, so bound Image never renders
            // a disposed bitmap and thumbnail returns in same turn.
            clip.HideHoverPreview(bitmap);
            bitmap.Dispose();
        }
        cancellation?.Dispose();
        if (wasActive) AppLog.Info($"Clip hover preview cancelled: {reason}.");
    }

    public void StopIfActive(ClipCardViewModel clip, string reason)
    {
        lock (_stateLock)
        {
            if (_clip != clip) return;
        }
        Stop(reason);
    }

    private async Task RunAsync(ClipCardViewModel clip, int generation, CancellationToken token)
    {
        try
        {
            await Task.Delay(HoverDelay, token);
            await _sessionLock.WaitAsync(token);
            try
            {
                if (!IsCurrent(clip, generation)) return;
                var range = clip.HoverPreviewRange;
                if (range.Duration <= TimeSpan.Zero) return;
                var frameRate = ResolveFrameRate(clip.Media.Fps);
                var bitmap = await Dispatcher.UIThread.InvokeAsync(
                    () => new WriteableBitmap(new PixelSize(Width, Height), new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Unpremul));
                SetBitmap(clip, generation, bitmap);
                if (!IsCurrent(clip, generation)) return;
                AppLog.Info($"Clip hover preview started: {Path.GetFileName(clip.Path)}, fps={frameRate:0.###} (recorded={clip.Media.Fps:0.###}).");

                while (!token.IsCancellationRequested && IsCurrent(clip, generation))
                {
                    using var process = StartDecoder(clip.Path, range, frameRate);
                    SetProcess(clip, generation, process);
                    var stderr = process.StandardError.ReadToEndAsync();
                    var buffer = new byte[FrameBytes];
                    var deliveredFrame = false;
                    while (!token.IsCancellationRequested && await ReadFrameAsync(process.StandardOutput.BaseStream, buffer, token))
                    {
                        var frame = buffer.ToArray();
                        await Dispatcher.UIThread.InvokeAsync(() => CopyFrame(clip, generation, bitmap, frame));
                        deliveredFrame = true;
                    }
                    await process.WaitForExitAsync(CancellationToken.None);
                    ClearProcess(process);
                    var error = await stderr;
                    if (!token.IsCancellationRequested && IsCurrent(clip, generation) && !deliveredFrame)
                    {
                        AppLog.Info($"Clip hover preview decoder failed: {Path.GetFileName(clip.Path)}. {error.Trim()}");
                        return;
                    }
                }
            }
            finally { _sessionLock.Release(); }
        }
        catch (OperationCanceledException) { }
        catch (Exception error) { AppLog.Error("Clip hover preview failed", error); }
        finally { Cleanup(clip, generation); }
    }

    internal static double ResolveFrameRate(double recordedFrameRate) =>
        double.IsFinite(recordedFrameRate) && recordedFrameRate > 0
            ? Math.Clamp(recordedFrameRate, 1, MaximumFramesPerSecond)
            : DefaultFramesPerSecond;

    private static Process StartDecoder(string path, (TimeSpan Start, TimeSpan Duration) range, double frameRate)
    {
        var info = new ProcessStartInfo("ffmpeg")
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        foreach (var argument in BuildDecoderArguments(path, range, frameRate)) info.ArgumentList.Add(argument);
        var process = Process.Start(info) ?? throw new InvalidOperationException("FFmpeg did not start.");
        try { process.PriorityClass = ProcessPriorityClass.BelowNormal; } catch { }
        return process;
    }

    internal static IReadOnlyList<string> BuildDecoderArguments(string path, (TimeSpan Start, TimeSpan Duration) range, double frameRate)
    {
        var start = range.Start.TotalSeconds.ToString("0.###", CultureInfo.InvariantCulture);
        var duration = range.Duration.TotalSeconds.ToString("0.###", CultureInfo.InvariantCulture);
        var fps = ResolveFrameRate(frameRate).ToString("0.###", CultureInfo.InvariantCulture);
        return [
            "-hide_banner", "-loglevel", "error", "-re", "-ss", start, "-i", path, "-t", duration,
            "-an", "-vf", $"fps={fps},scale={Width}:{Height}:force_original_aspect_ratio=decrease,pad={Width}:{Height}:(ow-iw)/2:(oh-ih)/2",
            "-pix_fmt", "bgra", "-f", "rawvideo", "pipe:1"
        ];
    }

    private static async Task<bool> ReadFrameAsync(Stream stream, byte[] buffer, CancellationToken token)
    {
        var offset = 0;
        while (offset < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(offset), token);
            if (read == 0) return false;
            offset += read;
        }
        return true;
    }

    private void SetBitmap(ClipCardViewModel clip, int generation, WriteableBitmap bitmap)
    {
        lock (_stateLock)
        {
            if (IsCurrentLocked(clip, generation)) _bitmap = bitmap;
            else bitmap.Dispose();
        }
    }

    private void SetProcess(ClipCardViewModel clip, int generation, Process process)
    {
        lock (_stateLock)
        {
            if (IsCurrentLocked(clip, generation)) _process = process;
            else Kill(process);
        }
    }

    private void ClearProcess(Process process)
    {
        lock (_stateLock) { if (_process == process) _process = null; }
    }

    private void CopyFrame(ClipCardViewModel clip, int generation, WriteableBitmap bitmap, byte[] frame)
    {
        if (!IsCurrent(clip, generation)) return;
        using var locked = bitmap.Lock();
        unsafe
        {
            fixed (byte* source = frame)
            {
                for (var row = 0; row < Height; row++)
                    Buffer.MemoryCopy(source + row * Width * 4, (byte*)locked.Address + row * locked.RowBytes, locked.RowBytes, Width * 4);
            }
        }
        clip.ShowHoverPreview(bitmap);
        RequestRepaint(clip, generation);
    }

    private void Cleanup(ClipCardViewModel clip, int generation)
    {
        WriteableBitmap? bitmap = null;
        lock (_stateLock)
        {
            if (!IsCurrentLocked(clip, generation)) return;
            bitmap = _bitmap;
            _bitmap = null; _process = null; _clip = null;
            _cancellation?.Dispose(); _cancellation = null; _requestRepaint = null;
        }
        if (bitmap is not null) { clip.HideHoverPreview(bitmap); bitmap.Dispose(); }
        AppLog.Info("Clip hover preview cleanup complete.");
    }

    private bool IsCurrent(ClipCardViewModel clip, int generation) { lock (_stateLock) return IsCurrentLocked(clip, generation); }
    private void RequestRepaint(ClipCardViewModel clip, int generation)
    {
        Action? requestRepaint;
        lock (_stateLock)
        {
            requestRepaint = IsCurrentLocked(clip, generation) ? _requestRepaint : null;
        }
        requestRepaint?.Invoke();
    }
    private bool IsCurrentLocked(ClipCardViewModel clip, int generation) => !_disposed && _generation == generation && _clip == clip;
    private static void Kill(Process? process) { try { if (process is { HasExited: false }) process.Kill(true); } catch { } }
    public void Dispose() { if (_disposed) return; _disposed = true; Stop("window closed"); _sessionLock.Dispose(); }
}
