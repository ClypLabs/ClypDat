using System.Diagnostics;
using System.Runtime.InteropServices;
using Vortice;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;

namespace ClypDat.App.Services;

// Game capture owns a second DXGI device so acquisition cannot be blocked by
// conversion/encoding. Three shared surfaces cross to the processing device.
// Shared fences express GPU ownership; CPU code never Flushes or waits.
internal sealed class DesktopDuplicationFrameSource : IGameFrameSource, IDisposable
{
    // Ring depth is latency tolerance, not throughput: a slot stays
    // unavailable until the PROCESSING device's release fence clears it, and
    // that fence sits behind crop, GPU downscale and the encoder readback. With
    // a game in the foreground that queue runs 100ms+ deep, so three slots left
    // the producer finding every one busy and throwing the frame away - measured
    // on a 4K90 recording, 84 of every 129 acquired frames died at this gate,
    // which is what leaves ~20 real frames a second inside a 90fps file and pads
    // the rest with duplicates. Each extra slot buys another frame interval
    // before that happens.
    private const int MinimumSurfaceCount = 3;
    private const int MaximumSurfaceCount = 8;
    // Sized from the source frame, so a 4K desktop (33MB a surface) gets 5 and a
    // 1080p one gets the full 8 rather than the same VRAM bill at every
    // resolution.
    private const long SurfaceBudgetBytes = 192L * 1024 * 1024;
    private readonly ID3D11Device _captureDevice, _processingDevice;
    private readonly ID3D11Device5 _captureDevice5, _processingDevice5;
    private readonly ID3D11DeviceContext4 _captureContext, _processingContext;
    private readonly ID3D11Fence _readyFence, _releasedFence, _readyFenceOnProcessing, _releasedFenceOnCapture;
    private readonly IDXGIOutputDuplication _duplication;
    private readonly object _stateLock = new();
    private readonly CancellationTokenSource _stopping = new();
    private readonly LatestFrameSignal _signal = new();
    // Allocated at the maximum and used up to _activeSlots: a SurfaceSlot holds
    // no GPU memory until EnsureSurface gives it a texture, so the unused tail
    // costs nothing.
    private readonly SurfaceSlot[] _slots = new SurfaceSlot[MaximumSurfaceCount];
    private int _activeSlots = MinimumSurfaceCount;
    private readonly Thread _producer;
    // Sampling belongs on the acquisition device.  Keeping it downstream
    // meant every desktop-sized BGRA frame crossed devices before we decided
    // it was too old for the selected output cadence.
    private readonly PresentSamplingBudget _transportSamplingBudget;
    private readonly bool _captureCursor;
    public int? AppliedGpuPriority { get; }
    private readonly Stopwatch _producerClock = Stopwatch.StartNew();
    private int _nextSlot;
    private long _generation, _sequence;
    private ulong _readyValue, _releasedValue;
    private string? _failure;
    private bool _disposed;
    private long _sourcePresents, _acquiredFrames, _transportedFrames, _busySlotSkips, _producerCopyTicks, _leaseTicks, _leaseCount, _accumulatedPresents, _zeroPresentFrames, _pointerUpdates, _transportedPointerFrames;
    // _busySlotSkips counts slot EXAMINATIONS (up to one per slot per frame), so
    // it cannot be read as a frame count. _allBusyDrops is the frame that was
    // actually thrown away, and _releaseLagFrames is how far the processing
    // device's fence was behind when that happened - a lag larger than the ring
    // says the queue, not the ring depth, is the remaining wall.
    private long _allBusyDrops, _releaseLagFrames, _acquireTicks;

