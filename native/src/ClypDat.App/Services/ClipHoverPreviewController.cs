using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Threading.Channels;
using Avalonia;
using Avalonia.Platform;
using ClypDat.App.Controls;
using ClypDat.App.ViewModels;

namespace ClypDat.App.Services;

// One FFmpeg session serves one library card at a time. Raw frames are paced
// before they leave FFmpeg, while the UI keeps only the newest not-yet-painted
// frame. This keeps previews current without letting slow UI paints pile up
// behind the decoder.
//
// Frames are decoded at the size of the card that displays them, NOT at the
// clip's own resolution. This used to be a hardcoded 1920x1080: every frame was
// 8.29MB of BGRA pushed through an anonymous pipe (~500MB/s at 60fps), scaled
// with lanczos, and blitted into a 1920x1080 WriteableBitmap - an LOH
// allocation churned once per hovered card. The card it lands on is CardWidth
// wide (MainWindowViewModel clamps that to a 220 minimum, typically 220-500),
// so that was 12-70x more pixels than were ever displayed, and it showed up on
// low-end machines as a hover stealing a whole core from the capture pipeline.
internal sealed class ClipHoverPreviewController : IDisposable
{
    // GPU composition uploads only card-sized RGBA textures. Every preview is
    // paced at a fixed 60fps so the card never silently changes cadence while
    // it is being watched.
    internal const int MaximumFramesPerSecond = 60;
    // Used when the card hasn't been laid out yet (no bounds to measure).
    internal const int DefaultPreviewWidth = 480;
    internal const int DefaultPreviewHeight = 270;
    // Long-edge cap. Past this the extra pixels cost pipe bandwidth and UI
    // upload time without being resolvable on a library card.
    internal const int MaximumPreviewWidth = 640;
    private const int MinimumPreviewWidth = 160;
    private const int MinimumPreviewHeight = 90;
    internal static readonly TimeSpan HoverDelay = TimeSpan.Zero;
    internal static readonly TimeSpan WarmExitGrace = TimeSpan.FromMilliseconds(150);

    private readonly object _stateLock = new();
    private readonly SemaphoreSlim _sessionLock = new(1, 1);
    private CancellationTokenSource? _pendingCancellation;
    private ClipCardViewModel? _pendingClip;
    private int _pendingGeneration;
    private CancellationTokenSource? _cancellation;
    private CancellationTokenSource? _warmExitCancellation;
    private Process? _process;
    private ClipCardViewModel? _clip;
    private IClipPreviewPresenter? _presenter;
    // The size the active presenter and decoder were built for. A warm
    // session can only be reused when the card still wants the same size -
    // after a window resize it doesn't, and the old surface would be scaled
    // instead of matching the card.
    private PixelSize _previewSize;
    private TaskCompletionSource? _attachSignal;
    private int _attachmentVersion;
    private int _generation;
    private bool _attached;
    private bool _disposed;

    public void Request(ClipCardViewModel clip, bool enabled, IClipPreviewPresenter? presenter, PixelSize previewSize)
    {
        if (!enabled || presenter is null || !File.Exists(clip.Path)) return;

        IClipPreviewPresenter? warmPresenter = null;
        CancellationTokenSource? warmExitCancellation = null;
        CancellationTokenSource? pendingCancellation = null;
        TaskCompletionSource? attachSignal = null;
        var warmReused = false;
        CancellationToken token;
        int pendingGeneration;
        long requestTimestamp;
        lock (_stateLock)
        {
            if (_disposed) return;
            if (_clip == clip && _previewSize == previewSize)
            {
                warmExitCancellation = _warmExitCancellation;
                warmReused = warmExitCancellation is not null;
                _warmExitCancellation = null;
                warmPresenter = _presenter;
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
                requestTimestamp = Stopwatch.GetTimestamp();
                goto StartPending;
            }
        }

        warmExitCancellation?.Cancel();
        warmExitCancellation?.Dispose();
        if (warmPresenter is not null) _ = warmPresenter.SetAttachedAsync(true).AsTask();
        attachSignal?.TrySetResult();
        if (warmReused) AppLog.Debug($"Clip hover preview warm reuse: {Path.GetFileName(clip.Path)}.");
        return;

    StartPending:
        pendingCancellation?.Cancel();
        pendingCancellation?.Dispose();
        _ = StartPendingAsync(clip, presenter, previewSize, pendingGeneration, token, requestTimestamp);
    }

