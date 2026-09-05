using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Avalonia.Threading;
using ClypDat.App.Services;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DirectComposition;
using Vortice.DXGI;
using D3D11Api = Vortice.Direct3D11.D3D11;
using DxgiFormat = Vortice.DXGI.Format;

namespace ClypDat.App.Views;

// One native notification surface. HWND, GPU presenter and timer live on its
// dedicated thread; Avalonia's frame clock never participates.
internal sealed unsafe class NativeClipOverlaySurface : IClipOverlaySurface
{
    private const int TimerId = 1;
    private const uint WmAppPublish = 0x8001, WmAppDismiss = 0x8002, WmClose = 0x0010, WmDestroy = 0x0002, WmTimer = 0x0113;
    private const int WsExTopmost = 0x00000008, WsExLayered = 0x00080000, WsExTransparent = 0x20, WsExToolWindow = 0x80, WsExNoActivate = 0x08000000, WsExNoRedirectionBitmap = 0x00200000, GwlExStyle = -20;
    private const uint WsPopup = 0x80000000, WdaExcludeFromCapture = 0x11, SwpNoSize = 0x0001, SwpNoMove = 0x0002, SwpNoActivate = 0x0010, SwpShowWindow = 0x0040, SwpFrameChanged = 0x0020;
    private static readonly nint HwndTopmost = new(-1);
    private static readonly ConcurrentDictionary<nint, NativeClipOverlaySurface> Instances = new();
    private static readonly WindowProc SharedWindowProc = WindowProcedure;
    private static int _classRegistered;
    private readonly Func<ClipOverlayPresentation, ClipOverlayFrame> _render;
    private readonly bool _requiresUiThread;
    private readonly Func<nint, INativeClipOverlayPresenter>? _presenterFactory;
    private readonly object _gate = new();
    private readonly Thread _thread;
    private readonly ManualResetEventSlim _ready = new();
    private (ClipOverlayPresentation Presentation, ClipOverlayFrame Frame, Action<ClipOverlayPresentationResult> Completion)? _pending;
    private ClipOverlayPresentation? _current;
    private ClipOverlayFrame? _frame;
    private INativeClipOverlayPresenter? _presenter;
    private long _pendingDismissal, _motionStarted, _latestGeneration;
    private nint _window;
    private int _width, _height, _publishCount;
    private Motion _motion;
    private double _opacity, _motionStartOpacity;
    private Action<ClipOverlayPresentationResult>? _presentationCompletion;
    private bool _visible, _disposed, _gpuRecoveryAttempted, _nativePublishLogged, _nativeFailureLogged;

    public NativeClipOverlaySurface(Func<ClipOverlayPresentation, ClipOverlayFrame>? render = null, Func<nint, INativeClipOverlayPresenter>? presenterFactory = null)
    {
        _render = render ?? ClipOverlayCardRenderer.Render;
        _requiresUiThread = render is null;
        _presenterFactory = presenterFactory;
        _thread = new Thread(Run) { IsBackground = true, Name = "ClypDat clip overlay" };
        _thread.SetApartmentState(ApartmentState.STA);
        _thread.Start();
        _ready.Wait(TimeSpan.FromSeconds(2));
    }

    internal nint WindowHandle => _window;
    internal int PublishCount => Volatile.Read(ref _publishCount);
    internal string PresenterName => _presenter?.Name ?? "unavailable";

    public void Publish(ClipOverlayPresentation presentation, Action<ClipOverlayPresentationResult> completion)
    {
        lock (_gate) { if (_disposed) return; }
        if (_requiresUiThread && !Dispatcher.UIThread.CheckAccess()) { Dispatcher.UIThread.Post(() => Publish(presentation, completion)); return; }
        ClipOverlayFrame frame;
        try { frame = _render(presentation); }
        catch (Exception error)
        {
            AppLog.Error("Clip overlay card rendering failed", error);
            completion(new ClipOverlayPresentationResult(presentation.Generation, false));
            return;
        }
        (long Generation, Action<ClipOverlayPresentationResult> Completion)? replaced = null;
        var rejected = false;
        lock (_gate)
        {
            if (_disposed || presentation.Generation <= _pendingDismissal || presentation.Generation <= _latestGeneration) rejected = true;
            else
            {
                replaced = _pending is { } pending ? (pending.Presentation.Generation, pending.Completion) : null;
                _latestGeneration = presentation.Generation;
                _pending = (presentation, frame, completion);
            }
        }
        if (replaced is { } superseded) superseded.Completion(new ClipOverlayPresentationResult(superseded.Generation, false));
        if (rejected) { completion(new ClipOverlayPresentationResult(presentation.Generation, false)); return; }
        if (_window == 0 || !PostMessage(_window, WmAppPublish, 0, 0)) completion(new ClipOverlayPresentationResult(presentation.Generation, false));
    }

