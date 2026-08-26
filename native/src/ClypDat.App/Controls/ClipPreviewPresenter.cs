using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Rendering.Composition;
using Avalonia.Threading;
using ClypDat.App.Services;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;
using D3D11Api = Vortice.Direct3D11.D3D11;
using DxgiFormat = Vortice.DXGI.Format;

namespace ClypDat.App.Controls;

// The card thumbnail remains a normal Avalonia Image. This control owns only
// the moving preview layer, so presenting a frame never changes card view-model
// state or causes the thumbnail to be re-decoded.
internal interface IClipPreviewPresenter : IAsyncDisposable
{
    PreviewPresentationPath Path { get; }
    ValueTask ActivateSessionAsync(CancellationToken cancellationToken);
    ValueTask SetAttachedAsync(bool attached);
    ValueTask ReleaseResourcesAsync();
    ValueTask<PreviewPresentResult> PresentAsync(ReadOnlyMemory<byte> rgba, PixelSize size, CancellationToken cancellationToken);
}

internal enum PreviewPresentationPath { Gpu, Software }

internal readonly record struct PreviewPresentResult(PreviewPresentationPath Path, TimeSpan Latency);

public sealed class ClipPreviewPresenter : Control, IClipPreviewPresenter
{
    // Process-wide, not per-card: every card builds its own presenter, so a
    // machine whose composition device keeps dying would otherwise re-arm the
    // GPU path on the next hover, lose it again, and spend the session doing
    // that. Two losses is enough to call it.
    private const int GpuLossesBeforeGivingUp = 2;
    private static int _gpuLosses;

    private readonly SoftwareClipPreviewAdapter _software;
    private readonly SemaphoreSlim _lifecycleLock = new(1, 1);
    private IClipPreviewPresenter? _adapter;
    private bool _requestedAttached;
    private bool _disposed;

    public ClipPreviewPresenter()
    {
        _software = new SoftwareClipPreviewAdapter(this);
        _adapter = _software;
    }

    PreviewPresentationPath IClipPreviewPresenter.Path => _adapter?.Path ?? PreviewPresentationPath.Software;

    protected override void OnDetachedFromVisualTree(Avalonia.VisualTreeAttachmentEventArgs e)
    {
        _ = ((IClipPreviewPresenter)this).ReleaseResourcesAsync().AsTask();
        base.OnDetachedFromVisualTree(e);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        if (change.Property == BoundsProperty)
            _ = ResizeGpuAsync(Bounds.Size);
        base.OnPropertyChanged(change);
    }

    public override void Render(DrawingContext context)
    {
        if (_requestedAttached && _adapter == _software && _software.Bitmap is { } bitmap)
            context.DrawImage(bitmap, Bounds);
    }

