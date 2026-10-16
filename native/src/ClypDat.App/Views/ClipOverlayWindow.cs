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

// One animated value: where it starts, where it ends, how long it takes and
// how it gets there. A still value is a zero-length track.
internal readonly record struct ClipOverlayTrack(double From, double To, double DurationSeconds, ClipOverlayEasing Easing)
{
    public static ClipOverlayTrack Still(double value) => new(value, value, 0, ClipOverlayEasing.Out);

    public double Sample(double seconds)
        => ClipOverlayAnimationCurve.Build(From, To, DurationSeconds, Easing).Sample(Math.Clamp(seconds, 0, DurationSeconds));

    // The same motion re-expressed through origin + scale * value. Affine, so
    // the easing - and therefore the timing - is untouched.
    public ClipOverlayTrack Map(double origin, double scale) => this with { From = origin + scale * From, To = origin + scale * To };
}

internal enum ClipOverlayEasing { Out, In, Smooth }

// What one motion asks the presenter for: where the window sits for the whole
// notification, how big the card inside it is, and the tracks it animates.
// The card slides between off-the-edge (progress 0) and at rest (progress 1);
// the text on it fades and trails on its own, so it can arrive after the card
// and leave before it.
internal readonly record struct ClipOverlayMotionPlan(
    int WindowX, int WindowY, int WindowWidth, int WindowHeight,
    int CardWidth, int CardHeight,
    ClipOverlayTrack Progress, ClipOverlayTrack CardOffsetX,
    ClipOverlayTrack ContentOpacity, ClipOverlayTrack ContentOffsetX);

// The timing of the slide. Entry decelerates onto the screen and exit
// accelerates off it, as the badge always did. The text fades in over the
// whole entry when the card is arriving from off the edge, and over a short
// beat when only the text changes on a card already at rest; it fades out in
// the first half of the exit so the card leaves empty.
internal static class ClipOverlayMotion
{
    public const double EnterMs = 340, ExitMs = 260;
    private const double ContentRefreshMs = 200, ContentExitMs = 130;

    public static ClipOverlayTrack Progress(bool entering, double from)
        => entering
            ? new ClipOverlayTrack(from, 1, EnterMs / 1000, ClipOverlayEasing.Out)
            : new ClipOverlayTrack(from, 0, ExitMs / 1000, ClipOverlayEasing.In);

    public static ClipOverlayTrack Content(bool entering, double fromProgress, double from)
    {
        if (!entering) return new ClipOverlayTrack(from, 0, ContentExitMs / 1000, ClipOverlayEasing.Out);
        var travel = Math.Clamp(1 - fromProgress, 0, 1);
        return new ClipOverlayTrack(from, 1, (ContentRefreshMs + (EnterMs - ContentRefreshMs) * travel) / 1000, ClipOverlayEasing.Smooth);
    }
}