    public void Dismiss(long generation)
    {
        lock (_gate) { if (_disposed) return; _pendingDismissal = Math.Max(_pendingDismissal, generation); }
        if (_window != 0) PostMessage(_window, WmAppDismiss, 0, 0);
    }

    public void Dispose()
    {
        lock (_gate) { if (_disposed) return; _disposed = true; }
        if (_window != 0) PostMessage(_window, WmClose, 0, 0);
        if (Thread.CurrentThread != _thread) _thread.Join(TimeSpan.FromSeconds(2));
        _ready.Dispose();
    }

    private void Run()
    {
        try
        {
            RegisterWindowClass();
            _window = CreateWindowEx(WsExTopmost | WsExNoRedirectionBitmap | WsExTransparent | WsExToolWindow | WsExNoActivate, ClassName, string.Empty, WsPopup, -32000, -32000, 1, 1, 0, 0, GetModuleHandle(null), 0);
            if (_window == 0) throw new InvalidOperationException($"Could not create clip overlay window ({Marshal.GetLastWin32Error()}).");
            Instances[_window] = this;
            CreatePresenter();
            SetTimer(_window, TimerId, 15, 0);
            _ready.Set();
            while (GetMessage(out var message, 0, 0, 0) > 0) { TranslateMessage(ref message); DispatchMessage(ref message); }
        }
        catch (Exception error) { AppLog.Error("Native clip overlay thread failed", error); _ready.Set(); }
        finally { _presenter?.Dispose(); if (_window != 0) Instances.TryRemove(_window, out _); _window = 0; }
    }

    private void CreatePresenter()
    {
        try
        {
            _presenter = _presenterFactory?.Invoke(_window) ?? new DirectCompositionClipOverlayPresenter(_window);
            AppLog.Info($"Clip overlay presenter selected: {_presenter.Name}.");
        }
        catch (Exception error) { AppLog.Error("Clip overlay DirectComposition initialization failed; using layered fallback", error); UseLayeredPresenter(); }
    }

    private void UseLayeredPresenter()
    {
        _presenter?.Dispose();
        _presenter = null;
        var style = GetWindowLongPtr(_window, GwlExStyle).ToInt64();
        SetWindowLongPtr(_window, GwlExStyle, new nint((style | WsExLayered) & ~WsExNoRedirectionBitmap));
        SetWindowPos(_window, HwndTopmost, 0, 0, 0, 0, SwpNoSize | SwpNoMove | SwpNoActivate | SwpFrameChanged);
        _presenter = new LayeredClipOverlayPresenter(_window);
        AppLog.Info("Clip overlay presenter selected: layered fallback.");
    }

    private void AcceptPublish()
    {
        (ClipOverlayPresentation Presentation, ClipOverlayFrame Frame, Action<ClipOverlayPresentationResult> Completion)? pending;
        lock (_gate) { pending = _pending; _pending = null; if (pending is { } expired && expired.Presentation.Generation <= _pendingDismissal) return; }
        if (pending is not { } update || (_current is { } current && current.Generation > update.Presentation.Generation)) return;
        var sameWorkflow = _visible && _current?.Event.WorkflowId == update.Presentation.Event.WorkflowId;
        _current = update.Presentation;
        _frame = update.Frame;
        _width = update.Frame.Width;
        _height = update.Frame.Height;
        _nativePublishLogged = _nativeFailureLogged = false;
        _gpuRecoveryAttempted = false;
        SetWindowDisplayAffinity(_window, _current.Event.ExcludeFromCapture ? WdaExcludeFromCapture : 0);
        _presentationCompletion = update.Completion;
        _visible = true;
        if (!sameWorkflow)
        {
            _motion = Motion.Entering;
            _motionStartOpacity = _opacity = 0;
            _motionStarted = Stopwatch.GetTimestamp();
            Present(0, true);
        }
        else
        {
            if (_motion == Motion.Exiting)
            {
                _motion = Motion.Entering;
                _motionStartOpacity = _opacity;
                _motionStarted = Stopwatch.GetTimestamp();
            }
            if (Present(_opacity, true) && _opacity > 0) AcknowledgePresentation();
        }
        Interlocked.Increment(ref _publishCount);
    }