    private DesktopDuplicationFrameSource(ID3D11Device captureDevice, ID3D11Device processingDevice, IDXGIOutputDuplication duplication, int frameRate, bool captureCursor, int? appliedGpuPriority)
    {
        _captureDevice = captureDevice; _processingDevice = processingDevice; _duplication = duplication;
        _captureCursor = captureCursor;
        AppliedGpuPriority = appliedGpuPriority;
        _captureDevice5 = captureDevice.QueryInterface<ID3D11Device5>();
        _processingDevice5 = processingDevice.QueryInterface<ID3D11Device5>();
        _captureContext = captureDevice.ImmediateContext.QueryInterface<ID3D11DeviceContext4>();
        _processingContext = processingDevice.ImmediateContext.QueryInterface<ID3D11DeviceContext4>();
        _readyFence = _captureDevice5.CreateFence<ID3D11Fence>(0, FenceFlags.Shared);
        _releasedFence = _processingDevice5.CreateFence<ID3D11Fence>(0, FenceFlags.Shared);
        var readyHandle = _readyFence.CreateSharedHandle(null, null!);
        var releasedHandle = _releasedFence.CreateSharedHandle(null, null!);
        try
        {
            _readyFenceOnProcessing = _processingDevice5.OpenSharedFence<ID3D11Fence>(readyHandle);
            _releasedFenceOnCapture = _captureDevice5.OpenSharedFence<ID3D11Fence>(releasedHandle);
        }
        finally { CloseHandle(readyHandle); CloseHandle(releasedHandle); }
        for (var i = 0; i < _slots.Length; i++) _slots[i] = new SurfaceSlot();
        _transportSamplingBudget = new PresentSamplingBudget(frameRate);
        _producer = new Thread(Produce) { IsBackground = true, Priority = ThreadPriority.AboveNormal, Name = "ClypDat-GameCapture-Producer" };
        _producer.Start();
    }

    public static DesktopDuplicationFrameSource Create(ID3D11Device processingDevice, nint targetHandle, ReplayBufferConfig config, out RawRect desktopBounds)
    {
        using var dxgi = processingDevice.QueryInterface<IDXGIDevice>();
        using var adapter = dxgi.GetParent<IDXGIAdapter>();
        var levels = new[] { FeatureLevel.Level_11_1, FeatureLevel.Level_11_0, FeatureLevel.Level_10_1 };
        D3D11.D3D11CreateDevice(adapter, DriverType.Unknown, DeviceCreationFlags.BgraSupport, levels, out var captureDevice, out _, out ID3D11DeviceContext? context).CheckError();
        context?.Dispose();
        var appliedGpuPriority = GpuScheduling.TryRaiseDeviceGpuPriority(captureDevice!.NativePointer, "DXGI acquisition");
        try { return new DesktopDuplicationFrameSource(captureDevice!, processingDevice, NativeReplayBuffer.CreateDuplicationFor(captureDevice!, targetHandle, config, out desktopBounds), config.FrameRate, config.CaptureCursor, appliedGpuPriority); }
        catch { captureDevice?.Dispose(); throw; }
    }

    public string CaptureMode => "Game Capture (DXGI shared fences)";
    public string? Failure { get { lock (_stateLock) return _failure; } }

    public bool WaitAndTakeLatestFrame(TimeSpan timeout, CancellationToken cancellationToken, out GameFrameLease? frame)
    {
        frame = null;
        if (!_signal.WaitAndTake(timeout, cancellationToken)) return false;
        lock (_stateLock)
        {
            var slot = _slots.Where(s => s.ProcessingTexture is not null && !s.Leased && s.Sequence != 0).OrderByDescending(s => s.Sequence).FirstOrDefault();
            if (slot is null) return false;
            slot.Leased = true;
            // Queue the dependency. The consumer CPU can immediately acquire
            // another frame; its conversion work will wait only on the GPU.
            _processingContext.Wait(_readyFenceOnProcessing, slot.ReadyValue);
            frame = new Lease(this, slot);
            return true;
        }
    }

    internal DesktopDuplicationTelemetry GetTelemetrySnapshot()
    {
        var signal = _signal.Snapshot;
        return new(_sourcePresents, _acquiredFrames, _transportedFrames, signal.Taken, signal.Overwritten, _busySlotSkips, _accumulatedPresents, _zeroPresentFrames, _pointerUpdates, _transportedPointerFrames, TimeSpan.FromTicks(_producerCopyTicks), _leaseCount == 0 ? TimeSpan.Zero : TimeSpan.FromTicks(_leaseTicks / _leaseCount), _failure, _allBusyDrops, _releaseLagFrames, Volatile.Read(ref _activeSlots), TimeSpan.FromTicks(_acquireTicks));
    }