    // Pixel size from card's laid-out DIP width and render scaling. Quantizing
    // width to 32px gives exact 16:9 with even dimensions: width = 32n,
    // height = 18n. A fractional DIP height must not change image aspect.
    internal static PixelSize ResolvePreviewSize(Size cardSize, double renderScaling)
    {
        var scale = double.IsFinite(renderScaling) && renderScaling > 0 ? renderScaling : 1.0;
        var width = cardSize.Width * scale;
        var height = cardSize.Height * scale;
        if (!double.IsFinite(width) || !double.IsFinite(height) || width < 1 || height < 1)
        {
            width = DefaultPreviewWidth;
            height = DefaultPreviewHeight;
        }

        width = Math.Clamp(width, MinimumPreviewWidth, MaximumPreviewWidth);
        var quantizedWidth = QuantizePreviewWidth(width);
        return new PixelSize(quantizedWidth, quantizedWidth * 9 / 16);
    }

    private static int QuantizePreviewWidth(double width)
    {
        const int aspectWidthStep = 32;
        var rounded = (int)Math.Round(width / aspectWidthStep, MidpointRounding.AwayFromZero) * aspectWidthStep;
        return Math.Clamp(rounded, MinimumPreviewWidth, MaximumPreviewWidth);
    }

    public void PointerLeft(ClipCardViewModel clip)
    {
        CancellationTokenSource? pendingCancellation = null;
        IClipPreviewPresenter? presenter = null;
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
                presenter = _presenter;
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

        if (presenter is not null) _ = presenter.SetAttachedAsync(false).AsTask();
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
        _ = DisposeDetachedSessionAsync(state, reason, state.IsActive);
    }

    public void StopIfActive(ClipCardViewModel clip, string reason)
    {
        lock (_stateLock)
        {
            if (_clip != clip && _pendingClip != clip) return;
        }
        Stop(reason);
    }

    private async Task StartPendingAsync(ClipCardViewModel clip, IClipPreviewPresenter presenter, PixelSize previewSize, int pendingGeneration, CancellationToken token, long requestTimestamp)
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
            await DisposeSessionAsync(previous, "replaced", previous.IsActive);