// DirectComposition animates a value by evaluating a polynomial in seconds
// since the animation started. These are the easings the overlay uses,
// expressed as those coefficients.
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

    // Out:    from + delta * (3u - 3u^2 + u^3)  -- matches 1 - (1-u)^3
    // In:     from + delta * u^3
    // Smooth: from + delta * (3u^2 - 2u^3)      -- smoothstep: slow start and end
    public static Cubic Build(double from, double to, double durationSeconds, ClipOverlayEasing easing)
    {
        if (durationSeconds <= 0) return new Cubic((float)to, 0, 0, 0);
        var delta = to - from;
        var d = durationSeconds;
        return easing switch
        {
            ClipOverlayEasing.Out => new Cubic((float)from, (float)(3 * delta / d), (float)(-3 * delta / (d * d)), (float)(delta / (d * d * d))),
            ClipOverlayEasing.Smooth => new Cubic((float)from, 0, (float)(3 * delta / (d * d)), (float)(-2 * delta / (d * d * d))),
            _ => new Cubic((float)from, 0, 0, (float)(delta / (d * d * d)))
        };
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
    private const double EnterMs = ClipOverlayMotion.EnterMs, ExitMs = ClipOverlayMotion.ExitMs;
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
    private double _motionStartProgress, _motionStartContent;
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
            if (!Present(1, 1, true)) { CompletePresentation(false, "present-failed-after-window-recreate"); Hide(); }
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
        var carried = sameWorkflow ? CurrentProgress() : 0;
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
            // Every new card restarts the entry from wherever the card is: a
            // different clip slides on from the edge, a stage of the same one
            // keeps its position (or turns back from leaving) while the new
            // text fades in on it. The text is never swapped in place at full
            // opacity, and upload plus motion land in one commit.
            _motionStartProgress = carried;
            _motionStartContent = 0;
            _motion = Motion.Entering;
            _motionStarted = Stopwatch.GetTimestamp();
            KillTimer(_window, HideTimerId);
            presented = Animate(PlanForMotion(), applyAnimation: true, frameChanged: true);
            if (presented) BeginVerification();
        }
        else if (!sameWorkflow)
        {
            _motion = Motion.Entering;
            _motionStartProgress = 0;
            _motionStartContent = 0;
            _motionStarted = Stopwatch.GetTimestamp();
            presented = Present(0, 0, true);
        }
        else
        {
            if (_motion == Motion.Exiting)
            {
                _motion = Motion.Entering;
                _motionStartProgress = carried;
                _motionStartContent = 0;
                _motionStarted = Stopwatch.GetTimestamp();
                KillTimer(_window, HideTimerId);
            }
            presented = Present(carried, CurrentContent(), true);
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
        _motionStartProgress = CurrentProgress();
        _motionStartContent = CurrentContent();
        _motion = Motion.Exiting;
        _motionStarted = Stopwatch.GetTimestamp();
        if (!Animating) return;
        Animate(PlanForMotion(), applyAnimation: true, frameChanged: false);
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
            var progress = CurrentProgress();
            if (Present(progress, CurrentContent(), false) && progress > 0) BeginVerification();
            if (elapsed >= EnterMs) { _motion = Motion.Still; if (Present(1, 1, false)) BeginVerification(); }
        }
        else if (_motion == Motion.Exiting)
        {
            Present(CurrentProgress(), CurrentContent(), false);
            if (elapsed >= ExitMs) Hide();
        }
        else if (Present(1, 1, false)) BeginVerification(); // Reassert topmost during full dwell.
    }

    // The tracks of the motion in progress, from when and where it started.
    private (ClipOverlayTrack Progress, ClipOverlayTrack Content) Tracks() => _motion switch
    {
        Motion.Entering => (ClipOverlayMotion.Progress(true, _motionStartProgress), ClipOverlayMotion.Content(true, _motionStartProgress, _motionStartContent)),
        Motion.Exiting => (ClipOverlayMotion.Progress(false, _motionStartProgress), ClipOverlayMotion.Content(false, _motionStartProgress, _motionStartContent)),
        _ => (ClipOverlayTrack.Still(1), ClipOverlayTrack.Still(1))
    };

    // The compositor owns the motion once an animation is committed, so where
    // the card and its text are is computed rather than observed. A republish
    // or dismissal needs it to pick the motion up wherever it had got to.
    private double CurrentProgress() => _visible ? Tracks().Progress.Sample(Stopwatch.GetElapsedTime(_motionStarted).TotalSeconds) : 0;
    private double CurrentContent() => _visible ? Tracks().Content.Sample(Stopwatch.GetElapsedTime(_motionStarted).TotalSeconds) : 0;

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
        _motionStartProgress = 0;
        _motionStartContent = 0;
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
            presented = Present(1, 1, true);
        }
        else if (step == 1 && _presenter is { IsLayered: false })
        {
            _recovery.Add("rebuild");
            presented = RebuildPresenter() && Present(1, 1, true);
        }
        else if (_presenter is { IsLayered: false })
        {
            _recovery.Add("layered");
            UseLayeredPresenter();
            presented = Present(1, 1, true);
        }
        else
        {
            _recovery.Add("reassert");
            ReassertTopmost();
            presented = Present(1, 1, true);
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
        if (!Present(1, 1, true))
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

    private ClipOverlayMotionPlan PlanForMotion()
    {
        var (progress, content) = Tracks();
        return Plan(progress, content);
    }

    // Offsets are affine in the tracks they follow: the card is one card width
    // past the edge at progress 0 and flush against it at 1; the text trails
    // by the parallax at opacity 0 and sits in place at 1.
    private ClipOverlayMotionPlan Plan(ClipOverlayTrack progress, ClipOverlayTrack content)
    {
        var layout = ClipOverlayLayout.Frame(_current!.Event.Target, _current.Event.Placement, _width, _height);
        return new ClipOverlayMotionPlan(
            layout.Window.X, layout.Window.Y, layout.Window.Width, layout.Window.Height, _width, _height,
            progress, progress.Map(layout.HiddenOffsetX, layout.RestOffsetX - layout.HiddenOffsetX),
            content, content.Map(layout.ParallaxX, -layout.ParallaxX));
    }

    // A compositor presenter keeps the window on the card's resting rectangle
    // and clips the card to it; the layered one shrinks the window itself to
    // the part of the card that is on screen.
    private Avalonia.PixelRect ExpectedWindow(in ClipOverlayMotionPlan plan)
    {
        var window = new Avalonia.PixelRect(plan.WindowX, plan.WindowY, plan.WindowWidth, plan.WindowHeight);
        return Animating ? window : ClipOverlayLayout.VisibleSlice(window, plan.CardOffsetX.To).Window;
    }

    private bool Animate(ClipOverlayMotionPlan plan, bool applyAnimation, bool frameChanged)
    {
        if (_current is null || _frame is not { } frame || _presenter is null) return false;
        _expectedWindow = ExpectedWindow(plan);
        try
        {
            _presenter.Animate(frame, in plan, applyAnimation, frameChanged);
            return true;
        }
        catch (Exception error)
        {
            return RecoverPresenter(error, () => { _expectedWindow = ExpectedWindow(plan); _presenter!.Animate(frame, in plan, true, true); });
        }
    }

    // The card held still at one point of the slide.
    private bool Present(double progress, double content, bool frameChanged)
        => _current is not null && Animate(Plan(ClipOverlayTrack.Still(progress), ClipOverlayTrack.Still(content)), applyAnimation: true, frameChanged);

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
                    var plan = Plan(ClipOverlayTrack.Still(CurrentProgress()), ClipOverlayTrack.Still(CurrentContent()));
                    _expectedWindow = ExpectedWindow(plan);
                    _presenter!.Animate(_frame!, in plan, true, true);
                    return true;
                }
                catch (Exception layeredError) { error = layeredError; }
            }
        }
        if (!_presentFailureLogged) AppLog.Error($"Clip overlay native present failed: id={_current?.Event.WorkflowId}, backend={_presenter?.Name}.", error);
        _presentFailureLogged = true;
        return false;
    }

    private static double Elapsed(long fromTicks, long toTicks)
        => fromTicks <= 0 || toTicks <= fromTicks ? 0 : (toTicks - fromTicks) * 1000.0 / Stopwatch.Frequency;

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
        // without the caller waking up per frame. A presenter that cannot is
        // handed still plans, one per tick, and shows each plan's end values.
        bool AnimatesItself { get; }
        // The last-resort presenter, which has nothing to fall back to.
        bool IsLayered => false;
        // False once the presenter's device has been lost.
        bool Healthy => true;
        void Animate(ClipOverlayFrame frame, in ClipOverlayMotionPlan plan, bool applyAnimation, bool frameChanged);
        void ReassertTopmost();
        void Hide();
        // Why the presented card is not actually on screen, or null when it is.
        string? Verify(in ClipOverlayVerification check);
    }

    // The card is two visuals: the body (fill, accent rail, border) and, as its
    // child, the content (mark, text, hotkey chips). The body carries the
    // slide; the content carries its own fade and trailing offset on top of
    // it. The window is the card's resting rectangle and DirectComposition
    // draws nothing outside it, so the body sliding past the window's flush
    // edge reads as the card going off the side of the screen - without the
    // window ever reaching onto a neighbouring monitor.
    private sealed class DirectCompositionClipOverlayPresenter : INativeClipOverlayPresenter
    {
        // Where the card is parked while the window is hidden: far enough
        // outside any window that whatever it held is never shown again when
        // the window next appears, before the new card's commit lands.
        private const float ParkedOffsetX = 100_000f;
        private readonly nint _window;
        private readonly ID3D11Device _device;
        private readonly ID3D11DeviceContext _context;
        private readonly IDXGIDevice _dxgiDevice;
        private readonly IDXGIFactory2 _factory;
        private readonly IDCompositionDevice _composition;
        private readonly IDCompositionTarget _target;
        private readonly IDCompositionVisual _visual, _contentVisual;
        private readonly IDCompositionEffectGroup _contentEffect;
        private SwapChainLayer? _body, _content;
        private byte[]? _blank;
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
            _composition.CreateVisual(out _contentVisual).CheckError();
            // Opacity lives on an effect group, not on the visual itself.
            _composition.CreateEffectGroup(out _contentEffect).CheckError();
            _contentEffect.SetOpacity(0f).CheckError();
            _contentVisual.SetEffect(_contentEffect).CheckError();
            _visual.AddVisual(_contentVisual, true, null).CheckError();
            _visual.SetOffsetX(ParkedOffsetX).CheckError();
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

        public void Animate(ClipOverlayFrame frame, in ClipOverlayMotionPlan plan, bool applyAnimation, bool frameChanged)
        {
            MoveWindow(plan);
            EnsureSwapChains(plan.CardWidth, plan.CardHeight);
            if (frameChanged || !_uploaded)
            {
                // Uploaded once per card. Motion and fade are the compositor's
                // job, so nothing here runs per frame.
                var stride = plan.CardWidth * 4;
                _body!.Upload(_context, frame.Layers?.Body ?? frame.Pixels, stride);
                _content!.Upload(_context, frame.Layers?.Content ?? Blank(frame.Pixels.Length), stride);
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
            try
            {
                _visual.SetOffsetX(ParkedOffsetX).CheckError();
                _composition.Commit().CheckError();
            }
            catch (SharpGen.Runtime.SharpGenException error) { AppLog.Debug($"Clip overlay could not park the hidden card: {error.Message}"); }
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
            Apply(plan.CardOffsetX, animation => _visual.SetOffsetX(animation).CheckError(), value => _visual.SetOffsetX(value).CheckError());
            Apply(plan.ContentOpacity, animation => _contentEffect.SetOpacity(animation).CheckError(), value => _contentEffect.SetOpacity(value).CheckError());
            Apply(plan.ContentOffsetX, animation => _contentVisual.SetOffsetX(animation).CheckError(), value => _contentVisual.SetOffsetX(value).CheckError());
        }

        private void Apply(in ClipOverlayTrack track, Action<IDCompositionAnimation> animate, Action<float> set)
        {
            if (track.DurationSeconds <= 0 || Math.Abs(track.To - track.From) < 0.0005) { set((float)track.To); return; }
            _composition.CreateAnimation(out var animation).CheckError();
            using (animation)
            {
                var curve = ClipOverlayAnimationCurve.Build(track.From, track.To, track.DurationSeconds, track.Easing);
                animation.AddCubic(0, curve.Constant, curve.Linear, curve.Quadratic, curve.Cubed).CheckError();
                animation.End(track.DurationSeconds, (float)track.To).CheckError();
                animate(animation);
            }
        }

        private byte[] Blank(int length) => _blank is { } blank && blank.Length == length ? blank : _blank = new byte[length];

        private void EnsureSwapChains(int width, int height)
        {
            if (_body is not null && _width == width && _height == height) return;
            _visual.SetContent(null).CheckError();
            _contentVisual.SetContent(null).CheckError();
            _body?.Dispose();
            _content?.Dispose();
            _body = new SwapChainLayer(_factory, _device, width, height);
            _content = new SwapChainLayer(_factory, _device, width, height);
            _visual.SetContent(_body.Chain).CheckError();
            _contentVisual.SetContent(_content.Chain).CheckError();
            _width = width; _height = height;
            _uploaded = false;
        }
        public void Dispose()
        {
            _visual.SetContent(null); _contentVisual.SetContent(null); _body?.Dispose(); _content?.Dispose();
            _contentEffect.Dispose(); _contentVisual.Dispose(); _visual.Dispose(); _target.Dispose(); _composition.Dispose(); _factory.Dispose(); _dxgiDevice.Dispose(); _context.Dispose(); _device.Dispose();
        }

        private sealed unsafe class SwapChainLayer : IDisposable
        {
            private readonly IDXGISwapChain3 _chain3;
            public SwapChainLayer(IDXGIFactory2 factory, ID3D11Device device, int width, int height)
            {
                var description = new SwapChainDescription1((uint)width, (uint)height, DxgiFormat.B8G8R8A8_UNorm, false, Usage.RenderTargetOutput, 2, Scaling.Stretch, SwapEffect.FlipSequential, AlphaMode.Premultiplied, SwapChainFlags.None);
                Chain = factory.CreateSwapChainForComposition(device, description);
                _chain3 = Chain.QueryInterface<IDXGISwapChain3>();
            }
            public IDXGISwapChain1 Chain { get; }
            public void Upload(ID3D11DeviceContext context, byte[] pixels, int stride)
            {
                using var texture = Chain.GetBuffer<ID3D11Texture2D>(_chain3.CurrentBackBufferIndex);
                fixed (byte* source = pixels) context.UpdateSubresource(texture, 0, null, (nint)source, (uint)stride, 0);
                Chain.Present(0, PresentFlags.None).CheckError();
            }
            public void Dispose() { _chain3.Dispose(); Chain.Dispose(); }
        }
    }

    // UpdateLayeredWindow can only move and size a whole window, so this
    // presenter shows the slide by shrinking the window to the part of the
    // card that is on screen and blitting that part from the card. The window
    // never leaves the card's resting rectangle, so it never reaches another
    // monitor either. There is no separate text fade here; the card arrives
    // and leaves whole.
    private sealed class LayeredClipOverlayPresenter : INativeClipOverlayPresenter
    {
        private const uint UlwAlpha = 2;
        private readonly nint _window;
        private nint _memoryDc, _bitmap, _oldBitmap, _bits;
        private int _width, _height;
        public LayeredClipOverlayPresenter(nint window) => _window = window;
        public string Name => "layered";
        public bool IsLayered => true;
        // UpdateLayeredWindow has to be called for every frame of a slide, so
        // this presenter stays on the tick loop.
        public bool AnimatesItself => false;
        public void Animate(ClipOverlayFrame frame, in ClipOverlayMotionPlan plan, bool applyAnimation, bool frameChanged)
        {
            if (frameChanged || frame.Width != _width || frame.Height != _height) CreateBitmap(frame);
            var slice = ClipOverlayLayout.VisibleSlice(new Avalonia.PixelRect(plan.WindowX, plan.WindowY, plan.WindowWidth, plan.WindowHeight), plan.CardOffsetX.To);
            var destination = new PointNative(slice.Window.X, slice.Window.Y);
            var source = new PointNative(slice.SourceX, 0);
            var size = new SizeNative(slice.Window.Width, slice.Window.Height);
            var blend = new BlendFunction(0, 0, (byte)(slice.Empty ? 0 : 255), 1);
            if (!UpdateLayeredWindow(_window, 0, ref destination, ref size, _memoryDc, ref source, 0, ref blend, UlwAlpha)) throw new InvalidOperationException($"UpdateLayeredWindow failed ({Marshal.GetLastWin32Error()}).");
            if (!SetWindowPos(_window, HwndTopmost, destination.X, destination.Y, size.Width, size.Height, SwpNoActivate | SwpShowWindow))
                throw new InvalidOperationException($"SetWindowPos for the layered clip overlay failed ({Marshal.GetLastWin32Error()}).");
        }
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
