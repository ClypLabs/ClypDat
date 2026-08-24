using System.Diagnostics;
using Vortice;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;

namespace ClypDat.App.Services;

// DXGI acquisition owns a separate immediate context. Three keyed shared
// textures cross to processing. Producer never waits; consumer keeps lease
// through crop/scale; newest pending frame wins.
internal sealed class DesktopDuplicationFrameSource : IGameFrameSource, IDisposable
{
    private const int SurfaceCount = 3;
    private readonly ID3D11Device _captureDevice, _processingDevice;
    private readonly IDXGIOutputDuplication _duplication;
    private readonly object _stateLock = new();
    private readonly CancellationTokenSource _stopping = new();
    private readonly LatestFrameSignal _signal = new();
    private readonly SurfaceSlot[] _slots = new SurfaceSlot[SurfaceCount];
    private readonly Thread _producer;
    private int _nextSlot;
    private long _generation, _sequence;
    private string? _failure;
    private bool _disposed;
    private long _sourcePresents, _acquiredFrames, _transportedFrames, _busySlotSkips, _producerCopyTicks, _leaseTicks, _leaseCount, _accumulatedPresents, _zeroPresentFrames;

    private DesktopDuplicationFrameSource(ID3D11Device captureDevice, ID3D11Device processingDevice, IDXGIOutputDuplication duplication)
    {
        _captureDevice = captureDevice; _processingDevice = processingDevice; _duplication = duplication;
        for (var i = 0; i < SurfaceCount; i++) _slots[i] = new SurfaceSlot();
        _producer = new Thread(Produce) { IsBackground = true, Priority = ThreadPriority.AboveNormal, Name = "ClypDat-DXGI-Producer" };
        _producer.Start();
    }

    public static DesktopDuplicationFrameSource Create(ID3D11Device processingDevice, nint targetHandle, ReplayBufferConfig config, out RawRect desktopBounds)
    {
        using var dxgi = processingDevice.QueryInterface<IDXGIDevice>();
        using var adapter = dxgi.GetParent<IDXGIAdapter>();
        var levels = new[] { Vortice.Direct3D.FeatureLevel.Level_11_1, Vortice.Direct3D.FeatureLevel.Level_11_0, Vortice.Direct3D.FeatureLevel.Level_10_1 };
        D3D11.D3D11CreateDevice(adapter, DriverType.Unknown, DeviceCreationFlags.BgraSupport, levels, out var captureDevice, out _, out ID3D11DeviceContext? context).CheckError();
        context?.Dispose();
        try
        {
            var duplication = NativeReplayBuffer.CreateDuplicationFor(captureDevice!, targetHandle, config, out desktopBounds);
            return new DesktopDuplicationFrameSource(captureDevice!, processingDevice, duplication);
        }
        catch { captureDevice?.Dispose(); throw; }
    }

    public string CaptureMode => "Desktop Duplication";
    public string? Failure { get { lock (_stateLock) return _failure; } }

    public bool WaitAndTakeLatestFrame(TimeSpan timeout, CancellationToken cancellationToken, out GameFrameLease? frame)
    {
        frame = null;
        if (!_signal.WaitAndTake(timeout, cancellationToken)) return false;
        SurfaceSlot[] candidates;
        lock (_stateLock) candidates = _slots.Where(x => x.ProcessingTexture is not null && x.Sequence != 0).OrderByDescending(x => x.Sequence).ToArray();
        foreach (var slot in candidates)
        {
            try { slot.ProcessingMutex!.AcquireSync(1, 0); }
            catch { continue; }
            frame = new Lease(this, slot);
            return true;
        }
        return false;
    }

    internal DesktopDuplicationTelemetry GetTelemetrySnapshot()
    {
        var signal = _signal.Snapshot;
        return new(_sourcePresents, _acquiredFrames, _transportedFrames, signal.Taken, signal.Overwritten, _busySlotSkips, _accumulatedPresents, _zeroPresentFrames, TimeSpan.FromTicks(_producerCopyTicks), _leaseCount == 0 ? TimeSpan.Zero : TimeSpan.FromTicks(_leaseTicks / _leaseCount), _failure);
    }

