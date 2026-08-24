using Vortice;
using Vortice.Direct3D11;
using Vortice.DXGI;

namespace ClypDat.App.Services;

// Desktop Duplication's frame must be released immediately.  Keeping acquire,
// crop and encode on one thread made a 90fps source arrive at only ~42fps when
// encoding was busy.  This producer owns AcquireNextFrame and publishes only
// the newest copied surface; it never builds an accumulating frame queue.
internal sealed class DesktopDuplicationFrameSource : IGameFrameSource, IDisposable
{
    private const int SurfaceCount = 3;
    private readonly ID3D11Device _device;
    private readonly object _d3dLock;
    private readonly IDXGIOutputDuplication _duplication;
    private readonly object _stateLock = new();
    private readonly CancellationTokenSource _stopping = new();
    private readonly LatestFrameSignal _signal = new();
    private readonly ID3D11Texture2D?[] _surfaces = new ID3D11Texture2D?[SurfaceCount];
    private readonly Thread _producer;
    private int _nextSurface;
    private int _latestSurface = -1;
    private string? _failure;
    private bool _disposed;
    private long _sourcePresents;
    private long _publishedFrames;
    private long _accumulatedPresents;
    private long _zeroPresentFrames;

    public DesktopDuplicationFrameSource(ID3D11Device device, object d3dLock, IDXGIOutputDuplication duplication)
    {
        _device = device;
        _d3dLock = d3dLock;
        _duplication = duplication;
        _producer = new Thread(Produce)
        {
            IsBackground = true,
            Priority = ThreadPriority.AboveNormal,
            Name = "ClypDat-DXGI-Producer"
        };
        _producer.Start();
    }

    public string CaptureMode => "Desktop Duplication";
    public string? Failure { get { lock (_stateLock) return _failure; } }

    public bool WaitAndTakeLatestTexture(TimeSpan timeout, CancellationToken cancellationToken, out ID3D11Texture2D? texture)
    {
        texture = null;
        if (!_signal.WaitAndTake(timeout, cancellationToken)) return false;
        lock (_stateLock)
        {
            if (_disposed || _latestSurface < 0 || _surfaces[_latestSurface] is null) return false;
            texture = _surfaces[_latestSurface]!.QueryInterface<ID3D11Texture2D>();
            return true;
        }
    }

    internal DesktopDuplicationTelemetry GetTelemetrySnapshot()
    {
        lock (_stateLock)
        {
            var signal = _signal.Snapshot;
            return new DesktopDuplicationTelemetry(
                _sourcePresents, _publishedFrames, signal.Taken, signal.Overwritten,
                _accumulatedPresents, _zeroPresentFrames, _failure);
        }
    }

    private void Produce()
    {
        try
        {
            while (!_stopping.IsCancellationRequested)
            {
                var result = _duplication.AcquireNextFrame(100, out var info, out var resource);
                if (result.Code == Vortice.DXGI.ResultCode.WaitTimeout.Code) continue;
                if (!result.Success || resource is null)
                {
                    resource?.Dispose();
                    Fail($"AcquireNextFrame failed with 0x{result.Code:X8}.");
                    return;
                }

                try
                {
                    if (info.LastPresentTime == 0)
                    {
                        Interlocked.Increment(ref _zeroPresentFrames);
                        continue;
                    }

                    using var source = resource.QueryInterface<ID3D11Texture2D>();
                    lock (_d3dLock)
                    lock (_stateLock)
                    {
                        if (_disposed) return;
                        var index = _nextSurface;
                        _nextSurface = (_nextSurface + 1) % SurfaceCount;
                        var description = source.Description;
                        if (_surfaces[index] is null ||
                            _surfaces[index]!.Description.Width != description.Width ||
                            _surfaces[index]!.Description.Height != description.Height ||
                            _surfaces[index]!.Description.Format != description.Format)
                        {
                            _surfaces[index]?.Dispose();
                            _surfaces[index] = _device.CreateTexture2D(new Texture2DDescription
                            {
                                Width = description.Width,
                                Height = description.Height,
                                MipLevels = 1,
                                ArraySize = 1,
                                Format = description.Format,
                                SampleDescription = description.SampleDescription,
                                Usage = ResourceUsage.Default,
                                BindFlags = BindFlags.None,
                                CPUAccessFlags = CpuAccessFlags.None
                            });
                        }
                        _device.ImmediateContext.CopyResource(_surfaces[index]!, source);
                        _latestSurface = index;
                        _sourcePresents++;
                        _accumulatedPresents += Math.Max(1, info.AccumulatedFrames);
                        _publishedFrames++;
                    }
                    _signal.Publish();
                }
                finally
                {
                    resource.Dispose();
                    _duplication.ReleaseFrame();
                }
            }
        }
        catch (Exception error)
        {
            if (!_stopping.IsCancellationRequested) Fail(error.Message, error);
        }
    }

    private void Fail(string failure, Exception? error = null)
    {
        lock (_stateLock)
        {
            if (_disposed) return;
            _failure ??= failure;
        }
        if (error is not null) AppLog.Error("Native capture: DXGI producer failed.", error);
        else AppLog.Info($"Native capture: DXGI producer stopped ({failure}).");
        _signal.Wake();
    }

    public void Dispose()
    {
        lock (_stateLock)
        {
            if (_disposed) return;
            _disposed = true;
        }
        _stopping.Cancel();
        _signal.Wake();
        if (Thread.CurrentThread != _producer) _producer.Join(TimeSpan.FromSeconds(2));
        lock (_d3dLock)
        {
            foreach (var surface in _surfaces) surface?.Dispose();
            _duplication.Dispose();
        }
        _signal.Dispose();
        _stopping.Dispose();
    }
}

internal readonly record struct DesktopDuplicationTelemetry(
    long SourceFrames,
    long PublishedFrames,
    long TakenFrames,
    long OverwrittenFrames,
    long AccumulatedPresents,
    long ZeroPresentFrames,
    string? Failure);
