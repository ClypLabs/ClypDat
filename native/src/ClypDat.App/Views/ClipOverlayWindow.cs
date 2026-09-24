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

// What the presenter is asked to confirm about the window it just presented.
internal readonly record struct ClipOverlayVerification(nint Window, ClipOverlayTarget Target, Avalonia.PixelRect ExpectedWindow, long PresentedTicks);

// The Win32 facts that say whether the overlay window is actually on screen
// where it was sent. Shared by the real presenters.
internal static class ClipOverlayWindowCheck
{
    private const int DwmwaCloaked = 14;
    private const uint GwHwndNext = 2;

    // Why the window is not visibly where it should be, or null when it is.
    public static string? Inspect(in ClipOverlayVerification check)
    {
        var window = check.Window;
        if (window == 0 || !IsWindow(window)) return "window-destroyed";
        if (!IsWindowVisible(window)) return "window-not-visible";
        if (DwmGetWindowAttribute(window, DwmwaCloaked, out var cloaked, sizeof(uint)) == 0 && cloaked != 0) return $"window-cloaked:{cloaked}";
        if (!GetWindowRect(window, out var rect)) return "window-rect-unavailable";
        var expected = check.ExpectedWindow;
        if (rect.Right - rect.Left != expected.Width || rect.Bottom - rect.Top != expected.Height) return "window-size-mismatch";
        if (rect.Left != expected.X || rect.Top != expected.Y) return "window-position-mismatch";
        var target = check.Target;
        if (!string.IsNullOrEmpty(target.DeviceName) &&
            !string.Equals(ClipOverlayTargeting.MonitorDeviceNameOf(window), target.DeviceName, StringComparison.OrdinalIgnoreCase))
            return "wrong-monitor";
        if (IsBelow(window, target.Window)) return "below-target-window";
        return null;
    }

    // True when `window` sits under `other` in the z-order. Unknown or hidden
    // windows are never above anything.
    public static bool IsBelow(nint window, nint other)
    {
        if (window == 0 || other == 0 || !IsWindow(other) || !IsWindowVisible(other)) return false;
        for (var current = GetTopWindow(0); current != 0; current = GetWindow(current, GwHwndNext))
        {
            if (current == window) return false;
            if (current == other) return true;
        }
        return false;
    }

    // Shell's view of the foreground app: 3 is a Direct3D exclusive-fullscreen
    // app, which may keep any other window off the display it owns.
    public static bool ExclusiveFullscreenOn(ClipOverlayTarget target)
    {
        if (!OperatingSystem.IsWindows()) return false;
        try
        {
            if (SHQueryUserNotificationState(out var state) != 0 || state != 3) return false;
        }
        catch (EntryPointNotFoundException) { return false; }
        var foreground = GetForegroundWindow();
        return foreground != 0 && (string.IsNullOrEmpty(target.DeviceName) ||
            string.Equals(ClipOverlayTargeting.MonitorDeviceNameOf(foreground), target.DeviceName, StringComparison.OrdinalIgnoreCase));
    }

    public static string Describe(nint window)
    {
        if (window == 0 || !IsWindow(window)) return "window=destroyed";
        GetWindowRect(window, out var rect);
        DwmGetWindowAttribute(window, DwmwaCloaked, out var cloaked, sizeof(uint));
        return $"visible={IsWindowVisible(window)}, rect={rect.Left},{rect.Top},{rect.Right - rect.Left}x{rect.Bottom - rect.Top}, cloaked={cloaked}, monitor={ClipOverlayTargeting.MonitorDeviceNameOf(window)}";
    }

    [StructLayout(LayoutKind.Sequential)] private struct Rect { public int Left, Top, Right, Bottom; }
    [DllImport("user32.dll")] private static extern bool IsWindow(nint window);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(nint window);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(nint window, out Rect rect);
    [DllImport("user32.dll")] private static extern nint GetTopWindow(nint window);
    [DllImport("user32.dll")] private static extern nint GetWindow(nint window, uint command);
    [DllImport("user32.dll")] private static extern nint GetForegroundWindow();
    [DllImport("dwmapi.dll")] private static extern int DwmGetWindowAttribute(nint window, int attribute, out uint value, int size);
    [DllImport("shell32.dll")] private static extern int SHQueryUserNotificationState(out int state);
}

