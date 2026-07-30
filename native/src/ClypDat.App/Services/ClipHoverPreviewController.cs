using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Threading.Channels;
using Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using ClypDat.App.ViewModels;

namespace ClypDat.App.Services;

// One FFmpeg session serves one library card at a time. Two bounded raw-frame
// slots decouple decoder reads from UI upload without letting FFmpeg read ahead
// through the source file while a preview is detached.
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
    private TaskCompletionSource? _attachSignal;
    private int _attachmentVersion;
    private int _generation;
    private bool _attached;
    private bool _disposed;

    public void Request(ClipCardViewModel clip, bool enabled, Action? requestRepaint)
    {
        if (!enabled || requestRepaint is null || !File.Exists(clip.Path)) return;

        WriteableBitmap? warmBitmap = null;
        CancellationTokenSource? warmExitCancellation = null;
        CancellationTokenSource? pendingCancellation = null;
        TaskCompletionSource? attachSignal = null;
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
                if (!_attached)
                {
                    _attached = true;
                    _attachmentVersion++;
                    attachSignal = _attachSignal;
                    _attachSignal = null;
                }
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
        attachSignal?.TrySetResult();
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
        CancellationTokenSource? previousWarmExit = null;
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
                if (_attached)
                {
                    _attached = false;
                    _attachmentVersion++;
                    _attachSignal = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                }
                previousWarmExit = _warmExitCancellation;
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
        previousWarmExit?.Cancel();
        previousWarmExit?.Dispose();
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
        DisposeSession(state, reason, state.IsActive);
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

            SessionState previous;
            lock (_stateLock)
            {
                if (!IsPendingLocked(clip, pendingGeneration)) return;
                previous = DetachActiveLocked();
            }
            DisposeSession(previous, "replaced", previous.IsActive);

            await _sessionLock.WaitAsync(token);
            try
            {
                CancellationTokenSource cancellation;
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
                    _attached = true;
                    _attachmentVersion++;
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
        var metrics = new PreviewMetrics();
        try
        {
            var range = clip.HoverPreviewRange;
            if (range.Duration <= TimeSpan.Zero) return;
            var frameRate = ResolveFrameRate(clip.Media.Fps);
            var bitmap = await Dispatcher.UIThread.InvokeAsync(
                () => new WriteableBitmap(new PixelSize(Width, Height), new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Unpremul));
            SetBitmap(clip, generation, bitmap);
            if (!IsCurrent(clip, generation)) return;
            var sourceMbps = clip.Duration > TimeSpan.Zero ? clip.SizeBytes * 8d / clip.Duration.TotalSeconds / 1_000_000d : 0;
            AppLog.Info($"Clip hover preview started: {Path.GetFileName(clip.Path)}, source={clip.Media.Width}x{clip.Media.Height}, sourceMbps={sourceMbps:0.###}, output={Width}x{Height}, fps={frameRate:0.###} (recorded={clip.Media.Fps:0.###}).");
            var slots = new[] { new FrameSlot(new byte[FrameBytes]), new FrameSlot(new byte[FrameBytes]) };

            while (!token.IsCancellationRequested && IsCurrent(clip, generation))
            {
                using var process = StartDecoder(clip.Path, range, frameRate);
                SetProcess(clip, generation, process);
                var stderr = process.StandardError.ReadToEndAsync();
                var sourceReadsBefore = GetReadBytes(process);
                var (decoded, displayed) = await DeliverFramesAsync(process.StandardOutput.BaseStream, slots, clip, generation, bitmap, frameRate, metrics, token);
                await process.WaitForExitAsync(CancellationToken.None);
                metrics.AddReadBytes(GetReadBytes(process) - sourceReadsBefore);
                ClearProcess(process);
                var error = await stderr;
                if (!token.IsCancellationRequested && IsCurrent(clip, generation) && decoded == 0 && displayed == 0)
                {
                    AppLog.Info($"Clip hover preview decoder failed: {Path.GetFileName(clip.Path)}. {error.Trim()}");
                    return;
                }
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception error) { AppLog.Error("Clip hover preview failed", error); }
        finally
        {
            metrics.Log(clip, generation);
            Cleanup(clip, generation);
        }
    }

    private async Task<(int Decoded, int Displayed)> DeliverFramesAsync(Stream stream, IReadOnlyList<FrameSlot> slots, ClipCardViewModel clip, int generation, WriteableBitmap bitmap, double frameRate, PreviewMetrics metrics, CancellationToken token)
    {
        var decodedBefore = metrics.DecodedFrames;
        var displayedBefore = metrics.DisplayedFrames;
        var frames = Channel.CreateBounded<FrameSlot>(new BoundedChannelOptions(1) { FullMode = BoundedChannelFullMode.Wait, SingleWriter = true, SingleReader = true });
        var freeSlots = Channel.CreateBounded<FrameSlot>(new BoundedChannelOptions(2) { FullMode = BoundedChannelFullMode.Wait, SingleWriter = true, SingleReader = true });
        foreach (var slot in slots) await freeSlots.Writer.WriteAsync(slot, token);

        var producer = ProduceFramesAsync(stream, freeSlots.Reader, frames.Writer, metrics, token);
        var consumer = ConsumeFramesAsync(frames.Reader, freeSlots.Writer, clip, generation, bitmap, frameRate, metrics, token);
        await Task.WhenAll(producer, consumer);
        return (metrics.DecodedFrames - decodedBefore, metrics.DisplayedFrames - displayedBefore);
    }

    private static async Task ProduceFramesAsync(Stream stream, ChannelReader<FrameSlot> freeSlots, ChannelWriter<FrameSlot> frames, PreviewMetrics metrics, CancellationToken token)
    {
        try
        {
            while (await freeSlots.WaitToReadAsync(token))
            {
                while (freeSlots.TryRead(out var slot))
                {
                    if (!await ReadFrameAsync(stream, slot.Buffer, token)) return;
                    metrics.MarkDecoded();
                    await frames.WriteAsync(slot, token);
                }
            }
        }
        finally { frames.TryComplete(); }
    }

    private async Task ConsumeFramesAsync(ChannelReader<FrameSlot> frames, ChannelWriter<FrameSlot> freeSlots, ClipCardViewModel clip, int generation, WriteableBitmap bitmap, double frameRate, PreviewMetrics metrics, CancellationToken token)
    {
        var pacer = new HoverPreviewFramePacer(frameRate);
        var attachmentVersion = -1;
        try
        {
            while (await frames.WaitToReadAsync(token))
            {
                while (frames.TryRead(out var slot))
                {
                    var version = await WaitUntilAttachedAsync(clip, generation, token);
                    if (version != attachmentVersion)
                    {
                        attachmentVersion = version;
                        pacer.Reset();
                    }
                    var delay = pacer.NextDelay(metrics.Elapsed);
                    if (delay > TimeSpan.Zero) await Task.Delay(delay, token);
                    var displayed = await Dispatcher.UIThread.InvokeAsync(() => CopyFrame(clip, generation, bitmap, slot.Buffer));
                    if (displayed) metrics.MarkDisplayed();
                    await freeSlots.WriteAsync(slot, token);
                }
            }
        }
        finally { freeSlots.TryComplete(); }
    }

    private async Task<int> WaitUntilAttachedAsync(ClipCardViewModel clip, int generation, CancellationToken token)
    {
        while (true)
        {
            Task signal;
            lock (_stateLock)
            {
                if (!IsCurrentLocked(clip, generation)) throw new OperationCanceledException(token);
                if (_attached) return _attachmentVersion;
                _attachSignal ??= new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                signal = _attachSignal.Task;
            }
            await signal.WaitAsync(token);
        }
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
        DisposeSession(state, "warm exit expired", state.IsActive);
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
        return ["-hide_banner", "-loglevel", "error", "-ss", start, "-i", path, "-t", duration,
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
    private bool CopyFrame(ClipCardViewModel clip, int generation, WriteableBitmap bitmap, byte[] frame)
    {
        Action? requestRepaint;
        lock (_stateLock)
        {
            if (!IsCurrentLocked(clip, generation) || !_attached) return false;
            requestRepaint = _requestRepaint;
        }
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
        requestRepaint?.Invoke();
        return true;
    }
    private void Cleanup(ClipCardViewModel clip, int generation)
    {
        SessionState state;
        lock (_stateLock)
        {
            if (!IsCurrentLocked(clip, generation)) return;
            state = DetachActiveLocked();
        }
        DisposeSession(state, "completed", state.IsActive);
    }
    private SessionState DetachActiveLocked()
    {
        _generation++;
        var state = new SessionState(_clip, _bitmap, _process, _cancellation, _warmExitCancellation);
        _clip = null; _bitmap = null; _process = null; _cancellation = null; _warmExitCancellation = null; _requestRepaint = null; _attachSignal = null; _attached = false;
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
    private static long GetReadBytes(Process process)
    {
        try
        {
            return NativeMethods.GetProcessIoCounters(process.Handle, out var counters)
                ? counters.ReadTransferCount > long.MaxValue ? long.MaxValue : (long)counters.ReadTransferCount
                : 0;
        }
        catch { return 0; }
    }
    private static void Kill(Process? process) { try { if (process is { HasExited: false }) process.Kill(true); } catch { } }
    public void Dispose() { if (_disposed) return; _disposed = true; Stop("window closed"); _sessionLock.Dispose(); }

    private readonly record struct FrameSlot(byte[] Buffer);
    private readonly record struct SessionState(ClipCardViewModel? Clip, WriteableBitmap? Bitmap, Process? Process, CancellationTokenSource? Cancellation, CancellationTokenSource? WarmExitCancellation)
    { public bool IsActive => Clip is not null || Process is not null || Cancellation is not null; }

    [StructLayout(LayoutKind.Sequential)]
    private struct IoCounters
    {
        public ulong ReadOperationCount;
        public ulong WriteOperationCount;
        public ulong OtherOperationCount;
        public ulong ReadTransferCount;
        public ulong WriteTransferCount;
        public ulong OtherTransferCount;
    }
    private static class NativeMethods
    {
        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool GetProcessIoCounters(IntPtr processHandle, out IoCounters ioCounters);
    }
}

internal sealed class HoverPreviewFramePacer
{
    private readonly TimeSpan _interval;
    private TimeSpan? _nextFrameAt;

    public HoverPreviewFramePacer(double frameRate) => _interval = TimeSpan.FromSeconds(1 / ClipHoverPreviewController.ResolveFrameRate(frameRate));
    public void Reset() => _nextFrameAt = null;
    public TimeSpan NextDelay(TimeSpan now)
    {
        if (_nextFrameAt is null || now >= _nextFrameAt)
        {
            _nextFrameAt = now + _interval;
            return TimeSpan.Zero;
        }
        var delay = _nextFrameAt.Value - now;
        _nextFrameAt += _interval;
        return delay;
    }
}

internal sealed class PreviewMetrics
{
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private long _firstDecodedTicks = -1;
    private long _firstDisplayedTicks = -1;
    private long _readBytes;
    private long _previousDisplayTicks = -1;
    private long _longestGapTicks;
    private int _decodedFrames;
    private int _displayedFrames;

    public TimeSpan Elapsed => _clock.Elapsed;
    public int DecodedFrames => Volatile.Read(ref _decodedFrames);
    public int DisplayedFrames => Volatile.Read(ref _displayedFrames);
    public void MarkDecoded()
    {
        Interlocked.Increment(ref _decodedFrames);
        Interlocked.CompareExchange(ref _firstDecodedTicks, Stopwatch.GetTimestamp(), -1);
    }
    public void MarkDisplayed()
    {
        Interlocked.Increment(ref _displayedFrames);
        var now = Stopwatch.GetTimestamp();
        Interlocked.CompareExchange(ref _firstDisplayedTicks, now, -1);
        var previous = Interlocked.Exchange(ref _previousDisplayTicks, now);
        if (previous >= 0) InterlockedExtensions.Max(ref _longestGapTicks, now - previous);
    }
    public void AddReadBytes(long bytes) { if (bytes > 0) Interlocked.Add(ref _readBytes, bytes); }
    public void Log(ClipCardViewModel clip, int generation)
    {
        if (DecodedFrames == 0 && DisplayedFrames == 0) return;
        var elapsed = Math.Max(_clock.Elapsed.TotalSeconds, 0.001);
        var firstDecoded = TicksToMilliseconds(Volatile.Read(ref _firstDecodedTicks));
        var firstDisplayed = TicksToMilliseconds(Volatile.Read(ref _firstDisplayedTicks));
        var longestGap = TicksToMilliseconds(Volatile.Read(ref _longestGapTicks));
        var readMb = Volatile.Read(ref _readBytes) / (1024d * 1024d);
        AppLog.Debug($"Clip hover preview metrics: {Path.GetFileName(clip.Path)}, generation={generation}, firstDecodedMs={firstDecoded:0}, firstDisplayedMs={firstDisplayed:0}, decoded={DecodedFrames}, displayed={DisplayedFrames}, displayedFps={DisplayedFrames / elapsed:0.##}, longestGapMs={longestGap:0}, readMB={readMb:0.##}.");
    }
    private static double TicksToMilliseconds(long ticks) => ticks < 0 ? -1 : ticks * 1000d / Stopwatch.Frequency;
}

internal static class InterlockedExtensions
{
    public static void Max(ref long location, long value)
    {
        while (true)
        {
            var current = Volatile.Read(ref location);
            if (value <= current || Interlocked.CompareExchange(ref location, value, current) == current) return;
        }
    }
}