    private void Produce()
    {
        try
        {
            var pendingContentUpdate = false;
            var pendingContentTimestamp = 0L;
            var pendingPresents = 0L;
            var pendingPointerUpdate = false;
            var pendingPointerTimestamp = 0L;
            while (!_stopping.IsCancellationRequested)
            {
                var acquireStarted = Stopwatch.GetTimestamp();
                var result = _duplication.AcquireNextFrame(100, out var info, out var resource);
                if (result.Code == Vortice.DXGI.ResultCode.WaitTimeout.Code) continue;
                if (!result.Success || resource is null) { resource?.Dispose(); Fail($"AcquireNextFrame failed with 0x{result.Code:X8}."); return; }
                // Timeouts are excluded above on purpose: this measures what a
                // productive acquire costs, so the gap between the desktop's
                // present rate and this loop's rate is attributable rather than
                // inferred.
                Interlocked.Add(ref _acquireTicks, Stopwatch.GetElapsedTime(acquireStarted).Ticks);
                Interlocked.Increment(ref _acquiredFrames);
                try
                {
                    var hasContentUpdate = info.LastPresentTime != 0;
                    var hasPointerUpdate = _captureCursor && info.LastMouseUpdateTime != 0;
                    if (!hasContentUpdate && !hasPointerUpdate) { Interlocked.Increment(ref _zeroPresentFrames); continue; }
                    if (hasContentUpdate)
                    {
                        pendingContentUpdate = true;
                        pendingContentTimestamp = info.LastPresentTime;
                        pendingPresents += Math.Max(1, info.AccumulatedFrames);
                    }
                    if (hasPointerUpdate)
                    {
                        pendingPointerUpdate = true;
                        pendingPointerTimestamp = info.LastMouseUpdateTime;
                        Interlocked.Increment(ref _pointerUpdates);
                    }
                    // A source at or below the configured rate keeps a full
                    // credit between presents and is transported intact.  A
                    // faster source keeps only the newest present at the
                    // selected cadence, before any shared-resource copy.
                    if (!_transportSamplingBudget.TryConsume(_producerClock.Elapsed, pendingSample: true)) continue;
                    using var source = resource.QueryInterface<ID3D11Texture2D>();
                    SurfaceSlot? slot = null;
                    lock (_stateLock)
                    {
                        if (_disposed) return;
                        EnsureSlotCount(source.Description);
                        for (var i = 0; i < _activeSlots; i++)
                        {
                            var candidate = _slots[_nextSlot]; _nextSlot = (_nextSlot + 1) % _activeSlots;
                            if (candidate.Leased || _releasedFenceOnCapture.CompletedValue < candidate.ReleaseValue) { Interlocked.Increment(ref _busySlotSkips); continue; }
                            EnsureSurface(candidate, source.Description);
                            slot = candidate;
                            break;
                        }
                        if (slot is null)
                        {
                            _allBusyDrops++;
                            _releaseLagFrames = (long)(_releasedValue - _releasedFenceOnCapture.CompletedValue);
                            continue;
                        }
                    }
                    lock (_stateLock) { if (_disposed) return; }
                    var timer = Stopwatch.StartNew();
                    _captureDevice.ImmediateContext.CopyResource(slot.CaptureTexture!, source);
                    slot.ReadyValue = unchecked(++_readyValue);
                    _captureContext.Signal(_readyFence, slot.ReadyValue);
                    timer.Stop(); Interlocked.Add(ref _producerCopyTicks, timer.Elapsed.Ticks);
                    lock (_stateLock)
                    {
                        slot.Timestamp = Math.Max(pendingContentTimestamp, pendingPointerTimestamp);
                        slot.ContentTimestamp = pendingContentTimestamp;
                        slot.Presents = pendingContentUpdate ? Math.Max(1, pendingPresents) : 0;
                        slot.HasDesktopContentUpdate = pendingContentUpdate;
                        slot.HasPointerUpdate = pendingPointerUpdate;
                        slot.Sequence = ++_sequence;
                        if (slot.HasDesktopContentUpdate) _sourcePresents++;
                        _accumulatedPresents += slot.Presents;
                        _transportedFrames++;
                        if (slot.HasPointerUpdate) _transportedPointerFrames++;
                    }
                    pendingContentUpdate = false;
                    pendingContentTimestamp = 0;
                    pendingPresents = 0;
                    pendingPointerUpdate = false;
                    pendingPointerTimestamp = 0;
                    _signal.Publish();
                }
                finally { resource.Dispose(); _duplication.ReleaseFrame(); }
            }
        }
        catch (Exception error) { if (!_stopping.IsCancellationRequested) Fail(error.Message, error); }
    }

