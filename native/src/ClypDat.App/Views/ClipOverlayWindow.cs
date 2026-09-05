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

// What one motion asks the presenter for: where the window sits for the whole
// notification, how big the card inside it is, and the opacity/offset pair the
// card travels between. Offset is affine in opacity, so both curves share one
// easing and cannot drift apart.
internal readonly record struct ClipOverlayMotionPlan(
    int WindowX, int WindowY, int WindowWidth, int WindowHeight,
    int CardWidth, int CardHeight,
    double DurationSeconds, bool EaseOut,
    double FromOpacity, double ToOpacity,
    double FromOffsetX, double ToOffsetX);

// DirectComposition animates a value by evaluating a polynomial in seconds
// since the animation started. These are the two easings the overlay has always
// used, expressed as those coefficients.
//
// A sign error here does not throw - it leaves the badge invisible or parked
// off its resting position, with nothing in the log. Hence the pure helper and
// the test that samples it against the easing functions themselves.
internal static class ClipOverlayAnimationCurve
{
    internal readonly record struct Cubic(float Constant, float Linear, float Quadratic, float Cubed)
    {
        public double Sample(double seconds)
            => Constant + Linear * seconds + Quadratic * seconds * seconds + Cubed * seconds * seconds * seconds;
    }

    // easeOut: from + delta * (3u - 3u^2 + u^3)  -- matches 1 - (1-u)^3
    // easeIn:  from + delta * u^3
    public static Cubic Build(double from, double to, double durationSeconds, bool easeOut)
    {
        if (durationSeconds <= 0) return new Cubic((float)to, 0, 0, 0);
        var delta = to - from;
        var d = durationSeconds;
        return easeOut
            ? new Cubic((float)from, (float)(3 * delta / d), (float)(-3 * delta / (d * d)), (float)(delta / (d * d * d)))
            : new Cubic((float)from, 0, 0, (float)(delta / (d * d * d)));
    }
}

// One native notification surface. HWND, GPU presenter and timer live on its
// dedicated thread; Avalonia's frame clock never participates.
//
// Under a GPU-bound game that thread is the thing that stops being scheduled,
// so as little as possible depends on it running on time: the card is uploaded
// once and the fade and slide are handed to DirectComposition, which DWM drives
// whether or not we are awake. What is left on our own clock is a slow topmost
// reassert. The layered fallback cannot do that and keeps the ticked path.
internal sealed unsafe class NativeClipOverlaySurface : IClipOverlaySurface
{
    private const int TimerId = 1, HideTimerId = 2;
    private const uint WmAppPublish = 0x8001, WmAppDismiss = 0x8002, WmClose = 0x0010, WmDestroy = 0x0002, WmTimer = 0x0113;
    private const int WsExTopmost = 0x00000008, WsExLayered = 0x00080000, WsExTransparent = 0x20, WsExToolWindow = 0x80, WsExNoActivate = 0x08000000, WsExNoRedirectionBitmap = 0x00200000, GwlExStyle = -20;
    private const uint WsPopup = 0x80000000, WdaExcludeFromCapture = 0x11, SwpNoSize = 0x0001, SwpNoMove = 0x0002, SwpNoActivate = 0x0010, SwpShowWindow = 0x0040, SwpFrameChanged = 0x0020;
    private const double EnterMs = 220, ExitMs = 180;
    // 15ms only for the layered fallback, which has to draw every frame itself.
    // The compositor path needs a tick solely to stay above a fullscreen game.
    private const uint TickMs = 15, ReassertMs = 250;
    private const double SlowPublishMs = 250;
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
    private PendingPublish? _pending;
    private ClipOverlayPresentation? _current;
    private ClipOverlayFrame? _frame;
    private INativeClipOverlayPresenter? _presenter;
    private long _pendingDismissal, _motionStarted, _latestGeneration;
    private long _requestedTicks, _queuedTicks, _renderedTicks, _acceptedTicks;
    private nint _window;
    private int _width, _height, _publishCount;
    private Motion _motion;
    private double _motionStartOpacity;
    private Action<ClipOverlayPresentationResult>? _presentationCompletion;
    private MmcssScope _mmcss;
    private bool _mmcssHeld;
    private bool _visible, _disposed, _gpuRecoveryAttempted, _nativePublishLogged, _nativeFailureLogged;

