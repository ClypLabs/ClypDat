using System.Diagnostics;
using System.Globalization;
using Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using ClypDat.App.ViewModels;

namespace ClypDat.App.Services;

// One low-priority FFmpeg raw-video session for library cards. A pending hover
// and active decoder are separate: card sweeps cancel before FFmpeg starts,
// while a brief exit keeps an already-running decoder warm for re-entry.
internal sealed class ClipHoverPreviewController : IDisposable
{
    internal const int Width = 1920;
    internal const int Height = 1080;
    internal const int MaximumFramesPerSecond = 60;
    internal const int DefaultFramesPerSecond = 30;
    internal static readonly TimeSpan HoverDelay = TimeSpan.FromMilliseconds(75);
    internal static readonly TimeSpan WarmExitGrace = TimeSpan.FromMilliseconds(150);
    private const int FrameBytes = Width * Height * 4;

    private readonly object _stateLock = new();
    private readonly SemaphoreSlim _sessionLock = new(1, 1);
    private CancellationTokenSource? _pendingCancellation;
    private ClipCardViewModel? _pendingClip;
    private int _pendingGeneration;
    private CancellationTokenSource? _cancellation;
    private CancellationTokenSource? _warmExitCancellation;
    private Process? _process;
    private ClipCardViewModel? _clip;
    private WriteableBitmap? _bitmap;
    private Action? _requestRepaint;
    private int _generation;
    private bool _disposed;

    public void Request(ClipCardViewModel clip, bool enabled, Action? requestRepaint)
    {
        if (!enabled || requestRepaint is null || !File.Exists(clip.Path)) return;

        WriteableBitmap? warmBitmap = null;
        CancellationTokenSource? warmExitCancellation = null;
        CancellationTokenSource? pendingCancellation = null;
        var warmReused = false;
        CancellationToken token;
        int pendingGeneration;
        lock (_stateLock)
        {
            if (_disposed) return;
            if (_clip == clip)
            {
                warmExitCancellation = _warmExitCancellation;
                warmReused = warmExitCancellation is not null;
                _warmExitCancellation = null;
                _requestRepaint = requestRepaint;
                warmBitmap = _bitmap;
            }
            else
            {
                pendingCancellation = _pendingCancellation;
                _pendingCancellation = new CancellationTokenSource();
                _pendingClip = clip;
                token = _pendingCancellation.Token;
                pendingGeneration = ++_pendingGeneration;
                goto StartPending;
            }
        }

        warmExitCancellation?.Cancel();
        warmExitCancellation?.Dispose();
        if (warmBitmap is not null) clip.ShowHoverPreview(warmBitmap);
        if (warmReused) AppLog.Debug($"Clip hover preview warm reuse: {Path.GetFileName(clip.Path)}.");
        return;

    StartPending:
        pendingCancellation?.Cancel();
        pendingCancellation?.Dispose();
        _ = StartPendingAsync(clip, requestRepaint, pendingGeneration, token);
    }

    public void PointerLeft(ClipCardViewModel clip)
    {
        CancellationTokenSource? pendingCancellation = null;
        WriteableBitmap? bitmap = null;
        CancellationTokenSource? warmExitCancellation = null;
        CancellationToken warmToken = CancellationToken.None;
        int generation = 0;
        var active = false;
        var pendingCancelled = false;
        lock (_stateLock)
        {
            if (_pendingClip == clip)
            {
                pendingCancellation = _pendingCancellation;
                _pendingCancellation = null;
                _pendingClip = null;
                _pendingGeneration++;
                pendingCancelled = true;
            }
            if (_clip == clip)
            {
                bitmap = _bitmap;
                _requestRepaint = null;
                warmExitCancellation = _warmExitCancellation;
                _warmExitCancellation = new CancellationTokenSource();
                warmToken = _warmExitCancellation.Token;
                generation = _generation;
                active = true;
            }
        }

        pendingCancellation?.Cancel();
        pendingCancellation?.Dispose();
        if (pendingCancelled) AppLog.Debug($"Clip hover preview pending cancelled: {Path.GetFileName(clip.Path)}.");
        if (!active) return;

        if (bitmap is not null) clip.HideHoverPreview(bitmap);
        warmExitCancellation?.Cancel();
        warmExitCancellation?.Dispose();
        _ = ExpireWarmSessionAsync(clip, generation, warmToken);
    }

