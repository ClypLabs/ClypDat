using System.Diagnostics;
using System.Threading.Channels;
using Avalonia;
using Avalonia.Platform;
using ClypDat.App.Controls;
using ClypDat.App.ViewModels;

namespace ClypDat.App.Services;

// One native decode session (NativeClipPreview, C++ in ClypDat.Capture.Native)
// serves one library card at a time. The decoder paces frames at 60fps and
// keeps only its newest one; the UI likewise keeps only the newest
// not-yet-painted frame, so slow paints never pile up behind the decoder.
// Each hover used to start an ffmpeg.exe process and read raw frames through
// a pipe; decoding in-process removes the process start from hover latency.
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
    private NativeClipPreview? _decoder;
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
        if (!enabled || clip.IsSpotifyProcessing || presenter is null || !File.Exists(clip.Path)) return;

        CancellationTokenSource? warmExitCancellation = null;
        CancellationTokenSource? pendingCancellation = null;
        CancellationTokenSource? previousCancellation = null;
        NativeClipPreview? previousDecoder = null;
        IClipPreviewPresenter? restartPresenter = null;
        var restart = false;
        var token = CancellationToken.None;
        var pendingGeneration = 0;
        var requestTimestamp = 0L;
        lock (_stateLock)
        {
            if (_disposed) return;
            if (_clip == clip && _previewSize == previewSize)
            {
                warmExitCancellation = _warmExitCancellation;
                _warmExitCancellation = null;
                if (warmExitCancellation is not null)
                {
                    // The surface stays allocated during grace, but remains
                    // hidden until the replacement decoder stages frame one.
                    previousCancellation = _cancellation;
                    previousDecoder = _decoder;
                    _cancellation = new CancellationTokenSource();
                    _decoder = null;
                    restartPresenter = _presenter;
                    token = _cancellation.Token;
                    pendingGeneration = ++_generation;
                    _attached = false;
                    requestTimestamp = Stopwatch.GetTimestamp();
                    restart = true;
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
        if (restart && restartPresenter is not null)
        {
            _ = RestartAttachedSessionAsync(clip, pendingGeneration, previewSize, restartPresenter,
                token, previousCancellation, previousDecoder, requestTimestamp);
            AppLog.Debug($"Clip hover preview warm restart: {Path.GetFileName(clip.Path)}.");
        }
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
        CancellationTokenSource? previousWarmExit = null;
        CancellationTokenSource? decoderCancellation = null;
        NativeClipPreview? decoder = null;
        IClipPreviewPresenter? presenter = null;
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
                previousWarmExit = _warmExitCancellation;
                _warmExitCancellation = new CancellationTokenSource();
                warmToken = _warmExitCancellation.Token;
                decoderCancellation = _cancellation;
                decoder = _decoder;
                _decoder = null;
                presenter = _presenter;
                _attached = false;
                _attachmentVersion++;
                _attachSignal?.TrySetResult();
                generation = ++_generation;
                active = true;
            }
        }

        pendingCancellation?.Cancel();
        pendingCancellation?.Dispose();
        if (pendingCancelled) AppLog.Debug($"Clip hover preview pending cancelled: {Path.GetFileName(clip.Path)}.");
        if (!active) return;

        previousWarmExit?.Cancel();
        previousWarmExit?.Dispose();
        if (presenter is not null) _ = DetachAndStopDecoderAsync(presenter, decoderCancellation, decoder);
        _ = ExpireWarmSessionAsync(clip, generation, warmToken);
    }

    public void Stop(string reason) => _ = StopAsync(reason);

    public Task StopAsync(string reason)
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
        return DisposeDetachedSessionAsync(state, reason, state.IsActive);
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
            await DisposeDetachedSessionAsync(previous, "replaced", previous.IsActive);

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
        using var processingRead = SpotifyProcessingPaths.TryRead(clip.Path);
        if (processingRead is null) return;
        var metrics = new PreviewMetrics(requestTimestamp);
        try
        {
            var range = clip.HoverPreviewRange;
            if (range.Duration <= TimeSpan.Zero) return;
            var frameRate = MaximumFramesPerSecond;
            var expectedFrameCount = FramesPerLoop(range.Duration);
            var frameBytes = previewSize.Width * previewSize.Height * 4;
            if (!IsCurrent(clip, generation)) return;
            var sourceMbps = clip.Duration > TimeSpan.Zero ? clip.SizeBytes * 8d / clip.Duration.TotalSeconds / 1_000_000d : 0;
            AppLog.Info($"Clip hover preview started: {Path.GetFileName(clip.Path)}, source={clip.Media.Width}x{clip.Media.Height}, sourceMbps={sourceMbps:0.###}, output={previewSize.Width}x{previewSize.Height}, targetFps={frameRate:0.###} (recorded={clip.Media.Fps:0.###}).");
            var slots = new[] { new FrameSlot(new byte[frameBytes]), new FrameSlot(new byte[frameBytes]), new FrameSlot(new byte[frameBytes]) };

            // The decoder loops over the range itself; a new one only replaces
            // a decoder that ended without being stopped.
            while (!token.IsCancellationRequested && IsCurrent(clip, generation))
            {
                using var decoder = NativeClipPreview.Open(clip.Path, range.Start, range.Duration,
                    previewSize.Width, previewSize.Height, frameRate, clip.HoverPreviewCrop);
                try
                {
                    if (!SetDecoder(clip, generation, decoder)) return;
                    using var stopOnCancel = token.Register(decoder.Stop);
                    var (decoded, displayed) = await DeliverFramesAsync(decoder, slots, clip, generation, presenter, previewSize, expectedFrameCount, metrics, token);
                    var error = decoder.Error();
                    if (!token.IsCancellationRequested && IsCurrent(clip, generation) && (error.Length > 0 || (decoded == 0 && displayed == 0)))
                    {
                        AppLog.Info($"Clip hover preview decoder failed: {Path.GetFileName(clip.Path)}. {error}");
                        return;
                    }
                }
                finally
                {
                    decoder.Stop();
                    ClearDecoder(decoder);
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

    private async Task<(int Decoded, int Displayed)> DeliverFramesAsync(NativeClipPreview decoder, IReadOnlyList<FrameSlot> slots, ClipCardViewModel clip, int generation, IClipPreviewPresenter presenter, PixelSize previewSize, int expectedFrameCount, PreviewMetrics metrics, CancellationToken token)
    {
        var decodedBefore = metrics.DecodedFrames;
        var displayedBefore = metrics.DisplayedFrames;
        var frames = new LatestFrameMailbox<FrameSlot>();
        var freeSlots = Channel.CreateBounded<FrameSlot>(new BoundedChannelOptions(3) { FullMode = BoundedChannelFullMode.Wait, SingleWriter = false, SingleReader = true });
        foreach (var slot in slots) await freeSlots.Writer.WriteAsync(slot, token);

        var producer = ProduceFramesAsync(decoder, freeSlots, frames, metrics, token);
        var consumer = ConsumeFramesAsync(frames, freeSlots.Writer, clip, generation, presenter, previewSize, expectedFrameCount, metrics, token);
        await Task.WhenAll(producer, consumer);
        return (metrics.DecodedFrames - decodedBefore, metrics.DisplayedFrames - displayedBefore);
    }

    private async Task RestartAttachedSessionAsync(
        ClipCardViewModel clip,
        int generation,
        PixelSize previewSize,
        IClipPreviewPresenter presenter,
        CancellationToken token,
        CancellationTokenSource? previousCancellation,
        NativeClipPreview? previousDecoder,
        long requestTimestamp)
    {
        previousCancellation?.Cancel();
        StopDecoder(previousDecoder);

        // Wait without replacement token. Previous RunSessionAsync can then
        // finish before its cancellation source is disposed.
        await _sessionLock.WaitAsync();
        try
        {
            previousCancellation?.Dispose();
            token.ThrowIfCancellationRequested();
            if (!IsCurrent(clip, generation)) return;

            await presenter.ActivateSessionAsync(token);
            await presenter.SetProgressAsync(0);
            await RunSessionAsync(clip, generation, previewSize, presenter, token, requestTimestamp);
        }
        catch (OperationCanceledException) { }
        catch (Exception error) { AppLog.Error("Clip hover preview restart failed", error); }
        finally { _sessionLock.Release(); }
    }

    // The native decoder paces itself and keeps only its newest frame, so this
    // just copies each new one into a free slot. Take blocks, so the loop runs
    // on its own thread rather than holding a pool thread per hover.
    private static Task ProduceFramesAsync(NativeClipPreview decoder, Channel<FrameSlot> freeSlots, LatestFrameMailbox<FrameSlot> frames, PreviewMetrics metrics, CancellationToken token)
        => Task.Factory.StartNew(() =>
        {
            try
            {
                ulong last = 0;
                long bytesRead = 0;
                FrameSlot? slot = null;
                while (!token.IsCancellationRequested)
                {
                    if (slot is null && !freeSlots.Reader.TryRead(out slot))
                    {
                        if (!freeSlots.Reader.WaitToReadAsync(token).AsTask().GetAwaiter().GetResult()) return;
                        continue;
                    }
                    var taken = decoder.Take(last, slot.Buffer, 100);
                    metrics.AddReadBytes(taken.SourceBytesRead - bytesRead);
                    bytesRead = taken.SourceBytesRead;
                    if (taken.Sequence == 0)
                    {
                        if (taken.Finished) return;
                        continue;
                    }
                    last = taken.Sequence;
                    metrics.MarkDecoded();
                    slot.Sequence = taken.Sequence;
                    var dropped = frames.Publish(slot);
                    slot = null;
                    if (dropped is not null)
                    {
                        metrics.MarkDropped();
                        slot = dropped;
                    }
                }
            }
            catch (OperationCanceledException) { }
            finally { frames.Complete(); }
        }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);

    private async Task ConsumeFramesAsync(LatestFrameMailbox<FrameSlot> frames, ChannelWriter<FrameSlot> freeSlots, ClipCardViewModel clip, int generation, IClipPreviewPresenter presenter, PixelSize previewSize, int expectedFrameCount, PreviewMetrics metrics, CancellationToken token)
    {
        try
        {
            while (await frames.ReadAsync(token) is { } slot)
            {
                if (!IsCurrent(clip, generation)) return;
                var result = await presenter.PresentAsync(slot.Buffer, previewSize, token);
                if (AttachAfterStaging(clip, generation)) await presenter.SetAttachedAsync(true);
                metrics.MarkPresent(result.Path, result.Latency);
                metrics.MarkDisplayed();
                await presenter.SetProgressAsync(((slot.Sequence - 1) % (ulong)expectedFrameCount + 1) / (double)expectedFrameCount);
                await freeSlots.WriteAsync(slot, token);
            }
        }
        finally { freeSlots.TryComplete(); }
    }

    private bool AttachAfterStaging(ClipCardViewModel clip, int generation)
    {
        lock (_stateLock)
        {
            if (!IsCurrentLocked(clip, generation) || _attached) return false;
            _attached = true;
            _attachmentVersion++;
            return true;
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

    private static async Task DetachAndStopDecoderAsync(IClipPreviewPresenter presenter, CancellationTokenSource? cancellation, NativeClipPreview? decoder)
    {
        var detach = presenter.SetAttachedAsync(false);
        cancellation?.Cancel();
        StopDecoder(decoder);
        await detach;
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

    // Output frames per pass over the range: ceil(duration * fps), computed on
    // the same whole microseconds the native decoder receives.
    internal static int FramesPerLoop(TimeSpan duration)
    {
        var microseconds = Math.Max(0, duration.Ticks / 10);
        return (int)Math.Max(1, (microseconds * MaximumFramesPerSecond + 999_999) / 1_000_000);
    }

    private bool SetDecoder(ClipCardViewModel clip, int generation, NativeClipPreview decoder)
    {
        lock (_stateLock)
        {
            if (!IsCurrentLocked(clip, generation)) return false;
            _decoder = decoder;
            return true;
        }
    }
    private void ClearDecoder(NativeClipPreview decoder) { lock (_stateLock) { if (_decoder == decoder) _decoder = null; } }
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
        var state = new SessionState(_clip, _presenter, _decoder, _cancellation, _warmExitCancellation);
        _clip = null; _presenter = null; _previewSize = default; _decoder = null; _cancellation = null; _warmExitCancellation = null; _attachSignal = null; _attached = false;
        return state;
    }
    private static async Task DisposeSessionAsync(SessionState state, string reason, bool log, bool presenterDetached = false)
    {
        // The run that opened the decoder disposes it once its frame loop has
        // unwound; this only has to stop it.
        CancelSession(state);
        if (state.Presenter is not null)
        {
            if (!presenterDetached) await state.Presenter.SetAttachedAsync(false);
            await state.Presenter.ReleaseResourcesAsync();
        }
        state.Cancellation?.Dispose();
        state.WarmExitCancellation?.Dispose();
        if (log) AppLog.Info($"Clip hover preview cleanup complete: {reason}.");
    }

    private async Task DisposeDetachedSessionAsync(SessionState state, string reason, bool log)
    {
        // Hide first. Decoder shutdown can wait behind RunSessionAsync without
        // leaving a stale moving overlay on screen.
        if (state.Presenter is not null) await state.Presenter.SetAttachedAsync(false);
        CancelSession(state);
        await _sessionLock.WaitAsync();
        try { await DisposeSessionAsync(state, reason, log, presenterDetached: state.Presenter is not null); }
        finally { _sessionLock.Release(); }
    }
    private static void CancelSession(SessionState state)
    {
        state.Cancellation?.Cancel();
        state.WarmExitCancellation?.Cancel();
        StopDecoder(state.Decoder);
    }
    private bool IsPending(ClipCardViewModel clip, int generation) { lock (_stateLock) return IsPendingLocked(clip, generation); }
    private bool IsPendingLocked(ClipCardViewModel clip, int generation) => !_disposed && _pendingGeneration == generation && _pendingClip == clip;
    private bool IsCurrent(ClipCardViewModel clip, int generation) { lock (_stateLock) return IsCurrentLocked(clip, generation); }
    private bool IsCurrentLocked(ClipCardViewModel clip, int generation) => !_disposed && _generation == generation && _clip == clip;
    private static void StopDecoder(NativeClipPreview? decoder) => decoder?.Stop();
    public void Dispose() { if (_disposed) return; _disposed = true; Stop("window closed"); }

    private sealed class FrameSlot(byte[] buffer)
    {
        public byte[] Buffer { get; } = buffer;
        public ulong Sequence { get; set; }
    }
    private readonly record struct SessionState(ClipCardViewModel? Clip, IClipPreviewPresenter? Presenter, NativeClipPreview? Decoder, CancellationTokenSource? Cancellation, CancellationTokenSource? WarmExitCancellation)
    { public bool IsActive => Clip is not null || Decoder is not null || Cancellation is not null; }
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