    public NativeClipOverlaySurface(Func<ClipOverlayPresentation, ClipOverlayFrame>? render = null, Func<nint, INativeClipOverlayPresenter>? presenterFactory = null)
    {
        _render = render ?? ClipOverlayCardRenderer.Render;
        _requiresUiThread = render is null;
        _presenterFactory = presenterFactory;
        // Runs at normal priority while idle, which is nearly always. It is
        // raised for the span a notification is actually on screen - see
        // HoldSchedulingWindow.
        _thread = new Thread(Run) { IsBackground = true, Name = "ClypDat clip overlay" };
        _thread.SetApartmentState(ApartmentState.STA);
        _thread.Start();
        _ready.Wait(TimeSpan.FromSeconds(2));
    }

    internal nint WindowHandle => _window;
    internal int PublishCount => Volatile.Read(ref _publishCount);
    internal string PresenterName => _presenter?.Name ?? "unavailable";

    public void Publish(ClipOverlayPresentation presentation, Action<ClipOverlayPresentationResult> completion)
        => Publish(presentation, completion, Stopwatch.GetTimestamp());

    private void Publish(ClipOverlayPresentation presentation, Action<ClipOverlayPresentationResult> completion, long requestedTicks)
    {
        lock (_gate) { if (_disposed) return; }
        if (_requiresUiThread && !Dispatcher.UIThread.CheckAccess())
        {
            // Send is the top of the queue. At the default priority this post
            // sits behind the hover preview's Render-priority work, which runs
            // at 60fps for as long as a clip is hovered.
            Dispatcher.UIThread.Post(() => Publish(presentation, completion, requestedTicks), DispatcherPriority.Send);
            return;
        }
        var queuedTicks = Stopwatch.GetTimestamp();
        ClipOverlayFrame frame;
        try { frame = _render(presentation); }
        catch (Exception error)
        {
            AppLog.Error("Clip overlay card rendering failed", error);
            completion(new ClipOverlayPresentationResult(presentation.Generation, false));
            return;
        }
        var renderedTicks = Stopwatch.GetTimestamp();
        (long Generation, Action<ClipOverlayPresentationResult> Completion)? replaced = null;
        var rejected = false;
        lock (_gate)
        {
            if (_disposed || presentation.Generation <= _pendingDismissal || presentation.Generation <= _latestGeneration) rejected = true;
            else
            {
                replaced = _pending is { } pending ? (pending.Presentation.Generation, pending.Completion) : null;
                _latestGeneration = presentation.Generation;
                _pending = new PendingPublish(presentation, frame, completion, requestedTicks, queuedTicks, renderedTicks);
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
            _ready.Set();
            while (GetMessage(out var message, 0, 0, 0) > 0) { TranslateMessage(ref message); DispatchMessage(ref message); }
        }
        catch (Exception error) { AppLog.Error("Native clip overlay thread failed", error); _ready.Set(); }
        finally { ReleaseSchedulingWindow(); _presenter?.Dispose(); if (_window != 0) Instances.TryRemove(_window, out _); _window = 0; }
    }

    private void CreatePresenter()
    {
        try
        {
            _presenter = _presenterFactory?.Invoke(_window) ?? new DirectCompositionClipOverlayPresenter(_window);
            AppLog.Info($"Clip overlay presenter selected: {_presenter.Name}.");
            ArmTick();
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
        ArmTick();
        AppLog.Info("Clip overlay presenter selected: layered fallback.");
    }

    private void ArmTick() => SetTimer(_window, TimerId, Animating ? ReassertMs : TickMs, 0);

    private bool Animating => _presenter is { AnimatesItself: true };

    private void AcceptPublish()
    {
        PendingPublish? pending;
        lock (_gate) { pending = _pending; _pending = null; if (pending is { } expired && expired.Presentation.Generation <= _pendingDismissal) return; }
        if (pending is not { } update || (_current is { } current && current.Generation > update.Presentation.Generation)) return;
        var sameWorkflow = _visible && _current?.Event.WorkflowId == update.Presentation.Event.WorkflowId;
        // Read before any of the motion state below is overwritten.
        var carried = sameWorkflow ? CurrentOpacity() : 0;
        _current = update.Presentation;
        _frame = update.Frame;
        _width = update.Frame.Width;
        _height = update.Frame.Height;
        _requestedTicks = update.RequestedTicks;
        _queuedTicks = update.QueuedTicks;
        _renderedTicks = update.RenderedTicks;
        _acceptedTicks = Stopwatch.GetTimestamp();
        _nativePublishLogged = _nativeFailureLogged = false;
        _gpuRecoveryAttempted = false;
        SetWindowDisplayAffinity(_window, _current.Event.ExcludeFromCapture ? WdaExcludeFromCapture : 0);
        _presentationCompletion = update.Completion;
        _visible = true;
        HoldSchedulingWindow();

        if (Animating)
        {
            // A frame swap does not disturb a running animation, so a repeat of
            // the workflow already fading in just changes what is being faded.
            var restart = !sameWorkflow || _motion == Motion.Exiting;
            if (restart)
            {
                _motionStartOpacity = carried;
                _motion = Motion.Entering;
                _motionStarted = Stopwatch.GetTimestamp();
                KillTimer(_window, HideTimerId);
            }
            if (Animate(restart, frameChanged: true)) AcknowledgePresentation();
        }
        else if (!sameWorkflow)
        {
            _motion = Motion.Entering;
            _motionStartOpacity = 0;
            _motionStarted = Stopwatch.GetTimestamp();
            Present(0, true);
        }
        else
        {
            if (_motion == Motion.Exiting)
            {
                _motion = Motion.Entering;
                _motionStartOpacity = carried;
                _motionStarted = Stopwatch.GetTimestamp();
            }
            if (Present(carried, true) && carried > 0) AcknowledgePresentation();
        }
        Interlocked.Increment(ref _publishCount);
    }

    private void AcceptDismissal()
    {
        long generation; lock (_gate) generation = _pendingDismissal;
        if (!_visible || _current?.Generation != generation) return;
        _motionStartOpacity = CurrentOpacity();
        _motion = Motion.Exiting;
        _motionStarted = Stopwatch.GetTimestamp();
        if (!Animating) return;
        Animate(applyAnimation: true, frameChanged: false);
        // One wake to tear the window down once the compositor has finished.
        SetTimer(_window, HideTimerId, (uint)ExitMs + 40, 0);
    }

    private void OnTimer(nint timerId)
    {
        if (timerId == HideTimerId) { Hide(); return; }
        if (Animating) { if (_visible) _presenter?.ReassertTopmost(); return; }
        Tick();
    }

    private void Tick()
    {
        if (!_visible || _current is null) return;
        var elapsed = Stopwatch.GetElapsedTime(_motionStarted).TotalMilliseconds;
        if (_motion == Motion.Entering)
        {
            var progress = _motionStartOpacity + (1 - _motionStartOpacity) * EaseOut(Math.Min(1, elapsed / EnterMs));
            if (Present(progress, false) && progress > 0) AcknowledgePresentation();
            if (elapsed >= EnterMs) { _motion = Motion.Still; if (Present(1, false)) AcknowledgePresentation(); }
        }
        else if (_motion == Motion.Exiting)
        {
            Present(_motionStartOpacity * (1 - EaseIn(Math.Min(1, elapsed / ExitMs))), false);
            if (elapsed >= ExitMs) Hide();
        }
        else if (Present(1, false)) AcknowledgePresentation(); // Reassert topmost during full dwell.
    }

    // The compositor owns the opacity once an animation is committed, so it is
    // computed rather than observed. Only the republish path needs it, to pick
    // up a fade wherever it had got to.
    private double CurrentOpacity()
    {
        if (!_visible) return 0;
        var elapsed = Stopwatch.GetElapsedTime(_motionStarted).TotalMilliseconds;
        return _motion switch
        {
            Motion.Entering => _motionStartOpacity + (1 - _motionStartOpacity) * EaseOut(Math.Min(1, elapsed / EnterMs)),
            Motion.Exiting => _motionStartOpacity * (1 - EaseIn(Math.Min(1, elapsed / ExitMs))),
            _ => 1
        };
    }

    private void Hide()
    {
        ReleaseSchedulingWindow();
        KillTimer(_window, HideTimerId);
        _presenter?.Hide();
        _visible = false;
        _motion = Motion.Still;
        _motionStartOpacity = 0;
        _current = null;
        _frame = null;
        _presentationCompletion = null;
    }

    // MMCSS is what gets this thread CPU while a game saturates every core -
    // the capture loops rely on it for the same reason. Held only while a
    // notification is actually on screen: outside that this thread is asleep,
    // and a permanently registered multimedia thread at raised priority is
    // scheduling pressure the machine pays for continuously and gets nothing
    // back for. Measurably so - holding it for the process lifetime was enough
    // to start perturbing unrelated frame-timing tests.
    private void HoldSchedulingWindow()
    {
        if (_mmcssHeld || !OperatingSystem.IsWindows()) return;
        _mmcss = MmcssScope.Capture("clip overlay");
        Thread.CurrentThread.Priority = ThreadPriority.AboveNormal;
        _mmcssHeld = true;
    }

    private void ReleaseSchedulingWindow()
    {
        if (!_mmcssHeld) return;
        _mmcssHeld = false;
        Thread.CurrentThread.Priority = ThreadPriority.Normal;
        _mmcss.Dispose();
        _mmcss = default;
    }

    private void AcknowledgePresentation()
    {
        var completion = Interlocked.Exchange(ref _presentationCompletion, null);
        completion?.Invoke(new ClipOverlayPresentationResult(_current!.Generation, true));
    }

    private ClipOverlayMotionPlan BuildPlan()
    {
        var target = _current!.Event.Target;
        var layout = ClipOverlayLayout.Frame(target, _current.Event.Placement, _width, _height);
        var exiting = _motion == Motion.Exiting;
        var from = _motionStartOpacity;
        var to = exiting ? 0d : 1d;
        var duration = (exiting ? ExitMs : EnterMs) / 1000.0;
        // Offset is affine in opacity: hidden at 0, flush against the edge at 1.
        double Offset(double opacity) => layout.HiddenOffsetX + (layout.RestOffsetX - layout.HiddenOffsetX) * opacity;
        return new ClipOverlayMotionPlan(
            layout.Window.X, layout.Window.Y, layout.Window.Width, layout.Window.Height,
            _width, _height, duration, !exiting, from, to, Offset(from), Offset(to));
    }

    private bool Animate(bool applyAnimation, bool frameChanged)
    {
        if (_current is null || _frame is not { } frame || _presenter is null) return false;
        var plan = BuildPlan();
        try
        {
            _presenter.Animate(frame, in plan, applyAnimation, frameChanged);
            NotePresented();
            return true;
        }
        catch (Exception error)
        {
            if (!RecoverPresenter(error, () => _presenter!.Animate(frame, in plan, true, true))) return false;
            NotePresented();
            return true;
        }
    }

    private bool Present(double progress, bool frameChanged)
    {
        if (_current is null || _frame is not { } frame || _presenter is null) return false;
        var position = ClipOverlayLayout.AnimatedPosition(_current.Event.Target, _current.Event.Placement, _width, _height, progress);
        var destination = new PointNative(position.X, position.Y);
        try
        {
            _presenter.Present(frame, destination, _width, _height, progress, frameChanged);
            if (progress > 0) NotePresented();
            return true;
        }
        catch (Exception error)
        {
            if (!RecoverPresenter(error, () => _presenter!.Present(frame, destination, _width, _height, progress, true))) return false;
            if (progress > 0) NotePresented();
            return true;
        }
    }

    private delegate void Retry();

    private bool RecoverPresenter(Exception error, Retry retry)
    {
        if (_presenter is DirectCompositionClipOverlayPresenter && !_gpuRecoveryAttempted)
        {
            _gpuRecoveryAttempted = true;
            AppLog.Error("Clip overlay DirectComposition presentation failed; rebuilding", error);
            try
            {
                _presenter.Dispose();
                _presenter = new DirectCompositionClipOverlayPresenter(_window);
                retry();
                return true;
            }
            catch (Exception recoveryError)
            {
                AppLog.Error("Clip overlay DirectComposition rebuild failed; using layered fallback", recoveryError);
                UseLayeredPresenter();
                // The layered presenter draws every frame itself, so whatever
                // the compositor was going to animate has to be re-driven from
                // the tick loop that ArmTick just restored.
                _presenter.Present(_frame!, ToPoint(ClipOverlayLayout.AnimatedPosition(_current!.Event.Target, _current.Event.Placement, _width, _height, CurrentOpacity())), _width, _height, CurrentOpacity(), true);
                return true;
            }
        }
        if (!_nativeFailureLogged) AppLog.Error($"Clip overlay native publish failed: id={_current?.Event.WorkflowId}, backend={_presenter?.Name}.", error);
        _nativeFailureLogged = true;
        return false;
    }

    private static PointNative ToPoint(Avalonia.PixelPoint point) => new(point.X, point.Y);

    // Where the badge actually went, in the four hops it can be delayed in.
    // queue is the wait for the Avalonia UI thread, raster is drawing the card
    // on it, post is the hand-off to this thread, and present is the GPU work.
    private void NotePresented()
    {
        if (_nativePublishLogged || _current is null || _presenter is null) return;
        _nativePublishLogged = true;
        var presented = Stopwatch.GetTimestamp();
        var queue = Elapsed(_requestedTicks, _queuedTicks);
        var raster = Elapsed(_queuedTicks, _renderedTicks);
        var post = Elapsed(_renderedTicks, _acceptedTicks);
        var present = Elapsed(_acceptedTicks, presented);
        var total = Elapsed(_requestedTicks, presented);
        var detail = $"id={_current.Event.WorkflowId}, kind={_current.Event.Kind}, backend={_presenter.Name}, monitor={_current.Event.Target.DeviceName}, "
            + $"size={_width}x{_height}, queueMs={queue:F1}, rasterMs={raster:F1}, postMs={post:F1}, presentMs={present:F1}, totalMs={total:F1}";
        AppLog.Info(total > SlowPublishMs
            ? $"Clip overlay native publish was slow: {detail}."
            : $"Clip overlay native publish succeeded: {detail}.");
    }

    private static double Elapsed(long fromTicks, long toTicks)
        => fromTicks <= 0 || toTicks <= fromTicks ? 0 : (toTicks - fromTicks) * 1000.0 / Stopwatch.Frequency;

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
            case WmTimer: instance.OnTimer(wParam); return 0;
            case WmClose: DestroyWindow(window); return 0;
            case WmDestroy: return Destroyed(instance);
            default: return DefWindowProc(window, message, wParam, lParam);
        }
    }
    private static nint Destroyed(NativeClipOverlaySurface instance)
    {
        KillTimer(instance._window, TimerId);
        KillTimer(instance._window, HideTimerId);
        PostQuitMessage(0);
        return 0;
    }