    async ValueTask IClipPreviewPresenter.ActivateSessionAsync(CancellationToken cancellationToken)
    {
        await _lifecycleLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_disposed) throw new ObjectDisposedException(nameof(ClipPreviewPresenter));
            if (_adapter is not SoftwareClipPreviewAdapter) return;
            await InitializeGpuAsync(cancellationToken).ConfigureAwait(true);
        }
        finally { _lifecycleLock.Release(); }
    }

    async ValueTask IClipPreviewPresenter.SetAttachedAsync(bool attached)
    {
        await _lifecycleLock.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_disposed) return;
            _requestedAttached = attached;
            if (_adapter is { } adapter) await adapter.SetAttachedAsync(attached).ConfigureAwait(false);
            await Dispatcher.UIThread.InvokeAsync(InvalidateVisual, DispatcherPriority.Render);
        }
        finally { _lifecycleLock.Release(); }
    }

    async ValueTask<PreviewPresentResult> IClipPreviewPresenter.PresentAsync(ReadOnlyMemory<byte> rgba, PixelSize size, CancellationToken cancellationToken)
    {
        await _lifecycleLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_disposed) throw new ObjectDisposedException(nameof(ClipPreviewPresenter));
            var adapter = _adapter ?? _software;
            try
            {
                return await adapter.PresentAsync(rgba, size, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception error) when (adapter is GpuClipPreviewAdapter && !cancellationToken.IsCancellationRequested)
            {
                // Device loss/import failures must not discard this frame. Switch
                // immediately, then render the same decoded bytes through software.
                var losses = Interlocked.Increment(ref _gpuLosses);
                AppLog.Info($"Clip hover preview GPU path lost ({losses}); switching to software: {error.Message}");
                if (losses == GpuLossesBeforeGivingUp)
                    AppLog.Info("Clip hover preview: staying on the software path for the rest of this session.");
                _adapter = _software;
                await _software.SetAttachedAsync(_requestedAttached).ConfigureAwait(false);
                await adapter.ReleaseResourcesAsync().ConfigureAwait(false);
                return await _software.PresentAsync(rgba, size, cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            _lifecycleLock.Release();
        }
    }

    private async Task InitializeGpuAsync(CancellationToken cancellationToken)
    {
        if (Volatile.Read(ref _gpuLosses) >= GpuLossesBeforeGivingUp) return;
        try
        {
            var element = ElementComposition.GetElementVisual(this);
            if (element is null) return;
            cancellationToken.ThrowIfCancellationRequested();
            var interop = await element.Compositor.TryGetCompositionGpuInterop();
            cancellationToken.ThrowIfCancellationRequested();
            if (interop is null || interop.IsLost || interop.DeviceLuid is not { Length: 8 }
                || !interop.SupportedImageHandleTypes.Contains(KnownPlatformGraphicsExternalImageHandleTypes.D3D11TextureGlobalSharedHandle)
                || !interop.GetSynchronizationCapabilities(KnownPlatformGraphicsExternalImageHandleTypes.D3D11TextureGlobalSharedHandle)
                    .HasFlag(CompositionGpuImportedImageSynchronizationCapabilities.KeyedMutex))
                return;

            var gpu = GpuClipPreviewAdapter.TryCreate(this, element.Compositor, interop);
            if (gpu is null) return;
            _adapter = gpu;
            await gpu.SetAttachedAsync(_requestedAttached).ConfigureAwait(false);
            InvalidateVisual();
            AppLog.Debug("Clip hover preview GPU resources created.");
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception error)
        {
            AppLog.Debug($"Clip hover preview presenter: software path selected ({error.Message}).");
        }
    }

    private async Task ResizeGpuAsync(Size size)
    {
        await _lifecycleLock.WaitAsync().ConfigureAwait(false);
        try
        {
            if (!_disposed && _adapter is GpuClipPreviewAdapter gpu) gpu.Resize(size);
        }
        finally { _lifecycleLock.Release(); }
    }

    async ValueTask IClipPreviewPresenter.ReleaseResourcesAsync()
    {
        await _lifecycleLock.WaitAsync().ConfigureAwait(false);
        try { await ReleaseResourcesCoreAsync().ConfigureAwait(false); }
        finally { _lifecycleLock.Release(); }
    }

    private async ValueTask ReleaseResourcesCoreAsync()
    {
        _requestedAttached = false;
        if (_adapter is { } activeAdapter) await activeAdapter.SetAttachedAsync(false).ConfigureAwait(false);
        var adapter = _adapter;
        _adapter = _software;
        if (adapter is not null && adapter != _software)
        {
            await adapter.ReleaseResourcesAsync().ConfigureAwait(false);
            AppLog.Debug("Clip hover preview GPU resources released.");
        }
        await _software.ReleaseResourcesAsync().ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        await _lifecycleLock.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_disposed) return;
            _disposed = true;
            await ReleaseResourcesCoreAsync().ConfigureAwait(false);
        }
        finally
        {
            _lifecycleLock.Release();
        }
    }
}

internal sealed class SoftwareClipPreviewAdapter(ClipPreviewPresenter owner) : IClipPreviewPresenter
{
    private WriteableBitmap? _bitmap;
    private bool _attached;

    public WriteableBitmap? Bitmap => _bitmap;
    public PreviewPresentationPath Path => PreviewPresentationPath.Software;

    public ValueTask ActivateSessionAsync(CancellationToken cancellationToken) => ValueTask.CompletedTask;