    private void AcceptDismissal()
    {
        long generation; lock (_gate) generation = _pendingDismissal;
        if (!_visible || _current?.Generation != generation) return;
        _motion = Motion.Exiting;
        _motionStartOpacity = _opacity;
        _motionStarted = Stopwatch.GetTimestamp();
    }

    private void Tick()
    {
        if (!_visible || _current is null) return;
        var elapsed = Stopwatch.GetElapsedTime(_motionStarted).TotalMilliseconds;
        if (_motion == Motion.Entering)
        {
            var progress = _motionStartOpacity + (1 - _motionStartOpacity) * EaseOut(Math.Min(1, elapsed / 220));
            if (Present(progress, false) && progress > 0) AcknowledgePresentation();
            if (elapsed >= 220) { _motion = Motion.Still; if (Present(1, false)) AcknowledgePresentation(); }
        }
        else if (_motion == Motion.Exiting)
        {
            Present(_motionStartOpacity * (1 - EaseIn(Math.Min(1, elapsed / 180))), false);
            if (elapsed >= 180 || elapsed >= 400) Hide();
        }
        else if (Present(1, false)) AcknowledgePresentation(); // Reassert topmost during full dwell.
    }

    private void Hide() { _presenter?.Hide(); _visible = false; _motion = Motion.Still; _opacity = _motionStartOpacity = 0; _current = null; _frame = null; _presentationCompletion = null; }

    private void AcknowledgePresentation()
    {
        var completion = Interlocked.Exchange(ref _presentationCompletion, null);
        completion?.Invoke(new ClipOverlayPresentationResult(_current!.Generation, true));
    }

    private bool Present(double progress, bool frameChanged)
    {
        if (_current is null || _frame is null || _presenter is null) return false;
        var target = _current.Event.Target;
        var placement = _current.Event.Placement;
        var position = ClipOverlayLayout.AnimatedPosition(target, placement, _width, _height, progress);
        var destination = new PointNative(position.X, position.Y);
        try
        {
            _presenter.Present(_frame, destination, _width, _height, progress, frameChanged);
            _opacity = progress;
            if (progress > 0 && !_nativePublishLogged)
            {
                AppLog.Info($"Clip overlay native publish succeeded: id={_current.Event.WorkflowId}, kind={_current.Event.Kind}, backend={_presenter.Name}, monitor={target.DeviceName}, position={destination.X},{destination.Y}, size={_width}x{_height}.");
                _nativePublishLogged = true;
            }
            return true;
        }
        catch (Exception error)
        {
            if (_presenter is DirectCompositionClipOverlayPresenter && !_gpuRecoveryAttempted)
            {
                _gpuRecoveryAttempted = true;
                AppLog.Error("Clip overlay DirectComposition presentation failed; rebuilding", error);
                try
                {
                    _presenter.Dispose();
                    _presenter = new DirectCompositionClipOverlayPresenter(_window);
                    _presenter.Present(_frame, destination, _width, _height, progress, true);
                    _opacity = progress;
                    return true;
                }
                catch (Exception recoveryError)
                {
                    AppLog.Error("Clip overlay DirectComposition rebuild failed; using layered fallback", recoveryError);
                    UseLayeredPresenter();
                    _presenter.Present(_frame, destination, _width, _height, progress, true);
                    _opacity = progress;
                    return true;
                }
            }
            if (!_nativeFailureLogged) AppLog.Error($"Clip overlay native publish failed: id={_current.Event.WorkflowId}, backend={_presenter.Name}.", error);
            _nativeFailureLogged = true;
            return false;
        }
    }

