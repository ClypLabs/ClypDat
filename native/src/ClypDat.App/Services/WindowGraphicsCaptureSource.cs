using System.Runtime.Versioning;
using System.Diagnostics;
using Windows.Graphics;
using Windows.Graphics.Capture;
using Windows.Graphics.DirectX;
using Vortice.Direct3D11;
using Vortice.DXGI;

namespace ClypDat.App.Services;

// Owns short-lived WGC frames on its callback thread. Consumers receive only
// an application-owned texture, so an encoder can never starve WGC's pool.
[SupportedOSPlatform("windows10.0.17763.0")]
internal sealed class WindowGraphicsCaptureSource : IGameFrameSource, IDisposable
{
    private const int FramePoolBufferCount = 3;
    private readonly ID3D11Device _device;
    private readonly object _d3dLock;
    private readonly object _stateLock = new();
    private readonly Windows.Graphics.DirectX.Direct3D11.IDirect3DDevice _direct3DDevice;
    private readonly GraphicsCaptureItem _item;
    private readonly LatestFrameSignal _frameSignal = new();
    private Direct3D11CaptureFramePool? _framePool;
    private GraphicsCaptureSession? _session;
    private ID3D11Texture2D? _latestTexture;
    private SizeInt32 _contentSize;
    private string? _failure;
    private bool _disposed;
    private long _callbackArrivals;
    // Frames actually taken out of the pool, which is not the same as callbacks:
    // one callback drains everything queued. The source rate is frames.
    private long _framesDelivered;
    private long _callbackDurationTicks;
    private long _gpuLockWaitTicks;
    private long _sourceTimestampGapTicks;
    private long _sourceTimestampGapCount;
    private long _sourceTimestampGapMaxTicks;
    private long _resizeEvents;
    private TimeSpan _lastSourceTimestamp;
    private WgcMinimumUpdateIntervalResult _minimumUpdateInterval;
    // Frames leave the frame pool on composition ticks, so the requested
    // minimum update interval has to be expressed on that grid - see
    // WgcMinimumUpdateIntervalPolicy. Not readonly: a window dragged to another
    // display, or a mode change, moves the grid under a live session.
    private double _displayRefreshHz;