    public ValueTask SetAttachedAsync(bool attached)
    {
        _attached = attached;
        return ValueTask.CompletedTask;
    }

    public async ValueTask<PreviewPresentResult> PresentAsync(ReadOnlyMemory<byte> rgba, PixelSize size, CancellationToken cancellationToken)
    {
        // The copies below read size.Width * size.Height * 4 bytes from rgba with no
        // reference to its actual length. It is correct today - the only producer
        // allocates exactly that, fills every slot, passes the same size, and pins
        // ffmpeg to scale+pad with -pix_fmt rgba - but this is the path that consumes
        // decoder output from clips the user may have imported, so it should not depend
        // on a caller invariant to stay in bounds.
        var requiredBytes = (long)size.Width * size.Height * 4;
        if (size.Width <= 0 || size.Height <= 0 || rgba.Length < requiredBytes)
        {
            throw new ArgumentException(
                $"Preview buffer is {rgba.Length} bytes; {size.Width}x{size.Height} RGBA needs {requiredBytes}.",
                nameof(rgba));
        }

        var started = Stopwatch.GetTimestamp();
        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!_attached) return;
            if (_bitmap is null || _bitmap.PixelSize != size)
            {
                var old = _bitmap;
                _bitmap = new WriteableBitmap(size, new Vector(96, 96), PixelFormat.Rgba8888, AlphaFormat.Unpremul);
                Services.DeferredBitmapDisposal.Release(old);
            }
            using var locked = _bitmap.Lock();
            unsafe
            {
                fixed (byte* source = rgba.Span)
                {
                    var rowBytes = size.Width * 4;
                    for (var row = 0; row < size.Height; row++)
                        Buffer.MemoryCopy(source + row * rowBytes, (byte*)locked.Address + row * locked.RowBytes, locked.RowBytes, rowBytes);
                }
            }
            owner.InvalidateVisual();
        }, DispatcherPriority.Render);
        return new PreviewPresentResult(Path, Stopwatch.GetElapsedTime(started));
    }

    public ValueTask ReleaseResourcesAsync()
    {
        var bitmap = Interlocked.Exchange(ref _bitmap, null);
        Services.DeferredBitmapDisposal.Release(bitmap);
        return ValueTask.CompletedTask;
    }

    public ValueTask DisposeAsync() => ReleaseResourcesAsync();
}

internal sealed class GpuClipPreviewAdapter : IClipPreviewPresenter
{
    private readonly ClipPreviewPresenter _owner;
    private readonly CompositionDrawingSurface _surface;
    private readonly CompositionSurfaceVisual _visual;
    private readonly ID3D11Device _device;
    private readonly ID3D11DeviceContext _context;
    private readonly ICompositionGpuInterop _interop;
    private readonly List<TextureSlot> _slots = [];
    private int _nextSlot;
    private int _importFailed;
    private bool _attached;
    private bool _disposed;

    private const int MutexWaitMilliseconds = 250;

    private GpuClipPreviewAdapter(ClipPreviewPresenter owner, Compositor compositor, ICompositionGpuInterop interop,
        ID3D11Device device, ID3D11DeviceContext context)
    {
        _owner = owner;
        _interop = interop;
        _device = device;
        _context = context;
        _surface = compositor.CreateDrawingSurface();
        _visual = compositor.CreateSurfaceVisual();
        _visual.Surface = _surface;
        _visual.Size = ToVector(owner.Bounds.Size);
        ElementComposition.SetElementChildVisual(owner, _visual);
    }

    public PreviewPresentationPath Path => PreviewPresentationPath.Gpu;