    // Called with _stateLock held, on every transported frame - a no-op after
    // the first, and after a resolution change until the new size settles.
    private void EnsureSlotCount(Texture2DDescription description)
    {
        var resolved = ResolveSlotCount(description);
        if (resolved == _activeSlots) return;
        // Shrinking releases the surfaces past the new end, but never one the
        // consumer is still holding - that texture is live GPU memory it is
        // reading from.
        for (var i = resolved; i < _slots.Length; i++)
        {
            if (!_slots[i].Leased) _slots[i].Dispose();
        }
        Volatile.Write(ref _activeSlots, resolved);
        if (_nextSlot >= resolved) _nextSlot = 0;
        AppLog.Info($"Native capture: game capture ring sized to {resolved} shared {description.Width}x{description.Height} surfaces.");
    }

    private static int ResolveSlotCount(Texture2DDescription description)
    {
        var frameBytes = (long)description.Width * description.Height * BytesPerPixel(description.Format);
        if (frameBytes <= 0) return MinimumSurfaceCount;
        return (int)Math.Clamp(SurfaceBudgetBytes / frameBytes, MinimumSurfaceCount, MaximumSurfaceCount);
    }

    // Desktop Duplication hands out BGRA8 for an SDR desktop and a 10-bit or
    // half-float format for an HDR one; anything unrecognised is assumed to be
    // 32-bit, which only costs a slot or two of budget if it is wider.
    private static int BytesPerPixel(Format format) => format switch
    {
        Format.R16G16B16A16_Float or Format.R16G16B16A16_UNorm => 8,
        _ => 4
    };

    private void EnsureSurface(SurfaceSlot slot, Texture2DDescription description)
    {
        if (slot.CaptureTexture is not null && slot.CaptureTexture.Description.Width == description.Width && slot.CaptureTexture.Description.Height == description.Height && slot.CaptureTexture.Description.Format == description.Format) return;
        slot.Dispose();
        slot.CaptureTexture = _captureDevice.CreateTexture2D(new Texture2DDescription { Width = description.Width, Height = description.Height, MipLevels = 1, ArraySize = 1, Format = description.Format, SampleDescription = description.SampleDescription, Usage = ResourceUsage.Default, BindFlags = BindFlags.None, CPUAccessFlags = CpuAccessFlags.None, MiscFlags = ResourceOptionFlags.Shared });
        using var shared = slot.CaptureTexture.QueryInterface<IDXGIResource>();
        slot.ProcessingTexture = _processingDevice.OpenSharedResource<ID3D11Texture2D>(shared.SharedHandle);
        slot.Generation = ++_generation;
    }

    private void Release(SurfaceSlot slot)
    {
        lock (_stateLock)
        {
            if (_disposed) return;
            slot.ReleaseValue = unchecked(++_releasedValue);
            slot.Leased = false;
            _processingContext.Signal(_releasedFence, slot.ReleaseValue);
        }
    }

    private void Fail(string failure, Exception? error = null) { lock (_stateLock) { if (_disposed) return; _failure ??= failure; } if (error is null) AppLog.Info($"Native capture: Game Capture producer stopped ({failure})."); else AppLog.Error("Native capture: Game Capture producer failed.", error); _signal.Wake(); }