    public void Stop(string reason)
    {
        SessionState state;
        CancellationTokenSource? pendingCancellation;
        lock (_stateLock)
        {
            pendingCancellation = _pendingCancellation;
            _pendingCancellation = null;
            _pendingClip = null;
            _pendingGeneration++;
            state = DetachActiveLocked();
        }
        pendingCancellation?.Cancel();
        pendingCancellation?.Dispose();
        DisposeSession(state, reason, log: state.IsActive);
    }

    public void StopIfActive(ClipCardViewModel clip, string reason)
    {
        lock (_stateLock)
        {
            if (_clip != clip && _pendingClip != clip) return;
        }
        Stop(reason);
    }

    private async Task StartPendingAsync(ClipCardViewModel clip, Action requestRepaint, int pendingGeneration, CancellationToken token)
    {
        try
        {
            await Task.Delay(HoverDelay, token);
            if (!IsPending(clip, pendingGeneration)) return;

            // Settle replaces any warm session before waiting for its serialized
            // decoder loop to finish, so FFmpeg processes never overlap.
            SessionState previous;
            lock (_stateLock)
            {
                if (!IsPendingLocked(clip, pendingGeneration)) return;
                previous = DetachActiveLocked();
            }
            DisposeSession(previous, "replaced", log: previous.IsActive);

            await _sessionLock.WaitAsync(token);
            try
            {
                CancellationTokenSource? cancellation;
                int generation;
                lock (_stateLock)
                {
                    if (!IsPendingLocked(clip, pendingGeneration)) return;
                    _pendingCancellation?.Dispose();
                    _pendingCancellation = null;
                    _pendingClip = null;
                    cancellation = new CancellationTokenSource();
                    _clip = clip;
                    _cancellation = cancellation;
                    _requestRepaint = requestRepaint;
                    generation = ++_generation;
                }
                await RunSessionAsync(clip, generation, cancellation.Token);
            }
            finally { _sessionLock.Release(); }
        }
        catch (OperationCanceledException) { }
        catch (Exception error) { AppLog.Error("Clip hover preview failed", error); }
    }

