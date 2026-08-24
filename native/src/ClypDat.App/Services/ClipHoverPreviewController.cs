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
    // GPU composition uploads only card-sized RGBA textures. Keep source
    // cadence up to 60fps; low-rate clips remain at their native cadence.
    internal const int MaximumFramesPerSecond = 60;
    internal const int DefaultFramesPerSecond = 30;
    // What an overloaded preview falls back to. Previews run at the recorded
    // rate by default; HoverPreviewFramePacer watches whether the machine is
    // actually sustaining that and steps down to this when it isn't, rather
    // than letting a preview that can only manage 22fps keep asking for 60 and
    // burning the difference on frames nobody sees.
    internal const int ReducedFramesPerSecond = 30;
    // Used when the card hasn't been laid out yet (no bounds to measure).
    internal const int DefaultPreviewWidth = 480;
    internal const int DefaultPreviewHeight = 270;
    // Long-edge cap. Past this the extra pixels cost pipe bandwidth and UI
    // upload time without being resolvable on a library card.
    internal const int MaximumPreviewWidth = 640;
    private const int MinimumPreviewWidth = 160;
    private const int MinimumPreviewHeight = 90;
    // 75ms was short enough that sweeping the pointer across the library
    // spawned a full ffmpeg decoder for cards the user was only passing over.
    // The log shows the shape plainly - repeated "preview started" followed by
    // "warm exit expired" 200-400ms later, each one a process launch, a seek,
    // and a renderer session thrown away. 180ms still feels
    // immediate on a card the pointer actually settles on, and costs nothing
    // for the ones it does not.
    internal static readonly TimeSpan HoverDelay = TimeSpan.FromMilliseconds(180);
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
                goto StartPending;
            }
        }

        warmExitCancellation?.Cancel();
        warmExitCancellation?.Dispose();
        warmPresenter?.SetAttached(true);
        attachSignal?.TrySetResult();
        if (warmReused) AppLog.Debug($"Clip hover preview warm reuse: {Path.GetFileName(clip.Path)}.");
        return;

    StartPending:
        pendingCancellation?.Cancel();
        pendingCancellation?.Dispose();
        _ = StartPendingAsync(clip, presenter, previewSize, pendingGeneration, token);
    }

    // Pixel size to decode at, from the card's laid-out DIP size and the
    // window's render scaling. Even dimensions: the decoder's yuv->rgba path
    // and texture upload both want them, and an odd height silently breaks
    // some scaler configurations.
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

        if (width > MaximumPreviewWidth)
        {
            height *= MaximumPreviewWidth / width;
            width = MaximumPreviewWidth;
        }

        return new PixelSize(RoundUpToEven(width, MinimumPreviewWidth), RoundUpToEven(height, MinimumPreviewHeight));
    }

    private static int RoundUpToEven(double value, int minimum)
    {
        var rounded = Math.Max(minimum, (int)Math.Round(value));
        return rounded % 2 == 0 ? rounded : rounded + 1;
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

        presenter?.SetAttached(false);
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

    private async Task StartPendingAsync(ClipCardViewModel clip, IClipPreviewPresenter presenter, PixelSize previewSize, int pendingGeneration, CancellationToken token)
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
                    _previewSize = previewSize;
                    _presenter = presenter;
                    _attached = true;
                    _attachmentVersion++;
                    generation = ++_generation;
                }
                presenter.SetAttached(true);
                await RunSessionAsync(clip, generation, previewSize, presenter, cancellation.Token);
            }
            finally { _sessionLock.Release(); }
        }
        catch (OperationCanceledException) { }
        catch (Exception error) { AppLog.Error("Clip hover preview failed", error); }
    }

    private async Task RunSessionAsync(ClipCardViewModel clip, int generation, PixelSize previewSize, IClipPreviewPresenter presenter, CancellationToken token)
    {
        var metrics = new PreviewMetrics();
        try
        {
            var range = clip.HoverPreviewRange;
            if (range.Duration <= TimeSpan.Zero) return;
            var frameRate = ResolveFrameRate(clip.Media.Fps);
            // Session-scoped, not per decoder run: the whole point is to carry
            // what it learned about this machine across the preview's loop
            // restarts instead of re-optimistically asking for 60 every time
            // the clip wraps around.
            var pacer = new HoverPreviewFramePacer(frameRate);
            var frameBytes = previewSize.Width * previewSize.Height * 4;
            if (!IsCurrent(clip, generation)) return;
            var sourceMbps = clip.Duration > TimeSpan.Zero ? clip.SizeBytes * 8d / clip.Duration.TotalSeconds / 1_000_000d : 0;
            AppLog.Info($"Clip hover preview started: {Path.GetFileName(clip.Path)}, source={clip.Media.Width}x{clip.Media.Height}, sourceMbps={sourceMbps:0.###}, output={previewSize.Width}x{previewSize.Height}, targetFps={frameRate:0.###} (recorded={clip.Media.Fps:0.###}).");
            var slots = new[] { new FrameSlot(new byte[frameBytes]), new FrameSlot(new byte[frameBytes]), new FrameSlot(new byte[frameBytes]) };

            while (!token.IsCancellationRequested && IsCurrent(clip, generation))
            {
                // Ask FFmpeg for whatever rate the pacer has settled on. Once
                // it has stepped down, this stops the decode work happening at
                // all rather than merely throttling reads off the pipe.
                using var process = StartDecoder(clip.Path, range, pacer.CurrentFrameRate, previewSize);
                SetProcess(clip, generation, process);
                var stderr = process.StandardError.ReadToEndAsync();
                var sourceReadsBefore = GetReadBytes(process);
                var (decoded, displayed) = await DeliverFramesAsync(process.StandardOutput.BaseStream, slots, clip, generation, presenter, previewSize, pacer, metrics, token);
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

    private async Task<(int Decoded, int Displayed)> DeliverFramesAsync(Stream stream, IReadOnlyList<FrameSlot> slots, ClipCardViewModel clip, int generation, IClipPreviewPresenter presenter, PixelSize previewSize, HoverPreviewFramePacer pacer, PreviewMetrics metrics, CancellationToken token)
    {
        var decodedBefore = metrics.DecodedFrames;
        var displayedBefore = metrics.DisplayedFrames;
        var frames = new LatestFrameMailbox<FrameSlot>();
        var freeSlots = Channel.CreateBounded<FrameSlot>(new BoundedChannelOptions(3) { FullMode = BoundedChannelFullMode.Wait, SingleWriter = true, SingleReader = true });
        foreach (var slot in slots) await freeSlots.Writer.WriteAsync(slot, token);

        var producer = ProduceFramesAsync(stream, freeSlots, frames, clip, generation, pacer, metrics, token);
        var consumer = ConsumeFramesAsync(frames, freeSlots.Writer, clip, generation, presenter, previewSize, pacer, metrics, token);
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
                    if (pacer.TryConsumeRateChange(out var newRate))
                    {
                        metrics.MarkRateTransition();
                        AppLog.Info($"Clip hover preview rate adapted: {Path.GetFileName(clip.Path)}, now {newRate:0.###} fps (recorded={clip.Media.Fps:0.###}) - the preview was not sustaining the previous rate.");
                        // Decoder fps is process configuration. Stop this run
                        // so the enclosing loop immediately starts FFmpeg at
                        // the newly selected target instead of spending the
                        // rest of a long clip decoding the old rate.
                        KillCurrentProcess(clip, generation);
                        return;
                    }
                    if (delay > TimeSpan.Zero) await Task.Delay(delay, token);
                    if (!await ReadFrameAsync(stream, slot.Buffer, token)) return;
                    metrics.MarkDecoded();
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

    private async Task ConsumeFramesAsync(LatestFrameMailbox<FrameSlot> frames, ChannelWriter<FrameSlot> freeSlots, ClipCardViewModel clip, int generation, IClipPreviewPresenter presenter, PixelSize previewSize, HoverPreviewFramePacer pacer, PreviewMetrics metrics, CancellationToken token)
    {
        try
        {
            while (await frames.ReadAsync(token) is { } slot)
            {
                if (!IsCurrent(clip, generation)) return;
                var result = await presenter.PresentAsync(slot.Buffer, previewSize, token);
                metrics.MarkPresent(result.Path, result.Latency);
                metrics.MarkDisplayed();
                pacer.ObservePresentLatency(result.Latency);
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
        DisposeSession(state, "warm exit expired", state.IsActive);
    }

    internal static double ResolveFrameRate(double recordedFrameRate) =>
        double.IsFinite(recordedFrameRate) && recordedFrameRate > 0
            ? Math.Clamp(recordedFrameRate, 1, MaximumFramesPerSecond)
            : DefaultFramesPerSecond;

    private static Process StartDecoder(string path, (TimeSpan Start, TimeSpan Duration) range, double frameRate, PixelSize previewSize)
    {
        var info = new ProcessStartInfo("ffmpeg") { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
        foreach (var argument in BuildDecoderArguments(path, range, frameRate, previewSize)) info.ArgumentList.Add(argument);
        var process = Process.Start(info) ?? throw new InvalidOperationException("FFmpeg did not start.");
        try { process.PriorityClass = ProcessPriorityClass.BelowNormal; } catch { }
        return process;
    }

    internal static IReadOnlyList<string> BuildDecoderArguments(string path, (TimeSpan Start, TimeSpan Duration) range, double frameRate, PixelSize previewSize)
    {
        var start = range.Start.TotalSeconds.ToString("0.###", CultureInfo.InvariantCulture);
        var duration = range.Duration.TotalSeconds.ToString("0.###", CultureInfo.InvariantCulture);
        var fps = ResolveFrameRate(frameRate).ToString("0.###", CultureInfo.InvariantCulture);
        var width = previewSize.Width;
        var height = previewSize.Height;
        // bilinear, not lanczos. Lanczos costs several times as much per pixel
        // and its sharpening is invisible once the output is this small - and
        // at the old fixed 1920x1080 it was paying that cost for a 1080p source
        // that wasn't even being resized.
        return ["-hide_banner", "-loglevel", "error", "-ss", start, "-i", path, "-t", duration,
            "-an", "-vf", $"fps={fps},scale=w={width}:h={height}:flags=bilinear:force_original_aspect_ratio=decrease,pad={width}:{height}:(ow-iw)/2:(oh-ih)/2",
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
    private void KillCurrentProcess(ClipCardViewModel clip, int generation)
    {
        Process? process;
        lock (_stateLock) process = IsCurrentLocked(clip, generation) ? _process : null;
        Kill(process);
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
        var state = new SessionState(_clip, _presenter, _process, _cancellation, _warmExitCancellation);
        _clip = null; _presenter = null; _previewSize = default; _process = null; _cancellation = null; _warmExitCancellation = null; _attachSignal = null; _attached = false;
        return state;
    }
    private static void DisposeSession(SessionState state, string reason, bool log)
    {
        state.Cancellation?.Cancel();
        state.WarmExitCancellation?.Cancel();
        Kill(state.Process);
        state.Presenter?.SetAttached(false);
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

    private sealed class FrameSlot(byte[] buffer) { public byte[] Buffer { get; } = buffer; }
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

// Paces frame reads off the decoder pipe, and watches whether the machine is
// keeping up with the rate it asked for.
//
// The overload signal is already in the pacing itself: NextDelay returns a
// positive delay when the caller arrived ahead of schedule (there is headroom)
// and Zero when it arrived late (there is not). A preview that is genuinely
// sustaining its rate is ahead most of the time; one that is late on most
// frames is being asked for a rate this machine cannot deliver, and every frame
// it does manage costs decode, pipe and UI-upload work for a result nobody sees
// as smoother. In that case step down to ReducedFramesPerSecond, which also
// halves what FFmpeg is asked to decode on the next loop restart.
internal sealed class HoverPreviewFramePacer
{
    // One observation window. At 60fps that is a second of preview - long
    // enough that a single slow paint doesn't trip it, short enough to react
    // while the user is still hovering.
    private const int WindowSize = 60;
    // Late on at least half the window: not keeping up.
    private const double DegradeLateShare = 0.5;
    // Comfortably ahead. The gap between this and DegradeLateShare is the
    // hysteresis band that stops a machine sitting right on the boundary from
    // flapping between rates.
    private const double RestoreLateShare = 0.15;
    // Consecutive clean windows before full rate is tried again. Restoring is
    // far more cautious than degrading, for the same reason EncoderTuningService
    // promotes cautiously: a wrong degrade costs preview smoothness, a wrong
    // restore costs the capture pipeline CPU it cannot spare.
    private const int RestoreCleanWindows = 5;
    // Two step-downs in one preview session means this is not a blip - stop
    // offering full rate back for the rest of the session.
    private const int MaximumDegrades = 2;

    private readonly double _requestedFrameRate;
    private readonly double _reducedFrameRate;
    private TimeSpan _interval;
    private TimeSpan? _nextFrameAt;
    private int _windowCount;
    private int _lateCount;
    private int _cleanWindows;
    private int _degrades;
    private bool _reduced;
    private double? _pendingRateChange;

    public HoverPreviewFramePacer(double frameRate)
    {
        _requestedFrameRate = ClipHoverPreviewController.ResolveFrameRate(frameRate);
        _reducedFrameRate = Math.Min(_requestedFrameRate, ClipHoverPreviewController.ReducedFramesPerSecond);
        _interval = TimeSpan.FromSeconds(1 / _requestedFrameRate);
    }

    public double CurrentFrameRate => _reduced ? _reducedFrameRate : _requestedFrameRate;
    public bool IsReduced => _reduced;

    // True once per transition, so the caller can log it without the pacer
    // needing to know which clip it belongs to.
    public bool TryConsumeRateChange(out double frameRate)
    {
        if (_pendingRateChange is not { } pending)
        {
            frameRate = 0;
            return false;
        }
        _pendingRateChange = null;
        frameRate = pending;
        return true;
    }

    // Called when the preview re-attaches after the pointer left and came back.
    // The schedule is stale (wall time ran on while detached) and so is the
    // observation window - frames missed while nothing was being painted say
    // nothing about whether this machine can sustain the rate.
    public void Reset()
    {
        _nextFrameAt = null;
        _windowCount = 0;
        _lateCount = 0;
    }

    public TimeSpan NextDelay(TimeSpan now)
    {
        var hadSchedule = _nextFrameAt is not null;
        TimeSpan delay;
        bool late;
        if (_nextFrameAt is { } scheduled && now < scheduled)
        {
            late = false;
            delay = scheduled - now;
            _nextFrameAt = scheduled + _interval;
        }
        else
        {
            late = true;
            _nextFrameAt = now + _interval;
            delay = TimeSpan.Zero;
        }

        // The first call after a reset has no schedule to be late against, so
        // it is late by construction and must not count as evidence.
        if (hadSchedule) Observe(late);
        return delay;
    }

    // Presentation completion is a real deadline too. GPU slots may still be
    // held by the compositor even when pipe reads were on time; include that
    // latency so adaptation protects replay capture from compositor stalls.
    public void ObservePresentLatency(TimeSpan latency)
    {
        if (latency > _interval) Observe(late: true);
    }

    private void Observe(bool late)
    {
        // Nothing to step down to (a 24fps clip is already at or below the
        // reduced rate), or already stepped down as far as this is allowed to.
        if (_requestedFrameRate <= _reducedFrameRate) return;
        if (_reduced && _degrades >= MaximumDegrades) return;

        _windowCount++;
        if (late) _lateCount++;
        if (_windowCount < WindowSize) return;

        var lateShare = (double)_lateCount / _windowCount;
        _windowCount = 0;
        _lateCount = 0;

        if (!_reduced)
        {
            if (lateShare < DegradeLateShare) return;
            _degrades++;
            SetReduced(true);
            return;
        }

        if (lateShare > RestoreLateShare)
        {
            _cleanWindows = 0;
            return;
        }

        if (++_cleanWindows < RestoreCleanWindows) return;
        _cleanWindows = 0;
        SetReduced(false);
    }

    private void SetReduced(bool reduced)
    {
        _reduced = reduced;
        _interval = TimeSpan.FromSeconds(1 / CurrentFrameRate);
        // The old schedule was built on the old interval - re-anchor rather
        // than carrying a deadline that means something different now.
        _nextFrameAt = null;
        _pendingRateChange = CurrentFrameRate;
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
    private long _totalPresentTicks;
    private long _longestPresentTicks;
    private int _decodedFrames;
    private int _displayedFrames;
    private int _droppedFrames;
    private int _gpuPresents;
    private int _softwarePresents;
    private int _rateTransitions;

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
    public void MarkRateTransition() => Interlocked.Increment(ref _rateTransitions);
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
        var longestGap = TicksToMilliseconds(Volatile.Read(ref _longestGapTicks));
        var presents = DisplayedFrames;
        var averagePresent = presents == 0 ? 0 : TicksToMilliseconds(Volatile.Read(ref _totalPresentTicks)) / presents;
        var longestPresent = TicksToMilliseconds(Volatile.Read(ref _longestPresentTicks));
        var readMb = Volatile.Read(ref _readBytes) / (1024d * 1024d);
        var path = Volatile.Read(ref _gpuPresents) > 0 ? (Volatile.Read(ref _softwarePresents) > 0 ? "gpu+software" : "gpu") : "software";
        AppLog.Debug($"Clip hover preview metrics: {Path.GetFileName(clip.Path)}, generation={generation}, path={path}, firstDecodedMs={firstDecoded:0}, firstDisplayedMs={firstDisplayed:0}, decoded={DecodedFrames}, displayed={DisplayedFrames}, staleDrops={Volatile.Read(ref _droppedFrames)}, targetFps={ClipHoverPreviewController.ResolveFrameRate(clip.Media.Fps):0.##}, achievedFps={DisplayedFrames / elapsed:0.##}, rateTransitions={Volatile.Read(ref _rateTransitions)}, longestGapMs={longestGap:0}, presentAvgMs={averagePresent:0.##}, presentMaxMs={longestPresent:0.##}, readMB={readMb:0.##}.");
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