    private static double EaseOut(double value) => 1 - Math.Pow(1 - value, 3);
    private static double EaseIn(double value) => value * value * value;
    private static string ClassName => "ClypDat.NativeClipOverlay";
    private static void RegisterWindowClass()
    {
        if (Interlocked.Exchange(ref _classRegistered, 1) != 0) return;
        var value = new WindowClass { Size = (uint)Marshal.SizeOf<WindowClass>(), Instance = GetModuleHandle(null), ClassName = ClassName, WindowProcedure = SharedWindowProc };
        if (RegisterClassEx(ref value) == 0 && Marshal.GetLastWin32Error() != 1410) throw new InvalidOperationException($"Could not register clip overlay class ({Marshal.GetLastWin32Error()}).");
    }
    private static nint WindowProcedure(nint window, uint message, nint wParam, nint lParam)
    {
        if (!Instances.TryGetValue(window, out var instance)) return DefWindowProc(window, message, wParam, lParam);
        switch (message)
        {
            case WmAppPublish: instance.AcceptPublish(); return 0;
            case WmAppDismiss: instance.AcceptDismissal(); return 0;
            case WmTimer: instance.Tick(); return 0;
            case WmClose: DestroyWindow(window); return 0;
            case WmDestroy: return Destroyed(instance);
            default: return DefWindowProc(window, message, wParam, lParam);
        }
    }
    private static nint Destroyed(NativeClipOverlaySurface instance) { KillTimer(instance._window, TimerId); PostQuitMessage(0); return 0; }

    internal interface INativeClipOverlayPresenter : IDisposable { string Name { get; } void Present(ClipOverlayFrame frame, PointNative destination, int width, int height, double opacity, bool frameChanged); void Hide(); }
    internal static bool RequiresFrameUpload(bool frameChanged, bool hasSwapChain, double lastUploadedOpacity, double opacity)
        => frameChanged || !hasSwapChain || double.IsNaN(lastUploadedOpacity) || Math.Abs(lastUploadedOpacity - opacity) > 0.0001;

