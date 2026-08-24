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
internal sealed class WindowGraphicsCaptureSource : IDisposable
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
    private long _callbackDurationTicks;
    private long _gpuLockWaitTicks;
    private long _sourceTimestampGapTicks;
    private long _sourceTimestampGapCount;
    private long _sourceTimestampGapMaxTicks;
    private TimeSpan _lastSourceTimestamp;

    private WindowGraphicsCaptureSource(ID3D11Device device, object d3dLock, GraphicsCaptureItem item, bool captureCursor)
    {
        _device = device;
        _d3dLock = d3dLock;
        _item = item;
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
            try { _session.IsCursorCaptureEnabled = captureCursor; }
            catch (Exception error) { AppLog.Info($"Native capture: WGC cursor setting unavailable; using system default ({error.Message})."); }
        }
        _session.StartCapture();
    }

    public static WindowGraphicsCaptureSource Create(ID3D11Device device, object d3dLock, nint windowHandle, bool captureCursor) =>
        new(device, d3dLock, CaptureInterop.CreateItemForWindow(windowHandle), captureCursor);

    public (int Width, int Height) ContentSize { get { lock (_stateLock) return (_contentSize.Width, _contentSize.Height); } }
    public string? Failure { get { lock (_stateLock) return _failure; } }

    internal WindowGraphicsCaptureTelemetry GetTelemetrySnapshot()
    {
        lock (_stateLock)
        {
            var signal = _frameSignal.Snapshot;
            return new WindowGraphicsCaptureTelemetry(
                _callbackArrivals,
                signal.Published,
                signal.Taken,
                signal.Overwritten,
                _sourceTimestampGapCount,
                TimeSpan.FromTicks(_sourceTimestampGapTicks),
                TimeSpan.FromTicks(_sourceTimestampGapMaxTicks),
                TimeSpan.FromTicks(_callbackDurationTicks),
                TimeSpan.FromTicks(_gpuLockWaitTicks));
        }
    }

    internal bool WaitAndTakeLatestTexture(TimeSpan timeout, CancellationToken cancellationToken, out ID3D11Texture2D? texture)
    {
        texture = null;
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
            texture = _latestTexture.QueryInterface<ID3D11Texture2D>();
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
                        if (_latestTexture is null || size.Width != _contentSize.Width || size.Height != _contentSize.Height)
                        {
                            _latestTexture?.Dispose();
                            _latestTexture = CreateOwnedTexture(size.Width, size.Height);
                            _contentSize = size;
                            recreatePool = true;
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
}

internal readonly record struct WindowGraphicsCaptureTelemetry(
    long CallbackArrivals,
    long PublishedFrames,
    long TakenFrames,
    long OverwrittenFrames,
    long SourceTimestampGapCount,
    TimeSpan SourceTimestampGapTotal,
    TimeSpan SourceTimestampGapMaximum,
    TimeSpan CallbackDurationTotal,
    TimeSpan GpuLockWaitTotal);