    public static GpuClipPreviewAdapter? TryCreate(ClipPreviewPresenter owner, Compositor compositor, ICompositionGpuInterop interop)
    {
        IDXGIAdapter1? adapter = null;
        ID3D11Device? device = null;
        ID3D11DeviceContext? context = null;
        try
        {
            using var factory = DXGI.CreateDXGIFactory1<IDXGIFactory1>();
            for (uint index = 0; ; index++)
            {
                if (factory.EnumAdapters1(index, out adapter).Failure) break;
                if (MatchesLuid(adapter.Description1.Luid, interop.DeviceLuid!)) break;
                adapter.Dispose();
                adapter = null;
            }
            if (adapter is null) return null;
            var levels = new[] { FeatureLevel.Level_11_1, FeatureLevel.Level_11_0, FeatureLevel.Level_10_1 };
            D3D11Api.D3D11CreateDevice(adapter, DriverType.Unknown, DeviceCreationFlags.BgraSupport, levels,
                out device, out _, out context).CheckError();
            return new GpuClipPreviewAdapter(owner, compositor, interop, device, context);
        }
        catch
        {
            device?.Dispose();
            context?.Dispose();
            return null;
        }
        finally { adapter?.Dispose(); }
    }

    public ValueTask ActivateSessionAsync(CancellationToken cancellationToken) => ValueTask.CompletedTask;

    public ValueTask SetAttachedAsync(bool attached)
    {
        _attached = attached;
        _visual.Opacity = attached ? 1 : 0;
        return ValueTask.CompletedTask;
    }

    public void Resize(Size size) => _visual.Size = ToVector(size);

    public async ValueTask<PreviewPresentResult> PresentAsync(ReadOnlyMemory<byte> rgba, PixelSize size, CancellationToken cancellationToken)
    {
        // The copies below read size.Width * size.Height * 4 bytes from rgba with no
        // reference to its actual length. It is correct today - the only producer
        // allocates exactly that, fills every slot, passes the same size, and pins
        // ffmpeg to scale+pad with -pix_fmt rgba - but this is the path that consumes
        // decoder output from clips the user may have imported, so it should not depend
        // on a caller invariant to stay in bounds.
        var requiredBytes = (long)size.Width * size.Height * 4;
        if (size.Width <= 0 || size.Height <= 0 || rgba.Length < requiredBytes)
        {
            throw new ArgumentException(
                $"Preview buffer is {rgba.Length} bytes; {size.Width}x{size.Height} RGBA needs {requiredBytes}.",
                nameof(rgba));
        }

        if (_disposed || _interop.IsLost) throw new InvalidOperationException("Avalonia composition device is unavailable.");
        // An import that failed on the render thread leaves a texture the
        // compositor will never read. Fail here so the caller drops to the
        // software path once, instead of feeding frames into nothing.
        if (Volatile.Read(ref _importFailed) != 0) throw new InvalidOperationException("Preview texture import failed.");
        if (!_attached) return new PreviewPresentResult(Path, TimeSpan.Zero);
        var started = Stopwatch.GetTimestamp();
        var slot = await AcquireSlotAsync(size, cancellationToken).ConfigureAwait(false);
        if (slot.Imported.IsLost) throw new InvalidOperationException("Preview texture was lost.");

        // Off the UI thread deliberately. Hover previews are started from a
        // pointer event and nothing in this pipeline calls ConfigureAwait(false),
        // so every continuation lands back on the UI thread - which put a keyed
        // mutex wait and a full-frame copy, sixty times a second, on the thread
        // that draws the window. When the GPU path is unhealthy that wait is the
        // whole timeout, and the app simply stops responding.
        await Task.Run(() => UploadFrame(slot, rgba, size), cancellationToken).ConfigureAwait(false);

        // Composition objects are UI-thread affine, so the present itself goes
        // back - but it only queues a job, it does not block.
        await Dispatcher.UIThread.InvokeAsync(
            () => slot.LastPresent = _surface.UpdateWithKeyedMutexAsync(slot.Imported, 1, 0));
        return new PreviewPresentResult(Path, Stopwatch.GetElapsedTime(started));
    }

    private unsafe void UploadFrame(TextureSlot slot, ReadOnlyMemory<byte> rgba, PixelSize size)
    {
        // Shorter than the two seconds this used to wait: the compositor reads a
        // slot in one frame, so anything beyond a few hundred milliseconds means
        // the GPU path is wedged, and stalling the pipeline does not unwedge it.
        slot.Mutex.AcquireSync(0, MutexWaitMilliseconds);
        try
        {
            fixed (byte* source = rgba.Span)
                _context.UpdateSubresource(slot.Texture, 0, null, (nint)source, (uint)(size.Width * 4), 0);
        }
        finally { slot.Mutex.ReleaseSync(1); }
    }