    private readonly record struct PendingPublish(
        ClipOverlayPresentation Presentation,
        ClipOverlayFrame Frame,
        Action<ClipOverlayPresentationResult> Completion,
        long RequestedTicks,
        long QueuedTicks,
        long RenderedTicks);

    internal interface INativeClipOverlayPresenter : IDisposable
    {
        string Name { get; }
        // True when the presenter can be handed a whole motion and run it
        // without the caller waking up per frame.
        bool AnimatesItself { get; }
        void Present(ClipOverlayFrame frame, PointNative destination, int width, int height, double opacity, bool frameChanged);
        void Animate(ClipOverlayFrame frame, in ClipOverlayMotionPlan plan, bool applyAnimation, bool frameChanged);
        void ReassertTopmost();
        void Hide();
    }

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
        private readonly IDCompositionEffectGroup _effect;
        private IDXGISwapChain1? _swapChain;
        private IDXGISwapChain3? _swapChain3;
        private int _width, _height, _windowX, _windowY, _windowWidth, _windowHeight;
        private bool _uploaded;
        public DirectCompositionClipOverlayPresenter(nint window)
        {
            _window = window;
            var levels = new[] { FeatureLevel.Level_11_1, FeatureLevel.Level_11_0, FeatureLevel.Level_10_1 };
            D3D11Api.D3D11CreateDevice(null, DriverType.Hardware, DeviceCreationFlags.BgraSupport, levels, out _device, out _, out _context).CheckError();
            // Per-device only. The process-wide class would reprioritise
            // Avalonia's renderer and everything else this process submits.
            GpuScheduling.TryRaiseDeviceGpuPriority(_device.NativePointer, "presentation", "Clip overlay",
                GpuScheduling.OverlayDevicePriority, "CLYPDAT_GPU_OVERLAY_PRIORITY");
            _dxgiDevice = _device.QueryInterface<IDXGIDevice>();
            using var adapter = _dxgiDevice.GetParent<IDXGIAdapter>();
            _factory = adapter.GetParent<IDXGIFactory2>();
            _composition = DComp.DCompositionCreateDevice<IDCompositionDevice>(_dxgiDevice);
            _composition.CreateTargetForHwnd(window, true, out _target).CheckError();
            _composition.CreateVisual(out _visual).CheckError();
            // Opacity lives on an effect group, not on the visual itself.
            _composition.CreateEffectGroup(out _effect).CheckError();
            _effect.SetOpacity(0f).CheckError();
            _visual.SetEffect(_effect).CheckError();
            _target.SetRoot(_visual).CheckError();
            _composition.Commit().CheckError();
        }
        public string Name => "DirectComposition";
        public bool AnimatesItself => true;