    private async Task RunSessionAsync(ClipCardViewModel clip, int generation, CancellationToken token)
    {
        try
        {
            var range = clip.HoverPreviewRange;
            if (range.Duration <= TimeSpan.Zero) return;
            var frameRate = ResolveFrameRate(clip.Media.Fps);
            var bitmap = await Dispatcher.UIThread.InvokeAsync(
                () => new WriteableBitmap(new PixelSize(Width, Height), new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Unpremul));
            SetBitmap(clip, generation, bitmap);
            if (!IsCurrent(clip, generation)) return;
            AppLog.Info($"Clip hover preview started: {Path.GetFileName(clip.Path)}, {Width}x{Height}, fps={frameRate:0.###} (recorded={clip.Media.Fps:0.###}).");

            while (!token.IsCancellationRequested && IsCurrent(clip, generation))
            {
                using var process = StartDecoder(clip.Path, range, frameRate);
                SetProcess(clip, generation, process);
                var stderr = process.StandardError.ReadToEndAsync();
                var buffer = new byte[FrameBytes];
                var deliveredFrame = false;
                while (!token.IsCancellationRequested && await ReadFrameAsync(process.StandardOutput.BaseStream, buffer, token))
                {
                    await Dispatcher.UIThread.InvokeAsync(() => CopyFrame(clip, generation, bitmap, buffer));
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
        catch (OperationCanceledException) { }
        catch (Exception error) { AppLog.Error("Clip hover preview failed", error); }
        finally { Cleanup(clip, generation); }
    }

    private async Task ExpireWarmSessionAsync(ClipCardViewModel clip, int generation, CancellationToken token)
    {
        try { await Task.Delay(WarmExitGrace, token); }
        catch (OperationCanceledException) { return; }

        SessionState state;
        lock (_stateLock)
        {
            if (!IsCurrentLocked(clip, generation) || _warmExitCancellation?.Token != token) return;
            state = DetachActiveLocked();
        }
        DisposeSession(state, "warm exit expired", log: state.IsActive);
    }

    internal static double ResolveFrameRate(double recordedFrameRate) =>
        double.IsFinite(recordedFrameRate) && recordedFrameRate > 0
            ? Math.Clamp(recordedFrameRate, 1, MaximumFramesPerSecond)
            : DefaultFramesPerSecond;

    private static Process StartDecoder(string path, (TimeSpan Start, TimeSpan Duration) range, double frameRate)
    {
        var info = new ProcessStartInfo("ffmpeg") { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
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
        return ["-hide_banner", "-loglevel", "error", "-re", "-ss", start, "-i", path, "-t", duration,
            "-an", "-vf", $"fps={fps},scale=w={Width}:h={Height}:flags=lanczos:force_original_aspect_ratio=decrease,pad={Width}:{Height}:(ow-iw)/2:(oh-ih)/2",
            "-pix_fmt", "bgra", "-f", "rawvideo", "pipe:1"];
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
        lock (_stateLock) { if (IsCurrentLocked(clip, generation)) _bitmap = bitmap; else bitmap.Dispose(); }
    }
    private void SetProcess(ClipCardViewModel clip, int generation, Process process)
    {
        lock (_stateLock) { if (IsCurrentLocked(clip, generation)) _process = process; else Kill(process); }
    }
    private void ClearProcess(Process process) { lock (_stateLock) { if (_process == process) _process = null; } }
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
        SessionState state;
        lock (_stateLock)
        {
            if (!IsCurrentLocked(clip, generation)) return;
            state = DetachActiveLocked();
        }
        DisposeSession(state, "completed", log: state.IsActive);
    }
    private SessionState DetachActiveLocked()
    {
        _generation++;
        var state = new SessionState(_clip, _bitmap, _process, _cancellation, _warmExitCancellation);
        _clip = null; _bitmap = null; _process = null; _cancellation = null; _warmExitCancellation = null; _requestRepaint = null;
        return state;
    }
    private static void DisposeSession(SessionState state, string reason, bool log)
    {
        state.Cancellation?.Cancel();
        state.WarmExitCancellation?.Cancel();
        Kill(state.Process);
        if (state.Clip is not null && state.Bitmap is not null) { state.Clip.HideHoverPreview(state.Bitmap); state.Bitmap.Dispose(); }
        state.Cancellation?.Dispose();
        state.WarmExitCancellation?.Dispose();
        if (log) AppLog.Info($"Clip hover preview cleanup complete: {reason}.");
    }
    private bool IsPending(ClipCardViewModel clip, int generation) { lock (_stateLock) return IsPendingLocked(clip, generation); }
    private bool IsPendingLocked(ClipCardViewModel clip, int generation) => !_disposed && _pendingGeneration == generation && _pendingClip == clip;
    private bool IsCurrent(ClipCardViewModel clip, int generation) { lock (_stateLock) return IsCurrentLocked(clip, generation); }
    private bool IsCurrentLocked(ClipCardViewModel clip, int generation) => !_disposed && _generation == generation && _clip == clip;
    private void RequestRepaint(ClipCardViewModel clip, int generation)
    {
        Action? requestRepaint;
        lock (_stateLock) requestRepaint = IsCurrentLocked(clip, generation) ? _requestRepaint : null;
        requestRepaint?.Invoke();
    }
    private static void Kill(Process? process) { try { if (process is { HasExited: false }) process.Kill(true); } catch { } }
    public void Dispose() { if (_disposed) return; _disposed = true; Stop("window closed"); _sessionLock.Dispose(); }

    private readonly record struct SessionState(ClipCardViewModel? Clip, WriteableBitmap? Bitmap, Process? Process, CancellationTokenSource? Cancellation, CancellationTokenSource? WarmExitCancellation)
    { public bool IsActive => Clip is not null || Process is not null || Cancellation is not null; }
}