    private WindowGraphicsCaptureSource(ID3D11Device device, object d3dLock, GraphicsCaptureItem item, bool captureCursor, int frameRate, double displayRefreshHz)
    {
        _device = device;
        _d3dLock = d3dLock;
        _item = item;
        _displayRefreshHz = displayRefreshHz;
        _contentSize = item.Size;
        if (_contentSize.Width < 1 || _contentSize.Height < 1) throw new InvalidOperationException("WGC reported an empty window size.");
        _direct3DDevice = CaptureInterop.CreateDirect3DDevice(device);
        _framePool = Direct3D11CaptureFramePool.CreateFreeThreaded(_direct3DDevice, DirectXPixelFormat.B8G8R8A8UIntNormalized, FramePoolBufferCount, _contentSize);
        _framePool.FrameArrived += FramePool_FrameArrived;
        _item.Closed += CaptureItem_Closed;
        _session = _framePool.CreateCaptureSession(item);
        if (OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22000))
        {
            try { _session.IsBorderRequired = false; }
            catch (Exception error) { AppLog.Info($"Native capture: WGC border setting unavailable; Windows will show its capture indicator ({error.Message})."); }
        }
        if (OperatingSystem.IsWindowsVersionAtLeast(10, 0, 19041))
        {
            try
            {
                _session.IsCursorCaptureEnabled = captureCursor;
                // WGC composites the cursor into the frame itself. Whoever consumes
                // this source must not draw a second one - see CursorCaptureApplied.
                CursorCaptureApplied = captureCursor;
            }
            catch (Exception error) { AppLog.Info($"Native capture: WGC cursor setting unavailable; using system default ({error.Message})."); }
        }
        TrySetTargetFrameRate(frameRate);
        _session.StartCapture();
    }

    public static WindowGraphicsCaptureSource Create(ID3D11Device device, object d3dLock, nint windowHandle, bool captureCursor, int frameRate) =>
        new(device, d3dLock, CaptureInterop.CreateItemForWindow(windowHandle), captureCursor, frameRate,
            DisplayRefreshService.GetRefreshHzForWindow(windowHandle));

    // Monitor-backed WGC item for desktop capture.
    public static WindowGraphicsCaptureSource CreateForMonitor(ID3D11Device device, object d3dLock, nint monitorHandle, bool captureCursor, int frameRate) =>
        new(device, d3dLock, CaptureInterop.CreateItemForMonitor(monitorHandle), captureCursor, frameRate,
            DisplayRefreshService.GetRefreshHz(monitorHandle));

    /// <summary>
    /// True when this session asked Windows to draw the cursor into the captured
    /// frames and Windows accepted. Consumers that composite their own cursor
    /// must skip it, or every frame carries two.
    /// </summary>
    public bool CursorCaptureApplied { get; }

    public (int Width, int Height) ContentSize { get { lock (_stateLock) return (_contentSize.Width, _contentSize.Height); } }
    public string CaptureMode => "Windows Graphics Capture";
    public string? Failure { get { lock (_stateLock) return _failure; } }

    internal static bool CanPublishFrame(int contentWidth, int contentHeight, int surfaceWidth, int surfaceHeight) =>
        contentWidth > 0 && contentHeight > 0 && contentWidth == surfaceWidth && contentHeight == surfaceHeight;

    internal WindowGraphicsCaptureTelemetry GetTelemetrySnapshot()
    {
        lock (_stateLock)
        {
            var signal = _frameSignal.Snapshot;
            return new WindowGraphicsCaptureTelemetry(
                _callbackArrivals,
                _framesDelivered,
                signal.Published,
                signal.Taken,
                signal.Overwritten,
                _sourceTimestampGapCount,
                TimeSpan.FromTicks(_sourceTimestampGapTicks),
                TimeSpan.FromTicks(_sourceTimestampGapMaxTicks),
                TimeSpan.FromTicks(_callbackDurationTicks),
                TimeSpan.FromTicks(_gpuLockWaitTicks),
                _resizeEvents,
                _minimumUpdateInterval);
        }
    }

    /// <summary>
    /// The frame rate the current minimum update interval was derived from, so a
    /// session whose target moves can tell that its interval is now stale.
    /// </summary>
    public int ConfiguredFrameRate { get; private set; }

    /// <summary>The display grid the current interval was derived from.</summary>
    public double DisplayRefreshHz { get { lock (_stateLock) return _displayRefreshHz; } }

    public bool TrySetTargetFrameRate(int frameRate) => TrySetTargetFrameRate(frameRate, null);

    /// <param name="displayRefreshHz">
    /// The grid to derive the interval on, for a window that has moved to
    /// another display or a display whose mode changed. Null keeps the current
    /// one; a non-positive value is ignored rather than trusted, since failing
    /// to read the refresh rate must not throw away a good one.
    /// </param>
    public bool TrySetTargetFrameRate(int frameRate, double? displayRefreshHz)
    {
        WgcMinimumUpdateIntervalResult result;
        double refreshHz;
        lock (_stateLock)
        {
            if (_disposed || _session is null) return false;
            if (displayRefreshHz is { } fresh && double.IsFinite(fresh) && fresh > 0) _displayRefreshHz = fresh;
            refreshHz = _displayRefreshHz;
            result = CaptureInterop.TrySetMinimumUpdateInterval(_session, frameRate, refreshHz);
            _minimumUpdateInterval = result;
            ConfiguredFrameRate = frameRate;
        }

        var requestedMs = result.Requested.TotalMilliseconds;
        var grid = refreshHz > 0 ? $"{refreshHz:0.##}Hz display ({1000d / refreshHz:0.###}ms grid)" : "unknown display refresh";
        // Neither the requested nor the applied value is what governs delivery:
        // WGC releases frames on composition ticks, so the request is rounded up
        // to one. That rounded value is the real ceiling on the source rate, and
        // it is the number worth reading when the source runs under the game.
        var floor = WgcMinimumUpdateIntervalPolicy.DeliveryFloor(result.Applied ?? result.Requested, refreshHz);
        var ceiling = floor > TimeSpan.Zero ? $", floor={floor.TotalMilliseconds:0.###}ms ({1000d / floor.TotalMilliseconds:0.#} FPS ceiling)" : string.Empty;
        if (!result.InterfaceAvailable)
            AppLog.Info($"Native capture: WGC MinUpdateInterval unavailable; requested={requestedMs:0.###}ms for {frameRate} FPS on a {grid}.");
        else if (result.Applied is not null)
            AppLog.Info($"Native capture: WGC MinUpdateInterval requested={requestedMs:0.###}ms, applied={result.Applied.Value.TotalMilliseconds:0.###}ms{ceiling} for {frameRate} FPS on a {grid}.");
        else
            AppLog.Info($"Native capture: WGC MinUpdateInterval request failed; requested={requestedMs:0.###}ms for {frameRate} FPS on a {grid} ({result.Failure}).");
        return result.Applied is not null;
    }

    public bool WaitAndTakeLatestFrame(TimeSpan timeout, CancellationToken cancellationToken, out GameFrameLease? frame)
    {
        frame = null;
        try
        {
            if (!_frameSignal.WaitAndTake(timeout, cancellationToken)) return false;
        }
        catch (ObjectDisposedException)
        {
            return false;
        }

        lock (_stateLock)
        {
            if (_disposed || _latestTexture is null) return false;
            frame = new WgcLease(_latestTexture.QueryInterface<ID3D11Texture2D>(), _lastSourceTimestamp.Ticks, _contentSize.Width, _contentSize.Height);
            return true;
        }
    }

    private void FramePool_FrameArrived(Direct3D11CaptureFramePool sender, object args)
    {
        var callbackTimer = Stopwatch.StartNew();
        Interlocked.Increment(ref _callbackArrivals);
        Direct3D11CaptureFrame? newest = null;
        var burstFrames = 0;
        try
        {
            while (true)
            {
                var candidate = sender.TryGetNextFrame();
                if (candidate is null) break;
                burstFrames++;
                Interlocked.Increment(ref _framesDelivered);
                newest?.Dispose();
                newest = candidate;
            }
            if (newest is null) return;
            var size = newest.ContentSize;
            if (size.Width < 1 || size.Height < 1) { newest.Dispose(); return; }
            var sourceTimestamp = newest.SystemRelativeTime;
            var recreatePool = false;
            var publishFrame = false;
            using (newest)
            using (var sourceTexture = CaptureInterop.GetTexture(newest.Surface))
            {
                var gpuLockTimer = Stopwatch.StartNew();
                lock (_d3dLock)
                {
                    gpuLockTimer.Stop();
                    lock (_stateLock)
                    {
                        Interlocked.Add(ref _gpuLockWaitTicks, gpuLockTimer.Elapsed.Ticks);
                        if (_disposed) return;
                        var sourceDescription = sourceTexture.Description;
                        if (!CanPublishFrame(size.Width, size.Height, (int)sourceDescription.Width, (int)sourceDescription.Height))
                        {
                            // A frame from the old pool can report a new content size.
                            // It cannot be copied into a new-size texture: CopyResource
                            // requires identical dimensions. Recreate, then wait for a
                            // matching frame while retaining the last valid publication.
                            _contentSize = size;
                            recreatePool = true;
                            _resizeEvents++;
                        }
                        else
                        {
                            if (_latestTexture is null || size.Width != _contentSize.Width || size.Height != _contentSize.Height)
                            {
                                _latestTexture?.Dispose();
                                _latestTexture = CreateOwnedTexture(size.Width, size.Height);
                                _contentSize = size;
                                recreatePool = true;
                                _resizeEvents++;
                            }
                            _device.ImmediateContext.CopyResource(_latestTexture!, sourceTexture);
                            if (sourceTimestamp > TimeSpan.Zero && _lastSourceTimestamp > TimeSpan.Zero && sourceTimestamp > _lastSourceTimestamp)
                            {
                                var gap = sourceTimestamp - _lastSourceTimestamp;
                                _sourceTimestampGapTicks += gap.Ticks;
                                _sourceTimestampGapCount++;
                                if (gap.Ticks > _sourceTimestampGapMaxTicks) _sourceTimestampGapMaxTicks = gap.Ticks;
                            }
                            if (sourceTimestamp > _lastSourceTimestamp) _lastSourceTimestamp = sourceTimestamp;
                            publishFrame = true;
                        }
                    }
                }
            }
            if (publishFrame) _frameSignal.Publish();
            if (burstFrames > 1) _frameSignal.RecordOverwritten(burstFrames - 1);
            if (recreatePool)
            {
                lock (_stateLock)
                    if (!_disposed) _framePool?.Recreate(_direct3DDevice, DirectXPixelFormat.B8G8R8A8UIntNormalized, FramePoolBufferCount, _contentSize);
            }
        }
        catch (Exception error)
        {
            lock (_stateLock)
            {
                if (_disposed) return;
                _failure ??= error.Message;
                _frameSignal.Wake();
            }
            AppLog.Error("Native capture: WGC frame callback failed.", error);
        }
        finally
        {
            callbackTimer.Stop();
            Interlocked.Add(ref _callbackDurationTicks, callbackTimer.Elapsed.Ticks);
        }
    }

    private void CaptureItem_Closed(GraphicsCaptureItem sender, object args)
    {
        lock (_stateLock)
        {
            if (_disposed) return;
            _failure ??= "The WGC capture item closed.";
            _frameSignal.Wake();
        }
    }

    private ID3D11Texture2D CreateOwnedTexture(int width, int height) => _device.CreateTexture2D(new Texture2DDescription
    {
        Width = (uint)width, Height = (uint)height, MipLevels = 1, ArraySize = 1,
        Format = Format.B8G8R8A8_UNorm, SampleDescription = new SampleDescription(1, 0),
        Usage = ResourceUsage.Default, BindFlags = BindFlags.None, CPUAccessFlags = CpuAccessFlags.None
    });

    public void Dispose()
    {
        Direct3D11CaptureFramePool? framePool; GraphicsCaptureSession? session; ID3D11Texture2D? latestTexture;
        lock (_stateLock)
        {
            if (_disposed) return;
            _disposed = true;
            framePool = _framePool; session = _session; latestTexture = _latestTexture;
            _framePool = null; _session = null; _latestTexture = null;
        }
        if (framePool is not null) framePool.FrameArrived -= FramePool_FrameArrived;
        _item.Closed -= CaptureItem_Closed;
        session?.Dispose(); framePool?.Dispose();
        _frameSignal.Wake();
        lock (_d3dLock) latestTexture?.Dispose();
        _frameSignal.Dispose();
    }

    private sealed class WgcLease(ID3D11Texture2D texture, long timestamp, int width, int height) : GameFrameLease
    {
        public override ID3D11Texture2D Texture => texture;
        public override long SourceTimestamp => timestamp;
        public override long AccumulatedPresents => 1;
        public override int Width => width;
        public override int Height => height;
        public override long Generation => 0;
        public override bool TextureIsOwnedByCapture => true;
        public override void Dispose() => texture.Dispose();
    }
}

internal readonly record struct WindowGraphicsCaptureTelemetry(
    long CallbackArrivals,
    long FramesDelivered,
    long PublishedFrames,
    long TakenFrames,
    long OverwrittenFrames,
    long SourceTimestampGapCount,
    TimeSpan SourceTimestampGapTotal,
    TimeSpan SourceTimestampGapMaximum,
    TimeSpan CallbackDurationTotal,
    TimeSpan GpuLockWaitTotal,
    long ResizeEvents,
    WgcMinimumUpdateIntervalResult MinimumUpdateInterval);