        public void Present(ClipOverlayFrame frame, PointNative destination, int width, int height, double opacity, bool frameChanged)
            => Animate(frame, new ClipOverlayMotionPlan(destination.X, destination.Y, width, height, width, height,
                0, false, opacity, opacity, 0, 0), applyAnimation: true, frameChanged);

        public unsafe void Animate(ClipOverlayFrame frame, in ClipOverlayMotionPlan plan, bool applyAnimation, bool frameChanged)
        {
            MoveWindow(plan);
            EnsureSwapChain(plan.CardWidth, plan.CardHeight);
            if (frameChanged || !_uploaded)
            {
                using var texture = _swapChain!.GetBuffer<ID3D11Texture2D>(_swapChain3!.CurrentBackBufferIndex);
                // Uploaded once, unfaded. The fade is the compositor's job now,
                // so the per-tick CPU pass over every pixel is gone.
                fixed (byte* source = frame.Pixels) _context.UpdateSubresource(texture, 0, null, (nint)source, (uint)(plan.CardWidth * 4), 0);
                _swapChain.Present(0, PresentFlags.None).CheckError();
                _uploaded = true;
            }
            if (applyAnimation) ApplyMotion(plan);
            _composition.Commit().CheckError();
        }

        public void ReassertTopmost()
            => SetWindowPos(_window, HwndTopmost, 0, 0, 0, 0, SwpNoMove | SwpNoSize | SwpNoActivate | SwpShowWindow);