    private sealed class DirectCompositionClipOverlayPresenter : INativeClipOverlayPresenter
    {
        private readonly nint _window;
        private readonly ID3D11Device _device;
        private readonly ID3D11DeviceContext _context;
        private readonly IDXGIDevice _dxgiDevice;
        private readonly IDXGIFactory2 _factory;
        private readonly IDCompositionDevice _composition;
        private readonly IDCompositionTarget _target;
        private readonly IDCompositionVisual _visual;
        private IDXGISwapChain1? _swapChain;
        private IDXGISwapChain3? _swapChain3;
        private byte[]? _fadedPixels;
        private int _width, _height;
        private double _lastUploadedOpacity = double.NaN;
        public DirectCompositionClipOverlayPresenter(nint window)
        {
            _window = window;
            var levels = new[] { FeatureLevel.Level_11_1, FeatureLevel.Level_11_0, FeatureLevel.Level_10_1 };
            D3D11Api.D3D11CreateDevice(null, DriverType.Hardware, DeviceCreationFlags.BgraSupport, levels, out _device, out _, out _context).CheckError();
            _dxgiDevice = _device.QueryInterface<IDXGIDevice>();
            using var adapter = _dxgiDevice.GetParent<IDXGIAdapter>();
            _factory = adapter.GetParent<IDXGIFactory2>();
            _composition = DComp.DCompositionCreateDevice<IDCompositionDevice>(_dxgiDevice);
            _composition.CreateTargetForHwnd(window, true, out _target).CheckError();
            _composition.CreateVisual(out _visual).CheckError();
            _target.SetRoot(_visual).CheckError();
            _composition.Commit().CheckError();
        }
        public string Name => "DirectComposition";
        public unsafe void Present(ClipOverlayFrame frame, PointNative destination, int width, int height, double opacity, bool frameChanged)
        {
            SetWindowPos(_window, HwndTopmost, destination.X, destination.Y, width, height, SwpNoActivate | SwpShowWindow);
            EnsureSwapChain(width, height);
            if (!RequiresFrameUpload(frameChanged, _swapChain is not null, _lastUploadedOpacity, opacity)) return;
            using var texture = _swapChain!.GetBuffer<ID3D11Texture2D>(_swapChain3!.CurrentBackBufferIndex);
            var pixels = opacity >= 0.999 ? frame.Pixels : Fade(frame.Pixels, opacity);
            fixed (byte* source = pixels) _context.UpdateSubresource(texture, 0, null, (nint)source, (uint)(width * 4), 0);
            _swapChain.Present(0, PresentFlags.None).CheckError();
            _composition.Commit().CheckError();
            _lastUploadedOpacity = opacity;
        }
        public void Hide() => ShowWindow(_window, 0);
        private void EnsureSwapChain(int width, int height)
        {
            if (_swapChain is not null && _width == width && _height == height) return;
            _visual.SetContent(null).CheckError();
            _swapChain3?.Dispose();
            _swapChain3 = null;
            _swapChain?.Dispose();
            var description = new SwapChainDescription1((uint)width, (uint)height, DxgiFormat.B8G8R8A8_UNorm, false, Usage.RenderTargetOutput, 2, Scaling.Stretch, SwapEffect.FlipSequential, AlphaMode.Premultiplied, SwapChainFlags.None);
            _swapChain = _factory.CreateSwapChainForComposition(_device, description);
            _swapChain3 = _swapChain.QueryInterface<IDXGISwapChain3>();
            _visual.SetContent(_swapChain).CheckError();
            _width = width; _height = height;
            _lastUploadedOpacity = double.NaN;
        }
        private byte[] Fade(byte[] source, double opacity)
        {
            _fadedPixels ??= new byte[source.Length];
            if (_fadedPixels.Length != source.Length) _fadedPixels = new byte[source.Length];
            for (var index = 0; index < source.Length; index++) _fadedPixels[index] = (byte)Math.Round(source[index] * opacity);
            return _fadedPixels;
        }
        public void Dispose()
        {
            _visual.SetContent(null); _swapChain3?.Dispose(); _swapChain?.Dispose(); _visual.Dispose(); _target.Dispose(); _composition.Dispose(); _factory.Dispose(); _dxgiDevice.Dispose(); _context.Dispose(); _device.Dispose();
        }
    }
    private sealed class LayeredClipOverlayPresenter : INativeClipOverlayPresenter
    {
        private const uint UlwAlpha = 2;
        private readonly nint _window;
        private nint _memoryDc, _bitmap, _oldBitmap, _bits;
        private int _width, _height;
        public LayeredClipOverlayPresenter(nint window) => _window = window;
        public string Name => "layered";
        public void Present(ClipOverlayFrame frame, PointNative destination, int width, int height, double opacity, bool frameChanged)
        {
            if (frameChanged || width != _width || height != _height) CreateBitmap(frame);
            var source = new PointNative(0, 0); var size = new SizeNative(width, height);
            var blend = new BlendFunction(0, 0, (byte)Math.Clamp((int)Math.Round(255 * opacity), 0, 255), 1);
            if (!UpdateLayeredWindow(_window, 0, ref destination, ref size, _memoryDc, ref source, 0, ref blend, UlwAlpha)) throw new InvalidOperationException($"UpdateLayeredWindow failed ({Marshal.GetLastWin32Error()}).");
            SetWindowPos(_window, HwndTopmost, destination.X, destination.Y, width, height, SwpNoActivate | SwpShowWindow);
        }
        public void Hide() => ShowWindow(_window, 0);
        private void CreateBitmap(ClipOverlayFrame frame)
        {
            DestroyBitmap(); _width = frame.Width; _height = frame.Height; _memoryDc = CreateCompatibleDC(0);
            var info = new BitmapInfo { Header = new BitmapInfoHeader { Size = (uint)Marshal.SizeOf<BitmapInfoHeader>(), Width = _width, Height = -_height, Planes = 1, BitCount = 32 } };
            _bitmap = CreateDIBSection(_memoryDc, ref info, 0, out _bits, 0, 0); _oldBitmap = SelectObject(_memoryDc, _bitmap); Marshal.Copy(frame.Pixels, 0, _bits, frame.Pixels.Length);
        }
        private void DestroyBitmap() { if (_memoryDc != 0 && _oldBitmap != 0) SelectObject(_memoryDc, _oldBitmap); if (_bitmap != 0) DeleteObject(_bitmap); if (_memoryDc != 0) DeleteDC(_memoryDc); _memoryDc = _bitmap = _oldBitmap = _bits = 0; }
        public void Dispose() => DestroyBitmap();
    }