    private async ValueTask<TextureSlot> AcquireSlotAsync(PixelSize size, CancellationToken cancellationToken)
    {
        if (_slots.Count != 0 && _slots[0].Size != size) await DisposeSlotsAsync();
        while (_slots.Count < 3) _slots.Add(CreateSlot(size));
        var slot = _slots[_nextSlot++ % _slots.Count];
        await slot.LastPresent.WaitAsync(cancellationToken).ConfigureAwait(false);
        return slot;
    }

    private TextureSlot CreateSlot(PixelSize size)
    {
        var texture = _device.CreateTexture2D(new Texture2DDescription
        {
            Width = (uint)size.Width,
            Height = (uint)size.Height,
            MipLevels = 1,
            ArraySize = 1,
            Format = DxgiFormat.R8G8B8A8_UNorm,
            SampleDescription = new SampleDescription(1, 0),
            Usage = ResourceUsage.Default,
            BindFlags = BindFlags.ShaderResource,
            CPUAccessFlags = CpuAccessFlags.None,
            MiscFlags = ResourceOptionFlags.SharedKeyedMutex
        });
        var mutex = texture.QueryInterface<IDXGIKeyedMutex>();
        using var resource = texture.QueryInterface<IDXGIResource>();
        var imported = _interop.ImportImage(
            new PlatformHandle(resource.SharedHandle, KnownPlatformGraphicsExternalImageHandleTypes.D3D11TextureGlobalSharedHandle),
            new PlatformGraphicsExternalImageProperties { Width = size.Width, Height = size.Height, Format = PlatformGraphicsExternalImageFormat.R8G8B8A8UNorm, TopLeftOrigin = true });
        // The import runs on the render thread and nothing here awaits it, so a
        // failure - a lost ANGLE context, a shared handle the driver rejects -
        // used to surface only as an unobserved exception rethrown by the
        // finalizer, hundreds of them, while this path kept importing more.
        _ = imported.ImportCompleted.ContinueWith(task =>
        {
            if (!task.IsFaulted) return;
            if (Interlocked.Exchange(ref _importFailed, 1) == 0)
                AppLog.Info($"Clip hover preview: GPU texture import failed ({task.Exception?.GetBaseException().Message}).");
        }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        return new TextureSlot(size, texture, mutex, imported);
    }

    private async ValueTask DisposeSlotsAsync()
    {
        foreach (var slot in _slots) await slot.DisposeAsync();
        _slots.Clear();
        _nextSlot = 0;
    }

    public ValueTask ReleaseResourcesAsync() => DisposeCoreAsync();

    public ValueTask DisposeAsync() => DisposeCoreAsync();

    private async ValueTask DisposeCoreAsync()
    {
        if (_disposed) return;
        _disposed = true;
        _visual.Opacity = 0;
        ElementComposition.SetElementChildVisual(_owner, null);
        await DisposeSlotsAsync();
        _surface.Dispose();
        _context.Dispose();
        _device.Dispose();
    }

    private static bool MatchesLuid(Vortice.Luid luid, byte[] bytes) =>
        BitConverter.ToUInt32(bytes, 0) == luid.LowPart && BitConverter.ToInt32(bytes, 4) == luid.HighPart;

    private static Vector ToVector(Size size) => new(size.Width, size.Height);

    private sealed class TextureSlot(PixelSize size, ID3D11Texture2D texture, IDXGIKeyedMutex mutex, ICompositionImportedGpuImage imported) : IAsyncDisposable
    {
        public PixelSize Size { get; } = size;
        public ID3D11Texture2D Texture { get; } = texture;
        public IDXGIKeyedMutex Mutex { get; } = mutex;
        public ICompositionImportedGpuImage Imported { get; } = imported;
        public Task LastPresent { get; set; } = Task.CompletedTask;

        public async ValueTask DisposeAsync()
        {
            try { await LastPresent.ConfigureAwait(false); } catch { }
            await Imported.DisposeAsync();
            Mutex.Dispose();
            Texture.Dispose();
        }
    }
}
