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
    void SetAttached(bool attached);
    ValueTask<PreviewPresentResult> PresentAsync(ReadOnlyMemory<byte> rgba, PixelSize size, CancellationToken cancellationToken);
}

internal enum PreviewPresentationPath { Gpu, Software }

internal readonly record struct PreviewPresentResult(PreviewPresentationPath Path, TimeSpan Latency);

public sealed class ClipPreviewPresenter : Control, IClipPreviewPresenter
{
    private readonly SoftwareClipPreviewAdapter _software;
    private IClipPreviewPresenter? _adapter;
    private bool _requestedAttached;
    private bool _disposed;
    private Task? _gpuInitialization;

    public ClipPreviewPresenter()
    {
        _software = new SoftwareClipPreviewAdapter(this);
        _adapter = _software;
    }

    PreviewPresentationPath IClipPreviewPresenter.Path => _adapter?.Path ?? PreviewPresentationPath.Software;

    protected override void OnAttachedToVisualTree(Avalonia.VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _gpuInitialization ??= InitializeGpuAsync();
    }

    protected override void OnDetachedFromVisualTree(Avalonia.VisualTreeAttachmentEventArgs e)
    {
        SetAttached(false);
        _ = ReleaseGpuAsync();
        base.OnDetachedFromVisualTree(e);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        if (change.Property == BoundsProperty && _adapter is GpuClipPreviewAdapter gpu)
            gpu.Resize(Bounds.Size);
        base.OnPropertyChanged(change);
    }

    public override void Render(DrawingContext context)
    {
        if (_requestedAttached && _adapter == _software && _software.Bitmap is { } bitmap)
            context.DrawImage(bitmap, Bounds);
    }

    public void SetAttached(bool attached)
    {
        _requestedAttached = attached;
        if (!Dispatcher.UIThread.CheckAccess())
        {
            Dispatcher.UIThread.Post(() => ApplyAttached(attached), DispatcherPriority.Render);
            return;
        }
        ApplyAttached(attached);
    }

    private void ApplyAttached(bool attached)
    {
        _adapter?.SetAttached(attached);
        InvalidateVisual();
    }

    async ValueTask<PreviewPresentResult> IClipPreviewPresenter.PresentAsync(ReadOnlyMemory<byte> rgba, PixelSize size, CancellationToken cancellationToken)
    {
        if (_disposed) throw new ObjectDisposedException(nameof(ClipPreviewPresenter));
        if (_gpuInitialization is { } initialization) await initialization.ConfigureAwait(false);
        var adapter = _adapter ?? _software;
        try
        {
            return await adapter.PresentAsync(rgba, size, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception error) when (adapter is GpuClipPreviewAdapter && !cancellationToken.IsCancellationRequested)
        {
            // Device loss/import failures must not discard this frame. Switch
            // immediately, then render the same decoded bytes through software.
            AppLog.Info($"Clip hover preview GPU path lost; switching to software: {error.Message}");
            await SwitchToSoftwareAsync(adapter).ConfigureAwait(false);
            return await _software.PresentAsync(rgba, size, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task InitializeGpuAsync()
    {
        try
        {
            var element = ElementComposition.GetElementVisual(this);
            if (element is null) return;
            var interop = await element.Compositor.TryGetCompositionGpuInterop();
            if (interop is null || interop.IsLost || interop.DeviceLuid is not { Length: 8 }
                || !interop.SupportedImageHandleTypes.Contains(KnownPlatformGraphicsExternalImageHandleTypes.D3D11TextureGlobalSharedHandle)
                || !interop.GetSynchronizationCapabilities(KnownPlatformGraphicsExternalImageHandleTypes.D3D11TextureGlobalSharedHandle)
                    .HasFlag(CompositionGpuImportedImageSynchronizationCapabilities.KeyedMutex))
                return;

            var gpu = GpuClipPreviewAdapter.TryCreate(this, element.Compositor, interop);
            if (gpu is null) return;
            _adapter = gpu;
            gpu.SetAttached(_requestedAttached);
            InvalidateVisual();
            AppLog.Debug("Clip hover preview presenter: D3D11 composition path ready.");
        }
        catch (Exception error)
        {
            AppLog.Debug($"Clip hover preview presenter: software path selected ({error.Message}).");
        }
    }

    private async Task SwitchToSoftwareAsync(IClipPreviewPresenter failed)
    {
        if (!ReferenceEquals(_adapter, failed)) return;
        _adapter = _software;
        _software.SetAttached(_requestedAttached);
        await failed.DisposeAsync();
        await Dispatcher.UIThread.InvokeAsync(InvalidateVisual, DispatcherPriority.Render);
    }

    private async Task ReleaseGpuAsync()
    {
        if (_adapter is not GpuClipPreviewAdapter gpu) return;
        _adapter = _software;
        _gpuInitialization = null;
        await gpu.DisposeAsync();
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        var adapter = Interlocked.Exchange(ref _adapter, null);
        if (adapter is not null && adapter != _software) await adapter.DisposeAsync();
        await _software.DisposeAsync();
    }
}

internal sealed class SoftwareClipPreviewAdapter(ClipPreviewPresenter owner) : IClipPreviewPresenter
{
    private WriteableBitmap? _bitmap;
    private bool _attached;

    public WriteableBitmap? Bitmap => _bitmap;
    public PreviewPresentationPath Path => PreviewPresentationPath.Software;

    public void SetAttached(bool attached) => _attached = attached;

    public async ValueTask<PreviewPresentResult> PresentAsync(ReadOnlyMemory<byte> rgba, PixelSize size, CancellationToken cancellationToken)
    {
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

    public ValueTask DisposeAsync()
    {
        var bitmap = Interlocked.Exchange(ref _bitmap, null);
        Services.DeferredBitmapDisposal.Release(bitmap);
        return ValueTask.CompletedTask;
    }
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
    private bool _attached;
    private bool _disposed;

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

    public void SetAttached(bool attached)
    {
        _attached = attached;
        _visual.Opacity = attached ? 1 : 0;
    }

    public void Resize(Size size) => _visual.Size = ToVector(size);

    public async ValueTask<PreviewPresentResult> PresentAsync(ReadOnlyMemory<byte> rgba, PixelSize size, CancellationToken cancellationToken)
    {
        if (_disposed || _interop.IsLost) throw new InvalidOperationException("Avalonia composition device is unavailable.");
        if (!_attached) return new PreviewPresentResult(Path, TimeSpan.Zero);
        var started = Stopwatch.GetTimestamp();
        var slot = await AcquireSlotAsync(size, cancellationToken).ConfigureAwait(false);
        slot.Mutex.AcquireSync(0, 2_000);
        try
        {
            unsafe
            {
                fixed (byte* source = rgba.Span)
                    _context.UpdateSubresource(slot.Texture, 0, null, (nint)source, (uint)(size.Width * 4), 0);
            }
        }
        finally { slot.Mutex.ReleaseSync(1); }
        slot.LastPresent = _surface.UpdateWithKeyedMutexAsync(slot.Imported, 1, 0);
        return new PreviewPresentResult(Path, Stopwatch.GetElapsedTime(started));
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
        return new TextureSlot(size, texture, mutex, imported);
    }

    private async ValueTask DisposeSlotsAsync()
    {
        foreach (var slot in _slots) await slot.DisposeAsync();
        _slots.Clear();
        _nextSlot = 0;
    }

    public async ValueTask DisposeAsync()
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