    private void Produce()
    {
        try
        {
            while (!_stopping.IsCancellationRequested)
            {
                var result = _duplication.AcquireNextFrame(100, out var info, out var resource);
                if (result.Code == Vortice.DXGI.ResultCode.WaitTimeout.Code) continue;
                if (!result.Success || resource is null) { resource?.Dispose(); Fail($"AcquireNextFrame failed with 0x{result.Code:X8}."); return; }
                Interlocked.Increment(ref _acquiredFrames);
                try
                {
                    if (info.LastPresentTime == 0) { Interlocked.Increment(ref _zeroPresentFrames); continue; }
                    using var source = resource.QueryInterface<ID3D11Texture2D>();
                    SurfaceSlot? slot = null;
                    lock (_stateLock)
                    {
                        if (_disposed) return;
                        for (var attempt = 0; attempt < SurfaceCount; attempt++)
                        {
                            var candidate = _slots[_nextSlot]; _nextSlot = (_nextSlot + 1) % SurfaceCount;
                            if (candidate.CaptureMutex is null)
                            {
                                EnsureSurface(candidate, source.Description);
                                candidate.CaptureMutex!.AcquireSync(0, 0);
                                slot = candidate;
                                break;
                            }
                            try { candidate.CaptureMutex.AcquireSync(0, 0); }
                            catch { Interlocked.Increment(ref _busySlotSkips); continue; }
                            if (candidate.CaptureTexture!.Description.Width != source.Description.Width || candidate.CaptureTexture.Description.Height != source.Description.Height || candidate.CaptureTexture.Description.Format != source.Description.Format)
                            {
                                candidate.CaptureMutex.ReleaseSync(0);
                                candidate.Dispose();
                                EnsureSurface(candidate, source.Description);
                                candidate.CaptureMutex!.AcquireSync(0, 0);
                            }
                            slot = candidate;
                            break;
                        }
                        if (slot is null) continue;
                    }
                    var copyTimer = Stopwatch.StartNew();
                    _captureDevice.ImmediateContext.CopyResource(slot.CaptureTexture!, source);
                    _captureDevice.ImmediateContext.Flush();
                    slot.CaptureMutex!.ReleaseSync(1);
                    copyTimer.Stop(); Interlocked.Add(ref _producerCopyTicks, copyTimer.Elapsed.Ticks);
                    lock (_stateLock)
                    {
                        slot.Timestamp = info.LastPresentTime; slot.Presents = Math.Max(1, info.AccumulatedFrames); slot.Sequence = ++_sequence;
                        _sourcePresents++; _accumulatedPresents += slot.Presents; _transportedFrames++;
                    }
                    _signal.Publish();
                }
                finally { resource.Dispose(); _duplication.ReleaseFrame(); }
            }
        }
        catch (Exception error) { if (!_stopping.IsCancellationRequested) Fail(error.Message, error); }
    }

    private void EnsureSurface(SurfaceSlot slot, Texture2DDescription description)
    {
        if (slot.CaptureTexture is not null && slot.CaptureTexture.Description.Width == description.Width && slot.CaptureTexture.Description.Height == description.Height && slot.CaptureTexture.Description.Format == description.Format) return;
        slot.Dispose();
        slot.CaptureTexture = _captureDevice.CreateTexture2D(new Texture2DDescription { Width = description.Width, Height = description.Height, MipLevels = 1, ArraySize = 1, Format = description.Format, SampleDescription = description.SampleDescription, Usage = ResourceUsage.Default, BindFlags = BindFlags.None, CPUAccessFlags = CpuAccessFlags.None, MiscFlags = ResourceOptionFlags.SharedKeyedMutex });
        slot.CaptureMutex = slot.CaptureTexture.QueryInterface<IDXGIKeyedMutex>();
        using var shared = slot.CaptureTexture.QueryInterface<IDXGIResource>();
        slot.ProcessingTexture = _processingDevice.OpenSharedResource<ID3D11Texture2D>(shared.SharedHandle);
        slot.ProcessingMutex = slot.ProcessingTexture.QueryInterface<IDXGIKeyedMutex>();
        slot.Generation = ++_generation;
    }

    private void Fail(string failure, Exception? error = null) { lock (_stateLock) { if (_disposed) return; _failure ??= failure; } if (error is null) AppLog.Info($"Native capture: DXGI producer stopped ({failure})."); else AppLog.Error("Native capture: DXGI producer failed.", error); _signal.Wake(); }

    public void Dispose()
    {
        lock (_stateLock) { if (_disposed) return; _disposed = true; }
        _stopping.Cancel(); _signal.Wake(); if (Thread.CurrentThread != _producer) _producer.Join(TimeSpan.FromSeconds(2));
        lock (_stateLock) foreach (var slot in _slots) slot.Dispose();
        _duplication.Dispose(); _captureDevice.Dispose(); _signal.Dispose(); _stopping.Dispose();
    }

    private sealed class SurfaceSlot : IDisposable
    {
        public ID3D11Texture2D? CaptureTexture, ProcessingTexture; public IDXGIKeyedMutex? CaptureMutex, ProcessingMutex; public long Timestamp, Presents, Sequence, Generation;
        public void Dispose() { ProcessingMutex?.Dispose(); ProcessingTexture?.Dispose(); CaptureMutex?.Dispose(); CaptureTexture?.Dispose(); ProcessingMutex = null; ProcessingTexture = null; CaptureMutex = null; CaptureTexture = null; Sequence = 0; }
    }
    private sealed class Lease(DesktopDuplicationFrameSource owner, SurfaceSlot slot) : GameFrameLease
    {
        private readonly long _started = Stopwatch.GetTimestamp(); private int _disposed;
        public override ID3D11Texture2D Texture => slot.ProcessingTexture!; public override long SourceTimestamp => slot.Timestamp; public override long AccumulatedPresents => slot.Presents; public override int Width => (int)slot.ProcessingTexture!.Description.Width; public override int Height => (int)slot.ProcessingTexture!.Description.Height; public override long Generation => slot.Generation;
        public override void Dispose() { if (Interlocked.Exchange(ref _disposed, 1) != 0) return; slot.ProcessingMutex!.ReleaseSync(0); Interlocked.Add(ref owner._leaseTicks, Stopwatch.GetElapsedTime(_started).Ticks); Interlocked.Increment(ref owner._leaseCount); }
    }
}

internal readonly record struct DesktopDuplicationTelemetry(long SourceFrames, long AcquiredFrames, long PublishedFrames, long TakenFrames, long OverwrittenFrames, long BusySlotSkips, long AccumulatedPresents, long ZeroPresentFrames, TimeSpan ProducerCopyTotal, TimeSpan AverageLeaseDuration, string? Failure)
{
    public long TransportedFrames => PublishedFrames;
}