            await _sessionLock.WaitAsync(token);
            var runStarted = false;
            var generation = 0;
            try
            {
                CancellationTokenSource cancellation;
                lock (_stateLock)
                {
                    if (!IsPendingLocked(clip, pendingGeneration)) return;
                    _pendingCancellation?.Dispose();
                    _pendingCancellation = null;
                    _pendingClip = null;
                    cancellation = new CancellationTokenSource();
                    _clip = clip;
                    _cancellation = cancellation;
                    _previewSize = previewSize;
                    _presenter = presenter;
                    _attached = true;
                    _attachmentVersion++;
                    generation = ++_generation;
                }
                await presenter.ActivateSessionAsync(cancellation.Token);
                bool attached;
                lock (_stateLock)
                {
                    if (!IsCurrentLocked(clip, generation)) return;
                    attached = _attached;
                }
                await presenter.SetAttachedAsync(attached);
                await presenter.SetProgressAsync(0);
                runStarted = true;
                await RunSessionAsync(clip, generation, previewSize, presenter, cancellation.Token, requestTimestamp);
            }
            finally
            {
                if (!runStarted) await AbandonSessionAsync(clip, generation);
                _sessionLock.Release();
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception error) { AppLog.Error("Clip hover preview failed", error); }
    }

    private async Task RunSessionAsync(ClipCardViewModel clip, int generation, PixelSize previewSize, IClipPreviewPresenter presenter, CancellationToken token, long requestTimestamp)
    {
        var metrics = new PreviewMetrics(requestTimestamp);
        try
        {
            var range = clip.HoverPreviewRange;
            if (range.Duration <= TimeSpan.Zero) return;
            var frameRate = MaximumFramesPerSecond;
            var pacer = new HoverPreviewFramePacer(frameRate);
            var expectedFrameCount = Math.Max(1, (int)Math.Ceiling(range.Duration.TotalSeconds * pacer.CurrentFrameRate));
            var frameBytes = previewSize.Width * previewSize.Height * 4;
            if (!IsCurrent(clip, generation)) return;
            var sourceMbps = clip.Duration > TimeSpan.Zero ? clip.SizeBytes * 8d / clip.Duration.TotalSeconds / 1_000_000d : 0;
            AppLog.Info($"Clip hover preview started: {Path.GetFileName(clip.Path)}, source={clip.Media.Width}x{clip.Media.Height}, sourceMbps={sourceMbps:0.###}, output={previewSize.Width}x{previewSize.Height}, targetFps={frameRate:0.###} (recorded={clip.Media.Fps:0.###}).");
            var slots = new[] { new FrameSlot(new byte[frameBytes]), new FrameSlot(new byte[frameBytes]), new FrameSlot(new byte[frameBytes]) };

            while (!token.IsCancellationRequested && IsCurrent(clip, generation))
            {
                using var process = StartDecoder(clip.Path, range, pacer.CurrentFrameRate, previewSize, clip.HoverPreviewCropFilter);
                SetProcess(clip, generation, process);
                var stderr = process.StandardError.ReadToEndAsync();
                var sourceReadsBefore = GetReadBytes(process);
                var (decoded, displayed) = await DeliverFramesAsync(process.StandardOutput.BaseStream, slots, clip, generation, presenter, previewSize, pacer, expectedFrameCount, metrics, token);
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
            await CleanupAsync(clip, generation);
        }
    }

    private async Task<(int Decoded, int Displayed)> DeliverFramesAsync(Stream stream, IReadOnlyList<FrameSlot> slots, ClipCardViewModel clip, int generation, IClipPreviewPresenter presenter, PixelSize previewSize, HoverPreviewFramePacer pacer, int expectedFrameCount, PreviewMetrics metrics, CancellationToken token)
    {
        var decodedBefore = metrics.DecodedFrames;
        var displayedBefore = metrics.DisplayedFrames;
        var frames = new LatestFrameMailbox<FrameSlot>();
        var freeSlots = Channel.CreateBounded<FrameSlot>(new BoundedChannelOptions(3) { FullMode = BoundedChannelFullMode.Wait, SingleWriter = true, SingleReader = true });
        foreach (var slot in slots) await freeSlots.Writer.WriteAsync(slot, token);

        var producer = ProduceFramesAsync(stream, freeSlots, frames, clip, generation, pacer, metrics, token);
        var consumer = ConsumeFramesAsync(frames, freeSlots.Writer, clip, generation, presenter, previewSize, expectedFrameCount, metrics, token);
        await Task.WhenAll(producer, consumer);
        return (metrics.DecodedFrames - decodedBefore, metrics.DisplayedFrames - displayedBefore);
    }

    private async Task ProduceFramesAsync(Stream stream, Channel<FrameSlot> freeSlots, LatestFrameMailbox<FrameSlot> frames, ClipCardViewModel clip, int generation, HoverPreviewFramePacer pacer, PreviewMetrics metrics, CancellationToken token)
    {
        var attachmentVersion = -1;
        try
        {
            while (await freeSlots.Reader.WaitToReadAsync(token))
            {
                while (freeSlots.Reader.TryRead(out var slot))
                {
                    var version = await WaitUntilAttachedAsync(clip, generation, token);
                    if (version != attachmentVersion)
                    {
                        attachmentVersion = version;
                        pacer.Reset();
                    }
                    var delay = pacer.NextDelay(metrics.Elapsed);
                    if (delay > TimeSpan.Zero) await Task.Delay(delay, token);
                    if (!await ReadFrameAsync(stream, slot.Buffer, token)) return;
                    metrics.MarkDecoded();
                    slot.Sequence = metrics.DecodedFrames;
                    var dropped = frames.Publish(slot);
                    if (dropped is not null)
                    {
                        metrics.MarkDropped();
                        await freeSlots.Writer.WriteAsync(dropped, token);
                    }
                }
            }
        }
        finally { frames.Complete(); }
    }

    private async Task ConsumeFramesAsync(LatestFrameMailbox<FrameSlot> frames, ChannelWriter<FrameSlot> freeSlots, ClipCardViewModel clip, int generation, IClipPreviewPresenter presenter, PixelSize previewSize, int expectedFrameCount, PreviewMetrics metrics, CancellationToken token)
    {
        try
        {
            while (await frames.ReadAsync(token) is { } slot)
            {
                if (!IsCurrent(clip, generation)) return;
                var result = await presenter.PresentAsync(slot.Buffer, previewSize, token);
                metrics.MarkPresent(result.Path, result.Latency);
                metrics.MarkDisplayed();
                await presenter.SetProgressAsync(((slot.Sequence - 1) % expectedFrameCount + 1) / (double)expectedFrameCount);
                await freeSlots.WriteAsync(slot, token);
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
        _ = DisposeDetachedSessionAsync(state, "warm exit expired", state.IsActive);
    }

    private async Task AbandonSessionAsync(ClipCardViewModel clip, int generation)
    {
        SessionState state;
        lock (_stateLock)
        {
            if (!IsCurrentLocked(clip, generation)) return;
            state = DetachActiveLocked();
        }
        await DisposeSessionAsync(state, "activation cancelled", state.IsActive);
    }

    internal static double ResolveFrameRate(double recordedFrameRate) => MaximumFramesPerSecond;

    private static Process StartDecoder(string path, (TimeSpan Start, TimeSpan Duration) range, double frameRate, PixelSize previewSize, string? cropFilter)
    {
        var info = new ProcessStartInfo(FfmpegPathResolver.FfmpegPath) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true, WorkingDirectory = FfmpegPathResolver.WorkingDirectory };
        foreach (var argument in BuildDecoderArguments(path, range, frameRate, previewSize, cropFilter)) info.ArgumentList.Add(argument);
        var process = Process.Start(info) ?? throw new InvalidOperationException("FFmpeg did not start.");
        try { process.PriorityClass = ProcessPriorityClass.BelowNormal; } catch { }
        return process;
    }

    internal static IReadOnlyList<string> BuildDecoderArguments(
        string path,
        (TimeSpan Start, TimeSpan Duration) range,
        double frameRate,
        PixelSize previewSize,
        string? cropFilter = null)
    {
        var start = range.Start.TotalSeconds.ToString("0.###", CultureInfo.InvariantCulture);
        var duration = range.Duration.TotalSeconds.ToString("0.###", CultureInfo.InvariantCulture);
        var fps = ResolveFrameRate(frameRate).ToString("0.###", CultureInfo.InvariantCulture);
        var width = previewSize.Width;
        var height = previewSize.Height;
        // Bilinear, not lanczos. Lanczos costs several times as much per pixel
        // and its sharpening is invisible once output is this small. Static
        // tiles use UniformToFill for normal footage and Uniform for saved crop
        // edits, so FFmpeg must compose frames the same way before they paint.
        var composition = string.IsNullOrWhiteSpace(cropFilter)
            ? $"scale=w={width}:h={height}:flags=bilinear:force_original_aspect_ratio=increase,crop={width}:{height}:(in_w-out_w)/2:(in_h-out_h)/2"
            : $"{cropFilter},scale=w={width}:h={height}:flags=bilinear:force_original_aspect_ratio=decrease,pad={width}:{height}:(ow-iw)/2:(oh-ih)/2";
        return ["-hide_banner", "-loglevel", "error", "-ss", start, "-i", path, "-t", duration,
            "-an", "-vf", $"fps={fps},{composition}",
            "-pix_fmt", "rgba", "-f", "rawvideo", "pipe:1"];
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

    private void SetProcess(ClipCardViewModel clip, int generation, Process process)
    {
        lock (_stateLock) { if (IsCurrentLocked(clip, generation)) _process = process; else Kill(process); }
    }
    private void ClearProcess(Process process) { lock (_stateLock) { if (_process == process) _process = null; } }
    private async Task CleanupAsync(ClipCardViewModel clip, int generation)
    {
        SessionState state;
        lock (_stateLock)
        {
            if (!IsCurrentLocked(clip, generation)) return;
            state = DetachActiveLocked();
        }
        await DisposeSessionAsync(state, "completed", state.IsActive);
    }
    private SessionState DetachActiveLocked()
    {
        _generation++;
        var state = new SessionState(_clip, _presenter, _process, _cancellation, _warmExitCancellation);
        _clip = null; _presenter = null; _previewSize = default; _process = null; _cancellation = null; _warmExitCancellation = null; _attachSignal = null; _attached = false;
        return state;
    }
    private static async Task DisposeSessionAsync(SessionState state, string reason, bool log)
    {
        CancelSession(state);
        if (state.Presenter is not null)
        {
            await state.Presenter.SetAttachedAsync(false);
            await state.Presenter.ReleaseResourcesAsync();
        }
        state.Cancellation?.Dispose();
        state.WarmExitCancellation?.Dispose();
        if (log) AppLog.Info($"Clip hover preview cleanup complete: {reason}.");
    }

    private async Task DisposeDetachedSessionAsync(SessionState state, string reason, bool log)
    {
        // Cancel before waiting. The session lock is held by RunSessionAsync;
        // waiting first leaves FFmpeg alive with nobody reading its pipe and
        // can block cleanup for the entire clip duration.
        CancelSession(state);
        await _sessionLock.WaitAsync();
        try { await DisposeSessionAsync(state, reason, log); }
        finally { _sessionLock.Release(); }
    }
    private static void CancelSession(SessionState state)
    {
        state.Cancellation?.Cancel();
        state.WarmExitCancellation?.Cancel();
        Kill(state.Process);
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
    public void Dispose() { if (_disposed) return; _disposed = true; Stop("window closed"); }

    private sealed class FrameSlot(byte[] buffer)
    {
        public byte[] Buffer { get; } = buffer;
        public int Sequence { get; set; }
    }
    private readonly record struct SessionState(ClipCardViewModel? Clip, IClipPreviewPresenter? Presenter, Process? Process, CancellationTokenSource? Cancellation, CancellationTokenSource? WarmExitCancellation)
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

// Paces frame reads off the decoder pipe at a fixed target rate.
internal sealed class HoverPreviewFramePacer
{
    private readonly TimeSpan _interval;
    private TimeSpan? _nextFrameAt;

    public HoverPreviewFramePacer(double frameRate)
    {
        _interval = TimeSpan.FromSeconds(1 / ClipHoverPreviewController.ResolveFrameRate(frameRate));
    }

    public double CurrentFrameRate => ClipHoverPreviewController.MaximumFramesPerSecond;

    // Re-attach starts a fresh cadence after the pointer was away.
    public void Reset()
    {
        _nextFrameAt = null;
    }

    public TimeSpan NextDelay(TimeSpan now)
    {
        TimeSpan delay;
        if (_nextFrameAt is { } scheduled && now < scheduled)
        {
            delay = scheduled - now;
            _nextFrameAt = scheduled + _interval;
        }
        else
        {
            _nextFrameAt = now + _interval;
            delay = TimeSpan.Zero;
        }
        return delay;
    }
}

internal sealed class PreviewMetrics
{
    private readonly long _requestTimestamp;
    private readonly long _runStartTimestamp;
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private long _firstDecodedTicks = -1;
    private long _firstDisplayedTicks = -1;
    private long _readBytes;
    private long _previousDisplayTicks = -1;
    private long _longestGapTicks;
    private long _totalPresentTicks;
    private long _longestPresentTicks;
    private int _decodedFrames;
    private int _displayedFrames;
    private int _droppedFrames;
    private int _gpuPresents;
    private int _softwarePresents;
    public PreviewMetrics(long requestTimestamp)
    {
        _requestTimestamp = requestTimestamp;
        _runStartTimestamp = Stopwatch.GetTimestamp();
    }

    public TimeSpan Elapsed => _clock.Elapsed;
    public int DecodedFrames => Volatile.Read(ref _decodedFrames);
    public int DisplayedFrames => Volatile.Read(ref _displayedFrames);
    public void MarkDecoded()
    {
        Interlocked.Increment(ref _decodedFrames);
        Interlocked.CompareExchange(ref _firstDecodedTicks, _clock.ElapsedTicks, -1);
    }
    public void MarkDisplayed()
    {
        Interlocked.Increment(ref _displayedFrames);
        var now = _clock.ElapsedTicks;
        Interlocked.CompareExchange(ref _firstDisplayedTicks, now, -1);
        var previous = Interlocked.Exchange(ref _previousDisplayTicks, now);
        if (previous >= 0) InterlockedExtensions.Max(ref _longestGapTicks, now - previous);
    }
    public void MarkDropped() => Interlocked.Increment(ref _droppedFrames);
    public void MarkPresent(PreviewPresentationPath path, TimeSpan latency)
    {
        if (path == PreviewPresentationPath.Gpu) Interlocked.Increment(ref _gpuPresents);
        else Interlocked.Increment(ref _softwarePresents);
        var ticks = (long)(latency.TotalSeconds * Stopwatch.Frequency);
        if (ticks <= 0) return;
        Interlocked.Add(ref _totalPresentTicks, ticks);
        InterlockedExtensions.Max(ref _longestPresentTicks, ticks);
    }
    public void AddReadBytes(long bytes) { if (bytes > 0) Interlocked.Add(ref _readBytes, bytes); }
    public void Log(ClipCardViewModel clip, int generation)
    {
        if (DecodedFrames == 0 && DisplayedFrames == 0) return;
        var elapsed = Math.Max(_clock.Elapsed.TotalSeconds, 0.001);
        var firstDecoded = TicksToMilliseconds(Volatile.Read(ref _firstDecodedTicks));
        var firstDisplayed = TicksToMilliseconds(Volatile.Read(ref _firstDisplayedTicks));
        var hoverToFirstDisplayed = firstDisplayed < 0
            ? -1
            : Stopwatch.GetElapsedTime(_requestTimestamp, _runStartTimestamp).TotalMilliseconds + firstDisplayed;
        var longestGap = TicksToMilliseconds(Volatile.Read(ref _longestGapTicks));
        var presents = DisplayedFrames;
        var averagePresent = presents == 0 ? 0 : TicksToMilliseconds(Volatile.Read(ref _totalPresentTicks)) / presents;
        var longestPresent = TicksToMilliseconds(Volatile.Read(ref _longestPresentTicks));
        var readMb = Volatile.Read(ref _readBytes) / (1024d * 1024d);
        var path = Volatile.Read(ref _gpuPresents) > 0 ? (Volatile.Read(ref _softwarePresents) > 0 ? "gpu+software" : "gpu") : "software";
        var steadyFps = firstDisplayed < 0
            ? 0
            : Math.Max(0, DisplayedFrames - 1) / Math.Max(elapsed - firstDisplayed / 1000, 0.001);
        AppLog.Debug($"Clip hover preview metrics: {Path.GetFileName(clip.Path)}, generation={generation}, path={path}, firstDecodedMs={firstDecoded:0}, firstDisplayedMs={firstDisplayed:0}, hoverToFirstDisplayedMs={hoverToFirstDisplayed:0}, decoded={DecodedFrames}, displayed={DisplayedFrames}, staleDrops={Volatile.Read(ref _droppedFrames)}, targetFps={ClipHoverPreviewController.ResolveFrameRate(clip.Media.Fps):0.##}, achievedFps={DisplayedFrames / elapsed:0.##}, steadyFps={steadyFps:0.##}, longestGapMs={longestGap:0}, presentAvgMs={averagePresent:0.##}, presentMaxMs={longestPresent:0.##}, readMB={readMb:0.##}.");
    }
    private static double TicksToMilliseconds(long ticks) => ticks < 0 ? -1 : ticks * 1000d / Stopwatch.Frequency;
}

internal sealed class LatestFrameMailbox<T> where T : class
{
    private readonly object _gate = new();
    private readonly Channel<bool> _signal = Channel.CreateBounded<bool>(new BoundedChannelOptions(1)
    {
        FullMode = BoundedChannelFullMode.DropWrite,
        SingleWriter = true,
        SingleReader = true
    });
    private T? _ready;

    public T? Publish(T frame)
    {
        T? dropped;
        lock (_gate)
        {
            dropped = _ready;
            _ready = frame;
        }
        if (dropped is null) _signal.Writer.TryWrite(true);
        return dropped;
    }

    public async ValueTask<T?> ReadAsync(CancellationToken token)
    {
        while (await _signal.Reader.WaitToReadAsync(token))
        {
            while (_signal.Reader.TryRead(out _))
            {
                lock (_gate)
                {
                    if (_ready is not null)
                    {
                        var frame = _ready;
                        _ready = null;
                        return frame;
                    }
                }
            }
        }
        lock (_gate)
        {
            var frame = _ready;
            _ready = null;
            return frame;
        }
    }

    public void Complete() => _signal.Writer.TryComplete();
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