    private enum Motion { Still, Entering, Exiting }
    [StructLayout(LayoutKind.Sequential)] internal struct PointNative { public int X, Y; public PointNative(int x, int y) { X = x; Y = y; } }
    [StructLayout(LayoutKind.Sequential)] private struct SizeNative { public int Width, Height; public SizeNative(int width, int height) { Width = width; Height = height; } }
    [StructLayout(LayoutKind.Sequential, Pack = 1)] private struct BlendFunction { public byte BlendOp, BlendFlags, SourceConstantAlpha, AlphaFormat; public BlendFunction(byte op, byte flags, byte alpha, byte format) { BlendOp = op; BlendFlags = flags; SourceConstantAlpha = alpha; AlphaFormat = format; } }
    [StructLayout(LayoutKind.Sequential)] private struct BitmapInfo { public BitmapInfoHeader Header; public uint Colors; }
    [StructLayout(LayoutKind.Sequential)] private struct BitmapInfoHeader { public uint Size; public int Width, Height; public ushort Planes, BitCount; public uint Compression, SizeImage; public int XPelsPerMeter, YPelsPerMeter; public uint ColorsUsed, ColorsImportant; }
    [StructLayout(LayoutKind.Sequential)] private struct Message { public nint Window; public uint Value; public nint WParam, LParam; public uint Time; public PointNative Point; public uint Private; }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)] private struct WindowClass { public uint Size, Style; public WindowProc? WindowProcedure; public int ClassExtra, WindowExtra; public nint Instance, Icon, Cursor, Background; [MarshalAs(UnmanagedType.LPWStr)] public string? MenuName; [MarshalAs(UnmanagedType.LPWStr)] public string ClassName; public nint SmallIcon; }
    private delegate nint WindowProc(nint window, uint message, nint wParam, nint lParam);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern nint GetModuleHandle(string? moduleName);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern ushort RegisterClassEx(ref WindowClass windowClass);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern nint CreateWindowEx(int extendedStyle, string className, string windowName, uint style, int x, int y, int width, int height, nint parent, nint menu, nint instance, nint parameter);
    [DllImport("user32.dll")] private static extern bool DestroyWindow(nint window);
    [DllImport("user32.dll")] private static extern nint DefWindowProc(nint window, uint message, nint wParam, nint lParam);
    [DllImport("user32.dll")] private static extern int GetMessage(out Message message, nint window, uint minimum, uint maximum);
    [DllImport("user32.dll")] private static extern bool TranslateMessage(ref Message message);
    [DllImport("user32.dll")] private static extern nint DispatchMessage(ref Message message);
    [DllImport("user32.dll")] private static extern void PostQuitMessage(int exitCode);
    [DllImport("user32.dll")] private static extern bool PostMessage(nint window, uint message, nint wParam, nint lParam);
    [DllImport("user32.dll")] private static extern nuint SetTimer(nint window, nuint id, uint milliseconds, nint callback);
    [DllImport("user32.dll")] private static extern bool KillTimer(nint window, nuint id);
    [DllImport("user32.dll")] private static extern bool ShowWindow(nint window, uint command);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(nint window, nint insertAfter, int x, int y, int width, int height, uint flags);
    [DllImport("user32.dll")] private static extern bool SetWindowDisplayAffinity(nint window, uint affinity);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] private static extern nint GetWindowLongPtr(nint window, int index);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")] private static extern nint SetWindowLongPtr(nint window, int index, nint value);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool UpdateLayeredWindow(nint window, nint destinationDc, ref PointNative destination, ref SizeNative size, nint sourceDc, ref PointNative source, uint colorKey, ref BlendFunction blend, uint flags);
    [DllImport("gdi32.dll")] private static extern nint CreateCompatibleDC(nint dc);
    [DllImport("gdi32.dll")] private static extern bool DeleteDC(nint dc);
    [DllImport("gdi32.dll")] private static extern nint CreateDIBSection(nint dc, ref BitmapInfo info, uint usage, out nint bits, nint section, uint offset);
    [DllImport("gdi32.dll")] private static extern nint SelectObject(nint dc, nint value);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(nint value);
}