        public void Hide()
        {
            ShowWindow(_window, 0);
            _uploaded = false;
        }

        private void MoveWindow(in ClipOverlayMotionPlan plan)
        {
            if (_windowX == plan.WindowX && _windowY == plan.WindowY && _windowWidth == plan.WindowWidth && _windowHeight == plan.WindowHeight)
            {
                ReassertTopmost();
                return;
            }
            SetWindowPos(_window, HwndTopmost, plan.WindowX, plan.WindowY, plan.WindowWidth, plan.WindowHeight, SwpNoActivate | SwpShowWindow);
            _windowX = plan.WindowX; _windowY = plan.WindowY; _windowWidth = plan.WindowWidth; _windowHeight = plan.WindowHeight;
        }

        private void ApplyMotion(in ClipOverlayMotionPlan plan)
        {
            if (plan.DurationSeconds <= 0)
            {
                _effect.SetOpacity((float)plan.ToOpacity).CheckError();
                _visual.SetOffsetX((float)plan.ToOffsetX).CheckError();
                return;
            }
            Apply(plan.FromOpacity, plan.ToOpacity, plan, animation => _effect.SetOpacity(animation).CheckError(),
                value => _effect.SetOpacity((float)value).CheckError());
            Apply(plan.FromOffsetX, plan.ToOffsetX, plan, animation => _visual.SetOffsetX(animation).CheckError(),
                value => _visual.SetOffsetX((float)value).CheckError());
        }