// One native notification surface. HWND, GPU presenter and timer live on its
// dedicated thread; Avalonia's frame clock never participates.
//
// Under a GPU-bound game that thread is the thing that stops being scheduled,
// so as little as possible depends on it running on time: the card is uploaded
// once and the fade and slide are handed to DirectComposition, which DWM drives
// whether or not we are awake. What is left on our own clock is a slow topmost
// reassert while a card is up, and nothing at all while none is. The layered
// fallback cannot animate itself and keeps the ticked path.
//
// A presentation is reported as presented only after the window was checked on
// screen: visible, uncloaked, at its rectangle on its monitor, above the game
// window it was sent for, and (DirectComposition) composed by DWM. A failed
// check reasserts and re-presents once, then rebuilds DirectComposition once,
// then falls back to the layered presenter, and then reports the failure.
internal sealed unsafe class NativeClipOverlaySurface : IClipOverlaySurface
{
    private const int TimerId = 1, HideTimerId = 2, VerifyTimerId = 3;
    private const uint WmAppPublish = 0x8001, WmAppDismiss = 0x8002, WmAppLoseWindow = 0x8003, WmClose = 0x0010, WmDestroy = 0x0002, WmTimer = 0x0113;
    private const int WsExTopmost = 0x00000008, WsExLayered = 0x00080000, WsExTransparent = 0x20, WsExToolWindow = 0x80, WsExNoActivate = 0x08000000, WsExNoRedirectionBitmap = 0x00200000, GwlExStyle = -20;
    private const uint WsPopup = 0x80000000, WdaExcludeFromCapture = 0x11, SwpNoSize = 0x0001, SwpNoMove = 0x0002, SwpNoActivate = 0x0010, SwpShowWindow = 0x0040, SwpFrameChanged = 0x0020;
    private const double EnterMs = 220, ExitMs = 180;
    // 15ms only for the layered fallback, which has to draw every frame itself.
    // The compositor path needs a tick solely to stay above a fullscreen game.
    private const uint TickMs = 15, ReassertMs = 250;
    private const double SlowPublishMs = 250;
    // Verification runs this long after the first present, then after each
    // recovery step: long enough for DWM to compose the change, short enough
    // that a failure is caught while the card would still be fading in.
    private const uint DefaultVerifyDelayMs = 120;
    private const int MaximumRecoverySteps = 3;
    private static readonly nint HwndTopmost = new(-1);
    private static readonly ConcurrentDictionary<nint, NativeClipOverlaySurface> Instances = new();
    private static readonly WindowProc SharedWindowProc = WindowProcedure;
    private static readonly WinEventProc SharedReorderProc = Reordered;
    private const uint EventObjectReorder = 0x8004, WinEventOutOfContext = 0;
    private static int _classRegistered;
    private readonly Func<ClipOverlayPresentation, ClipOverlayFrame> _render;
    private readonly bool _requiresUiThread;
    private readonly Func<nint, INativeClipOverlayPresenter>? _presenterFactory, _layeredFactory;
    private readonly ClipOverlayCounters _counters;
    private readonly uint _verifyDelayMs;
    private readonly object _gate = new();
    private readonly Thread _thread;
    private readonly ManualResetEventSlim _ready = new();
    private PendingPublish? _pending;
    private ClipOverlayPresentation? _current;
    private ClipOverlayFrame? _frame;
    private INativeClipOverlayPresenter? _presenter;
    private long _pendingDismissal, _motionStarted, _latestGeneration;
    private long _requestedTicks, _queuedTicks, _renderedTicks, _acceptedTicks, _presentedTicks;
    private nint _window;
    private int _width, _height, _publishCount, _renderCount, _verifyStep;
    private Avalonia.PixelRect _expectedWindow;
    private Motion _motion;
    private double _motionStartOpacity;
    private Action<ClipOverlayPresentationResult>? _presentationCompletion;
    private MmcssScope _mmcss;
    private nint _reorderHook;
    private bool _mmcssHeld, _tickArmed;
    private bool _visible, _disposed, _windowLost, _gpuRecoveryAttempted, _verifying, _presentFailureLogged;
    private string _affinity = "unset", _lastMonitor = string.Empty;
    private readonly List<string> _recovery = new();