    public void Dispose()
    {
        lock (_stateLock) { if (_disposed) return; _disposed = true; }
        _stopping.Cancel(); _signal.Wake();

        // The producer's GPU work is deliberately outside _stateLock, and neither
        // AcquireNextFrame nor ReleaseFrame is _disposed-guarded, so disposing the
        // slots while it is mid-CopyResource releases the destination texture out from
        // under the copy. The old bound was 2s, which a 4K BGRA CopyResource under GPU
        // contention can exceed. AcquireNextFrame's own timeout is 100ms, so a healthy
        // producer notices cancellation almost immediately and this returns at once;
        // 10s only matters when the GPU is genuinely stuck.
        var producerStopped = true;
        if (Thread.CurrentThread != _producer) producerStopped = _producer.Join(TimeSpan.FromSeconds(10));

        if (!producerStopped)
        {
            // Leak rather than free-and-use. These are GPU resources held by a thread
            // still writing into them; the process is ending this capture session, and
            // a leaked texture is recoverable where a use-after-free is not.
            AppLog.Error("Native capture: Game Capture producer did not stop within 10s; leaving its GPU resources alive rather than releasing them underneath it.");
            return;
        }

        lock (_stateLock) foreach (var slot in _slots) slot.Dispose();
        _duplication.Dispose(); _readyFenceOnProcessing.Dispose(); _releasedFenceOnCapture.Dispose(); _readyFence.Dispose(); _releasedFence.Dispose(); _processingContext.Dispose(); _captureContext.Dispose(); _processingDevice5.Dispose(); _captureDevice5.Dispose(); _captureDevice.Dispose(); _signal.Dispose(); _stopping.Dispose();
    }

    private sealed class SurfaceSlot : IDisposable
    {
        public ID3D11Texture2D? CaptureTexture, ProcessingTexture; public long Timestamp, ContentTimestamp, Presents, Sequence, Generation; public ulong ReadyValue, ReleaseValue; public bool Leased, HasDesktopContentUpdate, HasPointerUpdate;
        public void Dispose() { ProcessingTexture?.Dispose(); CaptureTexture?.Dispose(); ProcessingTexture = null; CaptureTexture = null; Timestamp = ContentTimestamp = Presents = Sequence = 0; ReadyValue = ReleaseValue = 0; Leased = HasDesktopContentUpdate = HasPointerUpdate = false; }
    }
    private sealed class Lease(DesktopDuplicationFrameSource owner, SurfaceSlot slot) : GameFrameLease
    {
        private readonly long _started = Stopwatch.GetTimestamp(); private int _disposed;
        public override ID3D11Texture2D Texture => slot.ProcessingTexture!; public override long SourceTimestamp => slot.Timestamp; public override long AccumulatedPresents => slot.Presents; public override int Width => (int)slot.ProcessingTexture!.Description.Width; public override int Height => (int)slot.ProcessingTexture!.Description.Height; public override long Generation => slot.Generation; public override bool HasDesktopContentUpdate => slot.HasDesktopContentUpdate; public override bool HasPointerUpdate => slot.HasPointerUpdate; public override long ContentTimestamp => slot.ContentTimestamp;
        public override void Dispose() { if (Interlocked.Exchange(ref _disposed, 1) != 0) return; owner.Release(slot); Interlocked.Add(ref owner._leaseTicks, Stopwatch.GetElapsedTime(_started).Ticks); Interlocked.Increment(ref owner._leaseCount); }
    }

    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr handle);
}

internal readonly record struct DesktopDuplicationTelemetry(long SourceFrames, long AcquiredFrames, long PublishedFrames, long TakenFrames, long OverwrittenFrames, long BusySlotSkips, long AccumulatedPresents, long ZeroPresentFrames, long PointerUpdates, long TransportedPointerFrames, TimeSpan ProducerCopyTotal, TimeSpan AverageLeaseDuration, string? Failure, long AllBusyDrops, long ReleaseLagFrames, int SlotCount, TimeSpan AcquireTotal)
{ public long TransportedFrames => PublishedFrames; }