        private void Apply(double from, double to, in ClipOverlayMotionPlan plan, Action<IDCompositionAnimation> animate, Action<double> set)
        {
            if (Math.Abs(to - from) < 0.0005) { set(to); return; }
            _composition.CreateAnimation(out var animation).CheckError();
            using (animation)
            {
                var curve = ClipOverlayAnimationCurve.Build(from, to, plan.DurationSeconds, plan.EaseOut);
                animation.AddCubic(0, curve.Constant, curve.Linear, curve.Quadratic, curve.Cubed).CheckError();
                animation.End(plan.DurationSeconds, (float)to).CheckError();
                animate(animation);
            }
        }

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
            _uploaded = false;
        }
        public void Dispose()
        {
            _visual.SetContent(null); _swapChain3?.Dispose(); _swapChain?.Dispose(); _effect.Dispose(); _visual.Dispose(); _target.Dispose(); _composition.Dispose(); _factory.Dispose(); _dxgiDevice.Dispose(); _context.Dispose(); _device.Dispose();
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
        // UpdateLayeredWindow has to be called for every frame of a fade, so
        // this presenter stays on the tick loop.
        public bool AnimatesItself => false;
        public void Present(ClipOverlayFrame frame, PointNative destination, int width, int height, double opacity, bool frameChanged)
        {
            if (frameChanged || width != _width || height != _height) CreateBitmap(frame);
            var source = new PointNative(0, 0); var size = new SizeNative(width, height);
            var blend = new BlendFunction(0, 0, (byte)Math.Clamp((int)Math.Round(255 * opacity), 0, 255), 1);
            if (!UpdateLayeredWindow(_window, 0, ref destination, ref size, _memoryDc, ref source, 0, ref blend, UlwAlpha)) throw new InvalidOperationException($"UpdateLayeredWindow failed ({Marshal.GetLastWin32Error()}).");
            SetWindowPos(_window, HwndTopmost, destination.X, destination.Y, width, height, SwpNoActivate | SwpShowWindow);
        }
        public void Animate(ClipOverlayFrame frame, in ClipOverlayMotionPlan plan, bool applyAnimation, bool frameChanged)
            => Present(frame, new PointNative(plan.WindowX + (int)Math.Round(plan.ToOffsetX), plan.WindowY),
                plan.CardWidth, plan.CardHeight, plan.ToOpacity, frameChanged);
        public void ReassertTopmost()
            => SetWindowPos(_window, HwndTopmost, 0, 0, 0, 0, SwpNoMove | SwpNoSize | SwpNoActivate | SwpShowWindow);
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