    public NativeClipOverlaySurface(
        Func<ClipOverlayPresentation, ClipOverlayFrame>? render = null,
        Func<nint, INativeClipOverlayPresenter>? presenterFactory = null,
        Func<nint, INativeClipOverlayPresenter>? layeredFactory = null,
        ClipOverlayCounters? counters = null,
        uint verifyDelayMs = DefaultVerifyDelayMs)
    {
        _verifyDelayMs = Math.Max(1, verifyDelayMs);
        _render = render ?? ClipOverlayCardRenderer.Render;
        _requiresUiThread = render is null;
        _presenterFactory = presenterFactory;
        _layeredFactory = layeredFactory;
        _counters = counters ?? ClipOverlayCounters.Shared;
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
    internal int RenderCount => Volatile.Read(ref _renderCount);
    internal string PresenterName => _presenter?.Name ?? "unavailable";
    internal bool TimerArmed => Volatile.Read(ref _tickArmed);

    public void Publish(ClipOverlayPresentation presentation, Action<ClipOverlayPresentationResult> completion)
        => Publish(presentation, Once(completion), Stopwatch.GetTimestamp());

    private void Publish(ClipOverlayPresentation presentation, Action<ClipOverlayPresentationResult> completion, long requestedTicks)
    {
        var generation = presentation.Generation;
        lock (_gate) { if (_disposed) { completion(Failed(generation, "surface-disposed")); return; } }
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
        try { frame = _render(presentation); Interlocked.Increment(ref _renderCount); }
        catch (Exception error)
        {
            AppLog.Error($"Clip overlay card rendering failed: id={presentation.Event.WorkflowId}, generation={generation}", error);
            completion(Failed(generation, $"render-failed:{error.GetType().Name}"));
            return;
        }
        var renderedTicks = Stopwatch.GetTimestamp();
        PendingPublish? replaced = null;
        string? rejected = null;
        lock (_gate)
        {
            if (_disposed) rejected = "surface-disposed";
            else if (generation <= _pendingDismissal) rejected = "dismissed-before-accept";
            else if (generation <= _latestGeneration) rejected = "stale-generation";
            else
            {
                replaced = _pending;
                _latestGeneration = generation;
                _pending = new PendingPublish(presentation, frame, completion, requestedTicks, queuedTicks, renderedTicks);
            }
        }
        if (replaced is { } superseded) superseded.Completion(Failed(superseded.Presentation.Generation, "superseded-before-accept"));
        if (rejected is not null) { completion(Failed(generation, rejected)); return; }
        var window = _window;
        string? failure = window == 0 ? "window-unavailable" : !PostMessage(window, WmAppPublish, 0, 0) ? $"post-message-failed:{Marshal.GetLastWin32Error()}" : null;
        if (failure is null) return;
        // Take it back so it cannot be presented later without its completion.
        lock (_gate) { if (_pending?.Presentation.Generation == generation) _pending = null; else return; }
        completion(Failed(generation, failure));
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

    // Test hook: destroys the window out from under the surface, as a crash in
    // a shell extension or an errant DestroyWindow would.
    internal void LoseWindowForTest() { if (_window != 0) PostMessage(_window, WmAppLoseWindow, 0, 0); }

    private static ClipOverlayPresentationResult Failed(long generation, string reason) => new(generation, false, reason);

    private static Action<ClipOverlayPresentationResult> Once(Action<ClipOverlayPresentationResult> completion)
    {
        var called = 0;
        return result => { if (Interlocked.Exchange(ref called, 1) == 0) completion(result); };
    }

    private void Run()
    {
        try
        {
            RegisterWindowClass();
            CreateWindow();
            _ready.Set();
            while (GetMessage(out var message, 0, 0, 0) > 0)
            {
                TranslateMessage(ref message);
                DispatchMessage(ref message);
                if (_windowLost && !_disposed) RecreateWindow();
            }
        }
        catch (Exception error) { AppLog.Error("Native clip overlay thread failed", error); _ready.Set(); }
        finally
        {
            DisarmTick();
            ReleaseSchedulingWindow();
            _presenter?.Dispose();
            if (_window != 0) Instances.TryRemove(_window, out _);
            _window = 0;
            // Nothing leaves this thread without its completion.
            var completion = Interlocked.Exchange(ref _presentationCompletion, null);
            if (completion is not null && _current is { } current) completion(Failed(current.Generation, "surface-thread-ended"));
            PendingPublish? pending; lock (_gate) { pending = _pending; _pending = null; }
            pending?.Completion(Failed(pending.Value.Presentation.Generation, "surface-thread-ended"));
        }
    }

    private void CreateWindow()
    {
        _window = CreateWindowEx(WsExTopmost | WsExNoRedirectionBitmap | WsExTransparent | WsExToolWindow | WsExNoActivate, ClassName, string.Empty, WsPopup, -32000, -32000, 1, 1, 0, 0, GetModuleHandle(null), 0);
        if (_window == 0) throw new InvalidOperationException($"Could not create clip overlay window ({Marshal.GetLastWin32Error()}).");
        Instances[_window] = this;
        CreatePresenter();
    }

    // The window went away without Dispose: make a new one on this thread and
    // put back whatever was on screen or waiting.
    private void RecreateWindow()
    {
        _windowLost = false;
        var lost = _window;
        Instances.TryRemove(lost, out _);
        _presenter?.Dispose();
        _presenter = null;
        _tickArmed = false;
        try { CreateWindow(); }
        catch (Exception error)
        {
            AppLog.Error("Clip overlay window could not be recreated", error);
            _window = 0;
            if (_visible) CompletePresentation(false, "window-recreate-failed");
            return;
        }
        _counters.WindowRecreated();
        AppLog.Info($"Clip overlay window recreated: lost={lost}, window={_window}, presenter={_presenter?.Name}.");
        if (_visible && _current is not null)
        {
            _motion = Motion.Still;
            SetAffinity();
            ArmTick();
            if (!Present(1, true)) { CompletePresentation(false, "present-failed-after-window-recreate"); Hide(); }
            else if (_presentationCompletion is not null) { _verifying = false; KillTimer(_window, VerifyTimerId); BeginVerification(); }
        }
        bool pending; lock (_gate) pending = _pending is not null;
        if (pending) PostMessage(_window, WmAppPublish, 0, 0);
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
        if (!SetWindowPos(_window, HwndTopmost, 0, 0, 0, 0, SwpNoSize | SwpNoMove | SwpNoActivate | SwpFrameChanged))
            AppLog.Info($"Clip overlay layered style change SetWindowPos failed ({Marshal.GetLastWin32Error()}).");
        _presenter = _layeredFactory?.Invoke(_window) ?? new LayeredClipOverlayPresenter(_window);
        _counters.LayeredFallback();
        _tickArmed = false;
        ArmTick();
        AppLog.Info("Clip overlay presenter selected: layered fallback.");
    }

    // Only while a card is up: an idle overlay thread never wakes. While it is
    // up, a z-order change anywhere is heard at once (out of context, on this
    // thread's message loop), so a game that raises itself is answered when it
    // does rather than on the next reassert tick.
    private void ArmTick()
    {
        if (!_visible || _tickArmed) return;
        SetTimer(_window, TimerId, Animating ? ReassertMs : TickMs, 0);
        _tickArmed = true;
        if (_reorderHook == 0)
        {
            _reorderHook = SetWinEventHook(EventObjectReorder, EventObjectReorder, 0, SharedReorderProc, 0, 0, WinEventOutOfContext);
            if (_reorderHook == 0) AppLog.Debug("Clip overlay z-order hook unavailable; relying on the reassert tick.");
        }
    }

    private void DisarmTick()
    {
        if (_reorderHook != 0) { UnhookWinEvent(_reorderHook); _reorderHook = 0; }
        if (!_tickArmed) return;
        KillTimer(_window, TimerId);
        _tickArmed = false;
    }

    private static void Reordered(nint hook, uint eventType, nint window, int objectId, int childId, uint thread, uint time)
    {
        foreach (var instance in Instances.Values)
            if (instance._reorderHook == hook) { instance.OnReordered(); return; }
    }

    // Only a game that actually got above the card is answered; the card's
    // own reassert reorders nothing it needs to react to.
    private void OnReordered()
    {
        if (!_visible || _current is not { } current || current.Event.Target.Window == 0) return;
        if (ClipOverlayWindowCheck.IsBelow(_window, current.Event.Target.Window)) ReassertTopmost();
    }

    private bool Animating => _presenter is { AnimatesItself: true };

    private void AcceptPublish()
    {
        PendingPublish? pending;
        lock (_gate) { pending = _pending; _pending = null; }
        if (pending is not { } update) return;
        long dismissed; lock (_gate) dismissed = _pendingDismissal;
        if (update.Presentation.Generation <= dismissed) { update.Completion(Failed(update.Presentation.Generation, "dismissed-before-accept")); return; }
        if (_current is { } newer && newer.Generation > update.Presentation.Generation) { update.Completion(Failed(update.Presentation.Generation, "stale-generation")); return; }
        var sameWorkflow = _visible && _current?.Event.WorkflowId == update.Presentation.Event.WorkflowId;
        // The card on screen that was never confirmed is replaced before it
        // could be; it gets its answer now rather than never.
        if (_presentationCompletion is not null && _current is { } replaced)
            CompletePresentation(false, sameWorkflow ? "superseded-by-stage" : "superseded-before-verification");
        // Read before any of the motion state below is overwritten.
        var carried = sameWorkflow ? CurrentOpacity() : 0;
        if (!string.IsNullOrEmpty(_lastMonitor) && !string.Equals(_lastMonitor, update.Presentation.Event.Target.DeviceName, StringComparison.OrdinalIgnoreCase))
            _counters.Retargeted();
        _current = update.Presentation;
        _lastMonitor = _current.Event.Target.DeviceName;
        _frame = update.Frame;
        _width = update.Frame.Width;
        _height = update.Frame.Height;
        _requestedTicks = update.RequestedTicks;
        _queuedTicks = update.QueuedTicks;
        _renderedTicks = update.RenderedTicks;
        _acceptedTicks = Stopwatch.GetTimestamp();
        _presentedTicks = 0;
        _presentFailureLogged = false;
        _gpuRecoveryAttempted = false;
        _verifying = false;
        _verifyStep = 0;
        _recovery.Clear();
        KillTimer(_window, VerifyTimerId);
        SetAffinity();
        _presentationCompletion = update.Completion;
        _visible = true;
        HoldSchedulingWindow();
        ArmTick();
        AppLog.Debug($"Clip overlay accepted: id={_current.Event.WorkflowId}, generation={_current.Generation}, kind={_current.Event.Kind}, sameWorkflow={sameWorkflow}, backend={_presenter?.Name}, monitor={_current.Event.Target.DeviceName}, affinity={_affinity}.");

        bool presented;
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
            presented = Animate(restart, frameChanged: true);
            if (presented) BeginVerification();
        }
        else if (!sameWorkflow)
        {
            _motion = Motion.Entering;
            _motionStartOpacity = 0;
            _motionStarted = Stopwatch.GetTimestamp();
            presented = Present(0, true);
        }
        else
        {
            if (_motion == Motion.Exiting)
            {
                _motion = Motion.Entering;
                _motionStartOpacity = carried;
                _motionStarted = Stopwatch.GetTimestamp();
                KillTimer(_window, HideTimerId);
            }
            presented = Present(carried, true);
            if (presented && carried > 0) BeginVerification();
        }
        Interlocked.Increment(ref _publishCount);
        if (!presented)
        {
            CompletePresentation(false, $"present-failed:{_presenter?.Name ?? "no-presenter"}");
            Hide();
        }
    }

    // Exclusion from capture is best effort: a refusal is reported, and the
    // badge still shows on the display.
    private void SetAffinity()
    {
        if (_current is null) return;
        var exclude = _current.Event.ExcludeFromCapture;
        if (SetWindowDisplayAffinity(_window, exclude ? WdaExcludeFromCapture : 0)) _affinity = exclude ? "excluded" : "included";
        else
        {
            _affinity = $"failed:{Marshal.GetLastWin32Error()}";
            AppLog.Info($"Clip overlay SetWindowDisplayAffinity failed: id={_current.Event.WorkflowId}, exclude={exclude}, error={_affinity}; the notification stays visible.");
        }
    }

    private void AcceptDismissal()
    {
        long generation; lock (_gate) generation = _pendingDismissal;
        if (!_visible || _current?.Generation != generation) return;
        // A presentation dismissed before it was confirmed never gets confirmed.
        if (_presentationCompletion is not null) CompletePresentation(false, "dismissed-before-verification");
        KillTimer(_window, VerifyTimerId);
        _verifying = false;
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
        if (timerId == VerifyTimerId) { Verify(); return; }
        if (Animating)
        {
            if (!_visible || _current is null) return;
            // A presenter whose device was lost mid-dwell is rebuilt now,
            // not discovered at the next notification.
            if (_presenter is { Healthy: false })
            {
                AppLog.Info($"Clip overlay presenter lost its device while visible: id={_current.Event.WorkflowId}, backend={_presenter.Name}.");
                RecoverVisible("device-lost");
                return;
            }
            ReassertTopmost();
            return;
        }
        Tick();
    }

    // Keeps the badge above a game that raises itself. Counted when the game
    // had actually got on top.
    private void ReassertTopmost()
    {
        if (_current is null || _presenter is null) return;
        var below = ClipOverlayWindowCheck.IsBelow(_window, _current.Event.Target.Window);
        if (below) _counters.TopmostRecovered();
        _presenter.ReassertTopmost();
        if (!below) return;
        AppLog.Debug($"Clip overlay topmost reasserted over target window: id={_current.Event.WorkflowId}, target={_current.Event.Target.Window}, aboveNow={!ClipOverlayWindowCheck.IsBelow(_window, _current.Event.Target.Window)}.");
    }

    private void Tick()
    {
        if (!_visible || _current is null) return;
        var elapsed = Stopwatch.GetElapsedTime(_motionStarted).TotalMilliseconds;
        if (_motion == Motion.Entering)
        {
            var progress = _motionStartOpacity + (1 - _motionStartOpacity) * EaseOut(Math.Min(1, elapsed / EnterMs));
            if (Present(progress, false) && progress > 0) BeginVerification();
            if (elapsed >= EnterMs) { _motion = Motion.Still; if (Present(1, false)) BeginVerification(); }
        }
        else if (_motion == Motion.Exiting)
        {
            Present(_motionStartOpacity * (1 - EaseIn(Math.Min(1, elapsed / ExitMs))), false);
            if (elapsed >= ExitMs) Hide();
        }
        else if (Present(1, false)) BeginVerification(); // Reassert topmost during full dwell.
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
        if (_presentationCompletion is not null) CompletePresentation(false, "hidden-before-verification");
        ReleaseSchedulingWindow();
        KillTimer(_window, HideTimerId);
        KillTimer(_window, VerifyTimerId);
        DisarmTick();
        _presenter?.Hide();
        _visible = false;
        _verifying = false;
        _motion = Motion.Still;
        _motionStartOpacity = 0;
        _current = null;
        _frame = null;
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

    // The first frame is on its way; confirm it actually reached the screen
    // shortly, rather than trusting the call that sent it.
    private void BeginVerification()
    {
        if (_verifying || _presentationCompletion is null || _current is null) return;
        _verifying = true;
        if (_presentedTicks == 0) _presentedTicks = Stopwatch.GetTimestamp();
        SetTimer(_window, VerifyTimerId, _verifyDelayMs, 0);
    }

    // Bounded: at most MaximumRecoverySteps recoveries, each followed by one
    // more check, then an explicit failure.
    private void Verify()
    {
        KillTimer(_window, VerifyTimerId);
        if (!_visible || _current is null || _presentationCompletion is null || _presenter is null) return;
        var check = new ClipOverlayVerification(_window, _current.Event.Target, _expectedWindow, _presentedTicks);
        string? problem;
        try { problem = _presenter.Verify(in check); }
        catch (Exception error) { problem = $"verify-threw:{error.GetType().Name}"; }
        if (problem is null)
        {
            var exclusive = ClipOverlayWindowCheck.ExclusiveFullscreenOn(_current.Event.Target);
            CompletePresentation(true, null, exclusive ? "exclusive-fullscreen-may-hide-overlay" : null, confirmed: !exclusive);
            return;
        }
        _recovery.Add(problem);
        AppLog.Info($"Clip overlay verification failed: id={_current.Event.WorkflowId}, generation={_current.Generation}, step={_verifyStep}, problem={problem}, backend={_presenter.Name}, {ClipOverlayWindowCheck.Describe(_window)}.");
        if (_verifyStep >= MaximumRecoverySteps)
        {
            CompletePresentation(false, $"{problem}-after-recovery");
            Hide();
            return;
        }
        _counters.VerificationRecovered();
        var step = _verifyStep++;
        bool presented;
        if (step == 0)
        {
            _recovery.Add("reassert");
            ReassertTopmost();
            presented = Present(1, true);
        }
        else if (step == 1 && _presenter is { IsLayered: false })
        {
            _recovery.Add("rebuild");
            presented = RebuildPresenter() && Present(1, true);
        }
        else if (_presenter is { IsLayered: false })
        {
            _recovery.Add("layered");
            UseLayeredPresenter();
            presented = Present(1, true);
        }
        else
        {
            _recovery.Add("reassert");
            ReassertTopmost();
            presented = Present(1, true);
        }
        _motion = Motion.Still;
        if (presented) SetTimer(_window, VerifyTimerId, _verifyDelayMs, 0);
        else { CompletePresentation(false, $"{problem}-then-present-failed"); Hide(); }
    }

    // Replaces the card that is up after its presenter failed while visible.
    private void RecoverVisible(string reason)
    {
        _recovery.Add(reason);
        _motion = Motion.Still;
        var rebuilt = !_gpuRecoveryAttempted && RebuildPresenter();
        _gpuRecoveryAttempted = true;
        if (!rebuilt) UseLayeredPresenter();
        if (!Present(1, true))
        {
            AppLog.Info($"Clip overlay could not re-present after {reason}: id={_current?.Event.WorkflowId}.");
            if (_presentationCompletion is not null) CompletePresentation(false, $"{reason}-unrecoverable");
            Hide();
        }
    }

    private bool RebuildPresenter()
    {
        try
        {
            _presenter?.Dispose();
            _presenter = _presenterFactory?.Invoke(_window) ?? new DirectCompositionClipOverlayPresenter(_window);
            _counters.DirectCompositionRebuilt();
            AppLog.Info($"Clip overlay presenter rebuilt: {_presenter.Name}.");
            return true;
        }
        catch (Exception error)
        {
            AppLog.Error("Clip overlay DirectComposition rebuild failed; using layered fallback", error);
            UseLayeredPresenter();
            return true;
        }
    }

    private void CompletePresentation(bool presented, string? reason, string? note = null, bool confirmed = true)
    {
        var completion = Interlocked.Exchange(ref _presentationCompletion, null);
        _verifying = false;
        if (completion is null || _current is null) return;
        var now = Stopwatch.GetTimestamp();
        var report = new ClipOverlayPresentationReport(
            _presenter?.Name ?? "unavailable",
            ClipOverlayTargeting.MonitorDeviceNameOf(_window) is { Length: > 0 } monitor ? monitor : _current.Event.Target.DeviceName,
            Elapsed(_requestedTicks, _queuedTicks), Elapsed(_queuedTicks, _renderedTicks), Elapsed(_renderedTicks, _acceptedTicks),
            Elapsed(_acceptedTicks, _presentedTicks), Elapsed(_presentedTicks, now), Elapsed(_requestedTicks, _presentedTicks > 0 ? _presentedTicks : now),
            _affinity, _recovery.Count == 0 ? "none" : string.Join('+', _recovery), confirmed, note);
        if (presented && report.TotalMs > SlowPublishMs)
            AppLog.Info($"Clip overlay native publish was slow: id={_current.Event.WorkflowId}, totalMs={report.TotalMs:F1}, queueMs={report.QueueMs:F1}, rasterMs={report.RasterMs:F1}, postMs={report.PostMs:F1}, presentMs={report.PresentMs:F1}.");
        completion(new ClipOverlayPresentationResult(_current.Generation, presented, reason, report));
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
        _expectedWindow = new Avalonia.PixelRect(plan.WindowX, plan.WindowY, plan.WindowWidth, plan.WindowHeight);
        try
        {
            _presenter.Animate(frame, in plan, applyAnimation, frameChanged);
            return true;
        }
        catch (Exception error)
        {
            return RecoverPresenter(error, () => _presenter!.Animate(frame, in plan, true, true));
        }
    }

    private bool Present(double progress, bool frameChanged)
    {
        if (_current is null || _frame is not { } frame || _presenter is null) return false;
        var position = ClipOverlayLayout.AnimatedPosition(_current.Event.Target, _current.Event.Placement, _width, _height, progress);
        var destination = new PointNative(position.X, position.Y);
        _expectedWindow = new Avalonia.PixelRect(position.X, position.Y, _width, _height);
        try
        {
            _presenter.Present(frame, destination, _width, _height, progress, frameChanged);
            return true;
        }
        catch (Exception error)
        {
            return RecoverPresenter(error, () => _presenter!.Present(frame, destination, _width, _height, progress, true));
        }
    }

    private delegate void Retry();

    // One DirectComposition rebuild per notification, then the layered
    // presenter, then an explicit failure.
    private bool RecoverPresenter(Exception error, Retry retry)
    {
        if (_presenter is { IsLayered: false } && !_gpuRecoveryAttempted)
        {
            _gpuRecoveryAttempted = true;
            _recovery.Add("rebuild");
            AppLog.Error($"Clip overlay {_presenter.Name} presentation failed; rebuilding", error);
            try
            {
                _presenter.Dispose();
                _presenter = _presenterFactory?.Invoke(_window) ?? new DirectCompositionClipOverlayPresenter(_window);
                _counters.DirectCompositionRebuilt();
                retry();
                return true;
            }
            catch (Exception recoveryError)
            {
                AppLog.Error("Clip overlay DirectComposition rebuild failed; using layered fallback", recoveryError);
                _recovery.Add("layered");
                UseLayeredPresenter();
                // The layered presenter draws every frame itself, so whatever
                // the compositor was going to animate has to be re-driven from
                // the tick loop that ArmTick just restored.
                try
                {
                    var opacity = CurrentOpacity();
                    var position = ClipOverlayLayout.AnimatedPosition(_current!.Event.Target, _current.Event.Placement, _width, _height, opacity);
                    _expectedWindow = new Avalonia.PixelRect(position.X, position.Y, _width, _height);
                    _presenter!.Present(_frame!, ToPoint(position), _width, _height, opacity, true);
                    return true;
                }
                catch (Exception layeredError) { error = layeredError; }
            }
        }
        if (!_presentFailureLogged) AppLog.Error($"Clip overlay native present failed: id={_current?.Event.WorkflowId}, backend={_presenter?.Name}.", error);
        _presentFailureLogged = true;
        return false;
    }

    private static PointNative ToPoint(Avalonia.PixelPoint point) => new(point.X, point.Y);

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
            // Only Dispose closes the overlay; a stray WM_CLOSE from another
            // process must not take the notification surface down.
            case WmClose:
                if (instance._disposed) DestroyWindow(window);
                else AppLog.Info("Clip overlay ignored WM_CLOSE it did not send.");
                return 0;
            case WmAppLoseWindow: DestroyWindow(window); return 0;
            case WmDestroy: return Destroyed(instance);
            default: return DefWindowProc(window, message, wParam, lParam);
        }
    }
    private static nint Destroyed(NativeClipOverlaySurface instance)
    {
        KillTimer(instance._window, TimerId);
        KillTimer(instance._window, HideTimerId);
        KillTimer(instance._window, VerifyTimerId);
        instance._tickArmed = false;
        if (instance._disposed) PostQuitMessage(0);
        else
        {
            AppLog.Info($"Clip overlay window destroyed unexpectedly: window={instance._window}; recreating.");
            instance._windowLost = true;
        }
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
        // The last-resort presenter, which has nothing to fall back to.
        bool IsLayered => false;
        // False once the presenter's device has been lost.
        bool Healthy => true;
        void Present(ClipOverlayFrame frame, PointNative destination, int width, int height, double opacity, bool frameChanged);
        void Animate(ClipOverlayFrame frame, in ClipOverlayMotionPlan plan, bool applyAnimation, bool frameChanged);
        void ReassertTopmost();
        void Hide();
        // Why the presented card is not actually on screen, or null when it is.
        string? Verify(in ClipOverlayVerification check);
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
        private long _committedTicks;
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
        public bool Healthy
        {
            get
            {
                try { return _device.DeviceRemovedReason.Success && _composition.CheckDeviceState(out var valid).Success && valid; }
                catch (SharpGen.Runtime.SharpGenException) { return false; }
            }
        }

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
            _committedTicks = Stopwatch.GetTimestamp();
        }

        public void ReassertTopmost()
        {
            if (!SetWindowPos(_window, HwndTopmost, 0, 0, 0, 0, SwpNoMove | SwpNoSize | SwpNoActivate | SwpShowWindow))
                AppLog.Debug($"Clip overlay topmost reassert failed ({Marshal.GetLastWin32Error()}).");
        }

        public void Hide()
        {
            ShowWindow(_window, 0);
            _uploaded = false;
        }

        // On screen as Win32 sees it, and DWM has composed a frame since the
        // last commit, so the committed content has actually been drawn.
        public string? Verify(in ClipOverlayVerification check)
        {
            if (ClipOverlayWindowCheck.Inspect(check) is { } problem) return problem;
            if (!Healthy) return "device-lost";
            if (_composition.GetFrameStatistics(out var statistics).Failure || statistics.TimeFrequency <= 0) return null;
            var committed = (long)(_committedTicks * (double)statistics.TimeFrequency / Stopwatch.Frequency);
            return statistics.LastFrameTime >= committed ? null : "compositor-not-updating";
        }

        private void MoveWindow(in ClipOverlayMotionPlan plan)
        {
            if (_windowX == plan.WindowX && _windowY == plan.WindowY && _windowWidth == plan.WindowWidth && _windowHeight == plan.WindowHeight)
            {
                ReassertTopmost();
                return;
            }
            if (!SetWindowPos(_window, HwndTopmost, plan.WindowX, plan.WindowY, plan.WindowWidth, plan.WindowHeight, SwpNoActivate | SwpShowWindow))
                throw new InvalidOperationException($"SetWindowPos for the clip overlay failed ({Marshal.GetLastWin32Error()}).");
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
        public bool IsLayered => true;
        // UpdateLayeredWindow has to be called for every frame of a fade, so
        // this presenter stays on the tick loop.
        public bool AnimatesItself => false;
        public void Present(ClipOverlayFrame frame, PointNative destination, int width, int height, double opacity, bool frameChanged)
        {
            if (frameChanged || width != _width || height != _height) CreateBitmap(frame);
            var source = new PointNative(0, 0); var size = new SizeNative(width, height);
            var blend = new BlendFunction(0, 0, (byte)Math.Clamp((int)Math.Round(255 * opacity), 0, 255), 1);
            if (!UpdateLayeredWindow(_window, 0, ref destination, ref size, _memoryDc, ref source, 0, ref blend, UlwAlpha)) throw new InvalidOperationException($"UpdateLayeredWindow failed ({Marshal.GetLastWin32Error()}).");
            if (!SetWindowPos(_window, HwndTopmost, destination.X, destination.Y, width, height, SwpNoActivate | SwpShowWindow))
                throw new InvalidOperationException($"SetWindowPos for the layered clip overlay failed ({Marshal.GetLastWin32Error()}).");
        }
        public void Animate(ClipOverlayFrame frame, in ClipOverlayMotionPlan plan, bool applyAnimation, bool frameChanged)
            => Present(frame, new PointNative(plan.WindowX + (int)Math.Round(plan.ToOffsetX), plan.WindowY),
                plan.CardWidth, plan.CardHeight, plan.ToOpacity, frameChanged);
        public void ReassertTopmost()
            => SetWindowPos(_window, HwndTopmost, 0, 0, 0, 0, SwpNoMove | SwpNoSize | SwpNoActivate | SwpShowWindow);
        public void Hide() => ShowWindow(_window, 0);
        // UpdateLayeredWindow is synchronous; the window checks are the whole answer.
        public string? Verify(in ClipOverlayVerification check) => ClipOverlayWindowCheck.Inspect(check);
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
    private delegate void WinEventProc(nint hook, uint eventType, nint window, int objectId, int childId, uint thread, uint time);
    [DllImport("user32.dll")] private static extern nint SetWinEventHook(uint eventMin, uint eventMax, nint module, WinEventProc callback, uint processId, uint threadId, uint flags);
    [DllImport("user32.dll")] private static extern bool UnhookWinEvent(nint hook);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern nint GetModuleHandle(string? moduleName);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern ushort RegisterClassEx(ref WindowClass windowClass);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern nint CreateWindowEx(int extendedStyle, string className, string windowName, uint style, int x, int y, int width, int height, nint parent, nint menu, nint instance, nint parameter);
    [DllImport("user32.dll")] private static extern bool DestroyWindow(nint window);
    [DllImport("user32.dll")] private static extern nint DefWindowProc(nint window, uint message, nint wParam, nint lParam);
    [DllImport("user32.dll")] private static extern int GetMessage(out Message message, nint window, uint minimum, uint maximum);
    [DllImport("user32.dll")] private static extern bool TranslateMessage(ref Message message);
    [DllImport("user32.dll")] private static extern nint DispatchMessage(ref Message message);
    [DllImport("user32.dll")] private static extern void PostQuitMessage(int exitCode);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool PostMessage(nint window, uint message, nint wParam, nint lParam);
    [DllImport("user32.dll")] private static extern nuint SetTimer(nint window, nuint id, uint milliseconds, nint callback);
    [DllImport("user32.dll")] private static extern bool KillTimer(nint window, nuint id);
    [DllImport("user32.dll")] private static extern bool ShowWindow(nint window, uint command);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool SetWindowPos(nint window, nint insertAfter, int x, int y, int width, int height, uint flags);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool SetWindowDisplayAffinity(nint window, uint affinity);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] private static extern nint GetWindowLongPtr(nint window, int index);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")] private static extern nint SetWindowLongPtr(nint window, int index, nint value);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool UpdateLayeredWindow(nint window, nint destinationDc, ref PointNative destination, ref SizeNative size, nint sourceDc, ref PointNative source, uint colorKey, ref BlendFunction blend, uint flags);
    [DllImport("gdi32.dll")] private static extern nint CreateCompatibleDC(nint dc);
    [DllImport("gdi32.dll")] private static extern bool DeleteDC(nint dc);
    [DllImport("gdi32.dll")] private static extern nint CreateDIBSection(nint dc, ref BitmapInfo info, uint usage, out nint bits, nint section, uint offset);
    [DllImport("gdi32.dll")] private static extern nint SelectObject(nint dc, nint value);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(nint value);
}
