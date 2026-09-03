using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Animation;
using Avalonia.Animation.Easings;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Media.Transformation;
using Avalonia.Rendering.Composition;
using Avalonia.Styling;
using Avalonia.Threading;
using ClypDat.App.Services;

namespace ClypDat.App.Views;

internal enum ClipOverlaySide { Left, Right }

// The monitor decision for ONE clip event, shared by every toast that event
// produces.
//
// A hotkey save shows two toasts through one reused window - "Clip Saving…"
// when the worker starts and "Clip Saved" 2-4 seconds later when it finishes -
// and the monitor used to be resolved separately for each. Any disagreement
// between the two answers (the game window resolvable for one and not the
// other, which is exactly what an alt-tab or a mode switch mid-save produces)
// dragged the one visible window from one display to another mid-life. Resolve
// once, carry the answer, and that cannot happen.
internal sealed class ClipOverlaySession
{
    private static int _counter;

    public ClipOverlaySession(string trigger, ClipOverlayTarget target)
    {
        Id = $"{Interlocked.Increment(ref _counter):x4}";
        Trigger = trigger;
        Target = target;
        StartedUtc = DateTime.UtcNow;
    }

    public string Id { get; }
    public string Trigger { get; }
    public ClipOverlayTarget Target { get; }
    public DateTime StartedUtc { get; }
    public TimeSpan Age => DateTime.UtcNow - StartedUtc;
}

internal sealed record ClipOverlayRequest(
    ClipOverlaySession Session,
    string Text,
    ClipOverlaySide Side,
    bool PlaySound,
    bool ExcludeFromCapture,
    string? Hotkey = null,
    string HotkeyHint = "");

// The clip notification badge: one borderless, topmost, click-through window
// pinned flush to one monitor's edge, sliding in from that edge and back out.
//
// Two things about this window are not obvious and are load-bearing.
//
// It is NEVER HIDDEN after startup; when idle it is parked off the side of the
// virtual desktop instead. Avalonia's WindowBase.Hide calls StopRendering()
// before hiding, and Show() calls PlatformImpl.Show() and only THEN
// StartRendering() - so between those two the window is on screen holding its
// last composited surface, which is the PREVIOUS notification. Filmed at 30fps
// that was four solid frames of "Clip Saved" at the start of a toast that says
// "Clip Saving…". A window that is never un-rendered has no stale frame to
// present, and moving it costs nothing.
//
// It carries TWO badge layers, not one. New text is written to the layer that
// is off screen and then animated in while the visible layer animates out, so
// the badge's content is never mutated in front of the user, and the result of
// a save arrives by overtaking the "saving" badge rather than snapping in
// place.
internal sealed class ClipOverlayWindow : IDisposable
{
    private const double TopMarginDips = 24;
    // Off every monitor, the same corner Windows itself parks minimised windows
    // at. Far enough out that no monitor arrangement can reach it.
    private static readonly PixelPoint ParkPosition = new(-32000, -32000);
    private static readonly TimeSpan EnterDuration = TimeSpan.FromMilliseconds(220);
    private static readonly TimeSpan SwapDuration = TimeSpan.FromMilliseconds(220);
    private static readonly TimeSpan ExitDuration = TimeSpan.FromMilliseconds(200);
    private static readonly TimeSpan DwellDuration = TimeSpan.FromMilliseconds(2200);
    private static readonly TimeSpan TopmostReassertInterval = TimeSpan.FromMilliseconds(500);
    private static readonly TimeSpan FrameBarrierTimeout = TimeSpan.FromMilliseconds(200);
    // The badge's BoxShadow is "0 10 28" - the blur has to clear the clip too,
    // or a soft grey smudge stays parked against the screen edge.
    private const double ShadowSlackDips = 32;

    private readonly Action _playSound;
    private readonly ClipOverlayPresentationState _presentation = new();

    private Window? _window;
    private Panel? _root;
    private BadgeLayer? _showing;
    private BadgeLayer? _staging;
    private ServerPerPixelOverlay? _perPixel;

    private DispatcherTimer? _dwellTimer;
    private DispatcherTimer? _topmostTimer;
    private DispatcherTimer? _watchdogTimer;
    private CancellationTokenSource? _motion;
    private Task _presentations = Task.CompletedTask;

    private ClipOverlaySession? _visibleSession;
    private ClipOverlayTarget? _committedTarget;
    private PixelRect _committedRect;
    private ClipOverlaySide _committedSide;
    private DateTime _shownAtUtc;
    private int _generation;
    private bool _parked = true;

    // Server SKUs mirror the whole badge panel into a native layered window,
    // because per-pixel transparency is not available there. The mirror is a
    // blit of _root, so it picks up both layers and the crossing for free - it
    // only needs re-blitting once per frame while anything is moving.
    private int _serverMirrorId;

    public ClipOverlayWindow(Action playSound) => _playSound = playSound;

    public bool IsShowing => _visibleSession is not null && !_parked;

    // Carries the session that was taken down, not just "something hid":
    // parking a superseded toast happens AFTER the next one's session already
    // exists, so the listener has to be able to tell the two apart.
    public event EventHandler<ClipOverlaySession>? Hidden;

    // Realises the window once, at startup, parked off the virtual desktop.
    //
    // Avalonia's Measure falls back to different (observed: wider) text metrics
    // for a control that has never been attached to a TopLevel, so the first
    // badge of a session used to come out visibly elongated. And
    // WS_EX_NOACTIVATE / WS_EX_TRANSPARENT can only be applied to a real hwnd,
    // which made the first Show of the session the one Show without them -
    // mid-game, where a window taking foreground for a single poll tick makes
    // the capture log "recording paused (window not foreground)". Both costs
    // are paid here, at launch, off screen.
    public void Warm() => EnsureWindow();

    // Presentations run one at a time. Show is fire-and-forget by design (the
    // caller is a worker event handler, not something that can await), and each
    // presentation awaits a frame barrier and an animation - so without this
    // chain two notifications arriving a few hundred ms apart would interleave
    // their awaits and fight over the same two layers.
    public void Show(ClipOverlayRequest request) =>
        _presentations = _presentations.ContinueWith(
            _ => PresentAsync(request),
            CancellationToken.None,
            TaskContinuationOptions.None,
            TaskScheduler.FromCurrentSynchronizationContext()).Unwrap();

    public void HideNow(string reason) => Park(reason);

    public void Dispose()
    {
        Park("disposed");
        _perPixel?.Dispose();
        _perPixel = null;
        _window?.Close();
        _window = null;
    }

    // ---- presentation ----------------------------------------------------

    private async Task PresentAsync(ClipOverlayRequest request)
    {
        try
        {
            EnsureWindow();
            if (_window is null || _root is null || _showing is null || _staging is null) return;

            // Same clip, new text, badge already on screen: cross the two
            // layers over rather than starting again from the edge.
            if (!_parked && _visibleSession is { } visible && ReferenceEquals(visible, request.Session))
            {
                await OvertakeAsync(request);
                return;
            }

            // A different clip while one is up: the old one leaves first.
            if (!_parked) await ExitAsync("superseded");

            await EnterAsync(request);
        }
        catch (OperationCanceledException)
        {
            // Superseded by a newer notification mid-animation. The newer one
            // owns the window now; nothing to unwind.
        }
        catch (Exception error)
        {
            AppLog.Error("Clip overlay presentation failed", error);
            Park("presentation-failed");
        }
    }

    private async Task EnterAsync(ClipOverlayRequest request)
    {
        if (_window is null || _showing is null || _staging is null) return;

        var target = request.Session.Target;
        var isLeft = request.Side == ClipOverlaySide.Left;

        StopTimer(ref _dwellTimer);
        CancelMotion();

        // Content goes onto the layer that will arrive, laid out and rendered
        // while the whole window is still parked off screen.
        var incoming = _staging;
        incoming.Apply(request, isLeft);
        incoming.Root.IsVisible = true;
        _showing.Root.IsVisible = false;
        ApplyCaptureExclusion(request.ExcludeFromCapture);

        // Park the window on the TARGET monitor before measuring. A parked
        // window still receives WM_DPICHANGED when it crosses displays, so this
        // is what gives the hwnd the target's DPI context - without it the DIP
        // size below converts against the monitor it was last on, which across
        // a 100%/150% pair is a badge either a third too narrow or half again
        // too wide.
        MoveWindow(target.WorkArea.X, target.WorkArea.Y, 0, 0, sizeToo: false);

        var size = MeasureBadge(incoming, target);
        var rect = RectFor(target, isLeft, size.widthPixels, size.heightPixels);

        _committedTarget = target;
        _committedSide = request.Side;
        _committedRect = rect;
        _visibleSession = request.Session;

        SetWindowSize(target, size.widthPixels, size.heightPixels);

        // Start off screen, then let one real frame render it there. The window
        // is visible-but-parked throughout, so this frame genuinely happens -
        // which is the whole point: the first frame anyone can see is already
        // the new content, sitting outside the badge's own clip bounds.
        var travel = TravelFor(isLeft, size.widthDips);
        incoming.SetOffset(travel);
        RaiseAbove(incoming);
        var barrier = await WaitForRenderedFrameAsync(CancellationToken.None);

        var generation = _generation = _presentation.Begin();
        _shownAtUtc = DateTime.UtcNow;

        MoveWindow(rect.X, rect.Y, rect.Width, rect.Height, sizeToo: true);
        CorrectForActualSize(target, isLeft, rect);
        _parked = false;
        AssertTopmost();
        StartTopmostReassert();
        ArmWatchdog();

        AppLog.Info(
            $"Clip overlay requested: id={request.Session.Id}, trigger={request.Session.Trigger}, text='{request.Text}', " +
            $"side={(isLeft ? "left" : "right")}, monitor={target.DeviceName} ({ClipOverlayTargeting.LabelFor(target.DeviceName)}), " +
            $"reason={target.ReasonLabel}, work={Describe(target.WorkArea)}, scaling={target.Scaling:0.00}, " +
            $"requested={Describe(rect)}, actual={Describe(_committedRect)}, foreground={DescribeForeground()}.");

        if (request.PlaySound) _playSound();

        var started = DateTime.UtcNow;
        var token = NewMotionToken();
        StartServerMirror(request.ExcludeFromCapture);
        await SlideAsync(incoming, travel, 0, EnterDuration, new CubicEaseOut(), token);

        if (!_presentation.EnterCompleted(generation)) return;
        PromoteStaging();

        // barrier= and animated= are the whole diagnostic story. A healthy
        // toast barriers in well under a frame and animates within a few
        // percent of what it asked for; an "animation" that completes in
        // almost no time did not play, which is exactly the regression that
        // shipped unnoticed once already and was only ever visible on a phone
        // camera.
        var elapsed = (DateTime.UtcNow - started).TotalMilliseconds;
        AppLog.Info(
            $"Clip overlay entry: id={request.Session.Id}, barrier={barrier.elapsedMs:0}ms({barrier.outcome}), " +
            $"animated={elapsed:0}ms, requested={EnterDuration.TotalMilliseconds:0}ms.");
        StartDwell(generation);
    }

    private async Task OvertakeAsync(ClipOverlayRequest request)
    {
        if (_window is null || _showing is null || _staging is null || _committedTarget is not { } target) return;

        var generation = _generation;
        if (!_presentation.BeginSwap(generation)) return;

        StopTimer(ref _dwellTimer);
        // Cancelling leaves the outgoing layer exactly where it was drawn -
        // FillMode.Forward wrote the interpolated value back - so reading its X
        // below gives the true start for its exit. That is what makes a result
        // arriving mid-entry cross without a jump.
        CancelMotion();

        var isLeft = request.Side == ClipOverlaySide.Left;
        var outgoing = _showing;
        var incoming = _staging;
        var outgoingFrom = outgoing.Offset;
        incoming.Apply(request, isLeft);
        incoming.Root.IsVisible = true;
        RaiseAbove(incoming);
        ApplyCaptureExclusion(request.ExcludeFromCapture);

        var size = MeasureBadge(incoming, target);

        // The window only ever GROWS within a session, and only away from the
        // anchored edge. Shrinking it - or growing it on the flush side - moves
        // the edge the badge is pinned to while the badge is on screen, which
        // is a visible slide of the whole thing. The leftover transparent strip
        // costs nothing: it is click-through and invisible.
        var grew = false;
        var widthPixels = Math.Max(size.widthPixels, _committedRect.Width);
        var heightPixels = Math.Max(size.heightPixels, _committedRect.Height);
        if (widthPixels > _committedRect.Width || heightPixels > _committedRect.Height)
        {
            var grown = RectFor(target, isLeft, widthPixels, heightPixels);
            SetWindowSize(target, widthPixels, heightPixels);
            MoveWindow(grown.X, grown.Y, grown.Width, grown.Height, sizeToo: true);
            _committedRect = grown;
            grew = true;
        }

        var travel = TravelFor(isLeft, size.widthDips);
        var outgoingTravel = TravelFor(isLeft, outgoing.Width);
        incoming.SetOffset(travel);
        var barrier = await WaitForRenderedFrameAsync(CancellationToken.None);
        if (!_presentation.IsCurrent(generation)) return;

        if (request.PlaySound) _playSound();

        var started = DateTime.UtcNow;
        var token = NewMotionToken();
        StartServerMirror(request.ExcludeFromCapture);

        // The pairing is what reads as an overtake rather than a swap: the
        // incoming badge is on top and covers half its distance in the first
        // fifth of the time, while the outgoing one has barely started, then
        // accelerates away underneath it. A delay between the two would read as
        // a stutter instead.
        await Task.WhenAll(
            SlideAsync(incoming, travel, 0, SwapDuration, new CubicEaseOut(), token),
            SlideAsync(outgoing, outgoingFrom, outgoingTravel, SwapDuration, new CubicEaseIn(), token));

        if (!_presentation.SwapCompleted(generation)) return;

        outgoing.Root.IsVisible = false;
        outgoing.SetOffset(0);
        PromoteStaging();
        _committedSide = request.Side;
        _shownAtUtc = DateTime.UtcNow;

        AppLog.Info(
            $"Clip overlay overtake: id={request.Session.Id}, from='{outgoing.Text}', to='{request.Text}', " +
            $"out-from={outgoingFrom:0.#}dip, resized={(grew ? $"grew to {widthPixels}x{heightPixels}" : "no")}, " +
            $"barrier={barrier.elapsedMs:0}ms({barrier.outcome}), animated={(DateTime.UtcNow - started).TotalMilliseconds:0}ms, " +
            $"requested={SwapDuration.TotalMilliseconds:0}ms.");

        StartDwell(generation);
    }

    private async Task ExitAsync(string reason)
    {
        if (_showing is null || _parked) return;
        var generation = _generation;
        if (!_presentation.BeginExit(generation)) return;

        StopTimer(ref _dwellTimer);
        CancelMotion();

        var isLeft = _committedSide == ClipOverlaySide.Left;
        var travel = TravelFor(isLeft, _showing.Width);
        var token = NewMotionToken();
        var started = DateTime.UtcNow;
        var id = _visibleSession?.Id ?? "----";

        // A cancellation here means a newer toast cut the exit short; it owns
        // the window from that point and enters from wherever this left the
        // layer, so the exception is deliberately allowed to propagate.
        await SlideAsync(_showing, _showing.Offset, travel, ExitDuration, new CubicEaseIn(), token);

        AppLog.Info($"Clip overlay exit: id={id}, animated={(DateTime.UtcNow - started).TotalMilliseconds:0}ms, requested={ExitDuration.TotalMilliseconds:0}ms.");
        Park(reason);
    }

    // Parking replaces hiding: the window stays visible (and therefore stays
    // rendered) but sits off every monitor. See the class comment.
    private void Park(string reason)
    {
        StopTimer(ref _dwellTimer);
        StopTimer(ref _topmostTimer);
        StopTimer(ref _watchdogTimer);
        CancelMotion();
        StopServerMirror();

        var session = _visibleSession;
        _presentation.Idle();
        _visibleSession = null;
        _committedTarget = null;
        _parked = true;

        // Both layers go away, not just the staging one: the parked window keeps
        // rendering, and the frame it settles on should be an empty one. That
        // way even a surface presented before the next toast's own frame lands
        // has nothing stale on it.
        foreach (var layer in new[] { _showing, _staging })
        {
            if (layer is null) continue;
            layer.SetOffset(0);
            layer.Root.IsVisible = false;
        }

        MoveWindow(ParkPosition.X, ParkPosition.Y, 0, 0, sizeToo: false);

        if (session is null) return;
        AppLog.Info($"Clip overlay parked: id={session.Id}, reason={reason}, visible={(DateTime.UtcNow - _shownAtUtc).TotalSeconds:0.00}s.");
        Hidden?.Invoke(this, session);
    }

    private void PromoteStaging()
    {
        (_showing, _staging) = (_staging, _showing);
        if (_staging is not null)
        {
            _staging.Root.IsVisible = false;
            _staging.SetOffset(0);
        }
    }

    // ---- motion ----------------------------------------------------------

    // An explicit keyframe animation, never a Transition, and run against the
    // layer's RenderTransform rather than a TranslateTransform object.
    //
    // Both parts matter. A Transition needs its from-value and its to-value
    // committed in separate passes, and a badge that is prepared and revealed
    // in one beat can commit both together. And Avalonia's animator for
    // TranslateTransform.X casts its target to Visual (TransformAnimator), so
    // running an Animation directly on a TranslateTransform throws - the
    // supported target is Visual.RenderTransform carrying TransformOperations,
    // which TransformOperationsAnimator interpolates properly.
    private static async Task SlideAsync(BadgeLayer layer, double from, double to, TimeSpan duration, Easing easing, CancellationToken token)
    {
        var animation = new Animation
        {
            Duration = duration,
            Easing = easing,
            // Without Forward the value snaps back to its base the instant the
            // animation ends - the badge would teleport off screen at the end
            // of its own entrance.
            FillMode = FillMode.Forward,
            Children =
            {
                new KeyFrame { Cue = new Cue(0d), Setters = { new Setter(Visual.RenderTransformProperty, TranslateBy(from)) } },
                new KeyFrame { Cue = new Cue(1d), Setters = { new Setter(Visual.RenderTransformProperty, TranslateBy(to)) } }
            }
        };

        await animation.RunAsync(layer.Root, token);
        token.ThrowIfCancellationRequested();
        // The completion callback runs before the final fill is applied, so the
        // resting value is assigned explicitly rather than assumed.
        layer.SetOffset(to);
    }

    private static TransformOperations TranslateBy(double x)
    {
        var builder = TransformOperations.CreateBuilder(1);
        builder.AppendTranslate(x, 0);
        return builder.Build();
    }

    // Server SKUs cannot do per-pixel transparency, so the badge is mirrored
    // into a native layered window by blitting _root. That blit is synchronous
    // (UpdateLayeredWindow), so the mirror never had the stale-surface problem
    // and needs no frame barrier - it just has to be re-blitted while anything
    // is moving. Because it copies the whole panel, both layers and the
    // crossing between them come along for free.
    //
    // Frames come off the OVERLAY window, not the main window: the main window
    // is minimised to the tray for the entire time anyone is playing, and a
    // tray-minimised toplevel stops producing animation frames - which is what
    // previously left Server with a badge that never finished its slide.
    private void StartServerMirror(bool excludeFromCapture)
    {
        if (!WindowsPlatformProfile.IsServer() || _window is not TopLevel topLevel) return;

        _perPixel?.ShowAndRefresh();
        _perPixel?.SetCaptureExcluded(excludeFromCapture);

        var id = ++_serverMirrorId;
        void Pump(TimeSpan _)
        {
            if (id != _serverMirrorId || _parked) return;
            _perPixel?.Refresh();
            topLevel.RequestAnimationFrame(Pump);
        }

        topLevel.RequestAnimationFrame(Pump);
    }

    // Refresh early-returns on a hidden input window, which used to be what
    // stopped it painting a finished toast. The window is never hidden any
    // more, so parking has to stop the pump itself or it would keep blitting
    // the badge at (-32000, -32000) forever.
    private void StopServerMirror()
    {
        _serverMirrorId++;
        _perPixel?.Hide();
    }

    // How far a layer travels to be completely off the badge's clip bounds.
    // The shadow has to clear it too, or a soft grey smudge stays behind
    // against the screen edge. Signed by the anchored side, and snapped to the
    // OVERLAY window's pixel grid - not the main window's, which is a different
    // grid whenever the two sit on displays with different DPI.
    private double TravelFor(bool isLeft, double widthDips)
    {
        var travel = SnapToPixelGrid(widthDips + ShadowSlackDips);
        return isLeft ? -travel : travel;
    }

    private double SnapToPixelGrid(double value)
    {
        var scaling = _window?.RenderScaling > 0 ? _window.RenderScaling : 1;
        return Math.Round(value * scaling) / scaling;
    }

    // The arriving badge has to be drawn OVER the one it is overtaking. ZIndex
    // rather than reordering the panel's children: removing and re-adding a
    // control raises DetachedFromVisualTree, which silently completes any
    // animation running on it - the outgoing badge would freeze mid-slide.
    private void RaiseAbove(BadgeLayer layer)
    {
        if (_showing is null || _staging is null) return;
        var other = ReferenceEquals(layer, _showing) ? _staging : _showing;
        layer.Root.ZIndex = 1;
        other.Root.ZIndex = 0;
    }

    private CancellationToken NewMotionToken()
    {
        CancelMotion();
        _motion = new CancellationTokenSource();
        return _motion.Token;
    }

    private void CancelMotion()
    {
        var motion = _motion;
        _motion = null;
        motion?.Cancel();
        motion?.Dispose();
    }

    // Waits until the frame containing whatever was just set has actually been
    // DRAWN, and reports how long that took.
    //
    // A window that is never hidden always holds a real frame, but "real" is
    // not "current" - what is on screen is the last frame the render thread
    // presented. CompositionBatch.Rendered is the only signal that says our
    // frame is among them. RequestAnimationFrame is not: those callbacks are
    // drained at the TOP of the media context's render pass, before layout and
    // before the batch is handed to the render thread, so a badge revealed on
    // an animation-frame callback can still be revealed one frame early - which
    // is the whole bug this class exists to close.
    //
    // Always raced against a timeout. A barrier that never completes must not
    // be able to swallow a notification.
    private async Task<(double elapsedMs, string outcome)> WaitForRenderedFrameAsync(CancellationToken token)
    {
        var started = DateTime.UtcNow;
        var compositor = _root is null ? null : ElementComposition.GetElementVisual(_root)?.Compositor;
        if (compositor is null)
        {
            // No composition visual yet (possible before the first attach).
            // One animation-frame hop is weaker than the barrier, but it is
            // strictly better than not waiting at all.
            if (_window is TopLevel topLevel)
            {
                var hop = new TaskCompletionSource();
                topLevel.RequestAnimationFrame(_ => hop.TrySetResult());
                await hop.Task;
            }

            return ((DateTime.UtcNow - started).TotalMilliseconds, "no-compositor");
        }

        var batch = compositor.RequestCompositionBatchCommitAsync();
        var finished = await Task.WhenAny(batch.Rendered, Task.Delay(FrameBarrierTimeout, CancellationToken.None));
        // Batch.Rendered completes its continuations synchronously ON THE
        // RENDER THREAD, so everything after this point has to be back on the
        // UI thread before it touches a field.
        if (!Dispatcher.UIThread.CheckAccess()) await Dispatcher.UIThread.InvokeAsync(() => { });

        var elapsed = (DateTime.UtcNow - started).TotalMilliseconds;
        if (!ReferenceEquals(finished, batch.Rendered))
        {
            AppLog.Error($"Clip overlay frame barrier timed out after {FrameBarrierTimeout.TotalMilliseconds:0}ms; presenting anyway - stale content is possible.");
            return (elapsed, "timeout");
        }

        token.ThrowIfCancellationRequested();
        return (elapsed, "rendered");
    }

    // ---- placement -------------------------------------------------------

    private (double widthDips, int widthPixels, int heightPixels) MeasureBadge(BadgeLayer layer, ClipOverlayTarget target)
    {
        layer.Root.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        var desired = layer.Root.DesiredSize;
        layer.Width = desired.Width;
        layer.Height = desired.Height;
        return (desired.Width, (int)Math.Ceiling(desired.Width * target.Scaling), (int)Math.Ceiling(desired.Height * target.Scaling));
    }

    private void SetWindowSize(ClipOverlayTarget target, int widthPixels, int heightPixels)
    {
        if (_window is null) return;
        // Assigned in DIPs derived from the target monitor's own scaling, so
        // Avalonia's bookkeeping agrees with the pixel rect applied below
        // rather than fighting it on the next layout pass.
        _window.Width = widthPixels / target.Scaling;
        _window.Height = heightPixels / target.Scaling;
    }

    private PixelRect RectFor(ClipOverlayTarget target, bool isLeft, int widthPixels, int heightPixels)
    {
        var area = target.WorkArea;
        var x = isLeft ? area.X : area.X + area.Width - widthPixels;
        x = Math.Clamp(x, area.X, Math.Max(area.X, area.X + area.Width - widthPixels));
        var y = area.Y + (int)Math.Round(TopMarginDips * target.Scaling);
        y = Math.Clamp(y, area.Y, Math.Max(area.Y, area.Y + area.Height - heightPixels));
        return new PixelRect(x, y, widthPixels, heightPixels);
    }

    private void MoveWindow(int x, int y, int width, int height, bool sizeToo)
    {
        var handle = NativeHandle();
        if (handle == IntPtr.Zero)
        {
            if (_window is not null) _window.Position = new PixelPoint(x, y);
            return;
        }

        var flags = SwpNoActivate | (sizeToo ? 0 : SwpNoSize);
        SetWindowPos(handle, HwndTopmost, x, y, width, height, flags);
    }

    // The OS can hand back a different size than it was asked for (minimum
    // track size, DPI rounding). Re-derive x from the size it actually took so
    // the anchored edge stays flush, and refuse the correction outright if it
    // would put the badge on a different display than the one this notification
    // belongs to - that is the failure this class exists to make impossible, so
    // it is worth an assertion rather than a silent move.
    private void CorrectForActualSize(ClipOverlayTarget target, bool isLeft, PixelRect requested)
    {
        var handle = NativeHandle();
        if (handle == IntPtr.Zero || !GetWindowRect(handle, out var actual)) { _committedRect = requested; return; }

        var actualWidth = actual.Right - actual.Left;
        var actualHeight = actual.Bottom - actual.Top;
        if (actualWidth <= 0 || actualHeight <= 0) { _committedRect = requested; return; }

        var corrected = RectFor(target, isLeft, actualWidth, actualHeight);
        if (corrected.X == actual.Left && corrected.Y == actual.Top)
        {
            _committedRect = new PixelRect(actual.Left, actual.Top, actualWidth, actualHeight);
            return;
        }

        var centre = new PixelPoint(corrected.X + corrected.Width / 2, corrected.Y + corrected.Height / 2);
        if (!target.Bounds.Contains(centre))
        {
            AppLog.Error($"Clip overlay placement rejected: corrected rect {Describe(corrected)} leaves {target.DeviceName}; keeping {Describe(new PixelRect(actual.Left, actual.Top, actualWidth, actualHeight))}.");
            _committedRect = new PixelRect(actual.Left, actual.Top, actualWidth, actualHeight);
            return;
        }

        SetWindowPos(handle, HwndTopmost, corrected.X, corrected.Y, 0, 0, SwpNoSize | SwpNoActivate);
        _committedRect = corrected;
    }

    // A real DPI change is the only event that legitimately invalidates a
    // committed rect. Everything else that used to re-place the window
    // (Resized, PositionChanged) was a feedback loop with our own SetWindowPos.
    private void OnScalingChanged()
    {
        if (_parked || _visibleSession is null || _committedTarget is not { } target || _window is null || _showing is null) return;
        var scaling = _window.RenderScaling;
        if (scaling <= 0 || Math.Abs(scaling - target.Scaling) <= 0.01) return;

        AppLog.Info($"Clip overlay rescaled: id={_visibleSession.Id}, {target.Scaling:0.00} -> {scaling:0.00}, recommitting.");
        var updated = target with { Scaling = scaling };
        _committedTarget = updated;

        var isLeft = _committedSide == ClipOverlaySide.Left;
        var size = MeasureBadge(_showing, updated);
        var rect = RectFor(updated, isLeft, size.widthPixels, size.heightPixels);
        SetWindowSize(updated, size.widthPixels, size.heightPixels);
        MoveWindow(rect.X, rect.Y, rect.Width, rect.Height, sizeToo: true);
        _committedRect = rect;
    }

    // ---- lifecycle timers ------------------------------------------------

    private void StartDwell(int generation)
    {
        StopTimer(ref _dwellTimer);
        var dwell = new DispatcherTimer { Interval = DwellDuration };
        dwell.Tick += (_, _) =>
        {
            StopTimer(ref _dwellTimer);
            if (!_presentation.IsCurrent(generation)) return;
            _ = ExitGuardedAsync("dwell-elapsed");
        };
        _dwellTimer = dwell;
        dwell.Start();

        ArmWatchdog();
    }

    private async Task ExitGuardedAsync(string reason)
    {
        try
        {
            await ExitAsync(reason);
        }
        catch (OperationCanceledException)
        {
            // Superseded mid-exit; the new toast owns the window.
        }
        catch (Exception error)
        {
            AppLog.Error("Clip overlay exit failed", error);
            Park("exit-failed");
        }
    }

    private void ArmWatchdog()
    {
        StopTimer(ref _watchdogTimer);
        var watchdog = new DispatcherTimer
        {
            Interval = DwellDuration + EnterDuration + SwapDuration + ExitDuration + FrameBarrierTimeout + TimeSpan.FromMilliseconds(500)
        };
        watchdog.Tick += (_, _) =>
        {
            StopTimer(ref _watchdogTimer);
            if (_parked || _visibleSession is null) return;
            AppLog.Error($"Clip overlay watchdog: id={_visibleSession.Id} was still on screen past its exit; forcing park.");
            Park("watchdog");
        };
        _watchdogTimer = watchdog;
        watchdog.Start();
    }

    // Topmost at construction only puts the window in the topmost band once,
    // and a game that goes fullscreen afterwards enters that same band above
    // it. SWP_NOACTIVATE is what keeps this safe: SetWindowPos with
    // HWND_TOPMOST and NOACTIVATE never changes the foreground window, so the
    // game cannot lose focus to a badge re-asserting itself.
    private void StartTopmostReassert()
    {
        StopTimer(ref _topmostTimer);
        var timer = new DispatcherTimer { Interval = TopmostReassertInterval };
        timer.Tick += (_, _) =>
        {
            if (_parked) { StopTimer(ref _topmostTimer); return; }
            AssertTopmost();
        };
        _topmostTimer = timer;
        timer.Start();
    }

    private void AssertTopmost()
    {
        var handle = NativeHandle();
        if (handle == IntPtr.Zero) return;
        SetWindowPos(handle, HwndTopmost, 0, 0, 0, 0, SwpNoSize | SwpNoMove | SwpNoActivate);
    }

    private static void StopTimer(ref DispatcherTimer? timer)
    {
        timer?.Stop();
        timer = null;
    }

    // ---- construction ----------------------------------------------------

    private void EnsureWindow()
    {
        if (_window is not null)
        {
            if (!_window.IsVisible) ShowParked();
            return;
        }

        _showing = new BadgeLayer();
        _staging = new BadgeLayer();
        _staging.Root.IsVisible = false;

        _root = new Panel
        {
            Background = Brushes.Transparent,
            ClipToBounds = true,
            Children = { _showing.Root, _staging.Root }
        };

        _window = new Window
        {
            WindowDecorations = WindowDecorations.None,
            CanResize = false,
            ShowInTaskbar = false,
            ShowActivated = false,
            Topmost = true,
            Background = Brushes.Transparent,
            TransparencyLevelHint = [WindowTransparencyLevel.Transparent],
            // Manual: the size is computed in device pixels for the target
            // monitor and applied once. Letting Avalonia resize the window
            // during a toast re-entered the placement path every layout pass.
            SizeToContent = SizeToContent.Manual,
            WindowStartupLocation = WindowStartupLocation.Manual,
            Content = _root
        };

        _window.Opened += (_, _) =>
        {
            OverlayTransparencyDiagnostics.Log(_window, "clip-toast");
            // Applied against the first realised hwnd rather than after every
            // show: these are what stop the badge taking foreground from a
            // game. WS_EX_TOOLWINDOW matters now that the window is never
            // hidden - without it a permanently-visible window turns up in
            // alt-tab.
            AddExtendedStyle(WsExNoActivate | WsExTransparent | WsExToolWindow);
            if (WindowsPlatformProfile.IsServer())
            {
                _perPixel?.Dispose();
                _perPixel = new ServerPerPixelOverlay(_window!, _root!);
                // The mirror never moves any more: the slide lives inside
                // _root as per-layer transforms, so blitting the panel already
                // carries the motion. Offset stays at zero for the window's
                // whole life.
                _perPixel.SetPositionOffset(default);
                _perPixel.Hide();
                WindowTransparencyFallback.ApplyInputSurfaceIfNeeded(_window!);
            }
            else
            {
                WindowTransparencyFallback.ApplyIfNeeded(_window!, _showing!.Badge.Background, b =>
                {
                    _showing!.Badge.Background = b;
                    _staging!.Badge.Background = b;
                }, "clip-toast");
            }
        };
        _window.Closed += (_, _) =>
        {
            _perPixel?.Dispose();
            _perPixel = null;
        };
        _window.ScalingChanged += (_, _) => OnScalingChanged();

        ShowParked();
    }

    // Shown once and then left visible for the process lifetime - see the class
    // comment. Positioned off the virtual desktop first so this one unstyled,
    // uncomposited show happens where nobody can see it.
    private void ShowParked()
    {
        if (_window is null) return;
        _window.Position = ParkPosition;
        _window.Show();
        MoveWindow(ParkPosition.X, ParkPosition.Y, 0, 0, sizeToo: false);
        _parked = true;
    }

    private static string Describe(PixelRect rect) => $"{rect.X},{rect.Y} {rect.Width}x{rect.Height}";

    private static string DescribeForeground()
    {
        if (!OperatingSystem.IsWindows()) return "n/a";
        var foreground = GetForegroundWindow();
        if (foreground == IntPtr.Zero) return "none";
        _ = GetWindowThreadProcessId(foreground, out var pid);
        return $"0x{foreground.ToInt64():X8}(pid {pid})";
    }

    // One badge - the visual that slides. Two of these live in the window at
    // once so a new one can be prepared, laid out and rendered entirely off
    // screen before it replaces the one on show.
    private sealed class BadgeLayer
    {
        public BadgeLayer()
        {
            var accentBrush = (Application.Current?.Resources["AccentBrush"] as IBrush) ?? AppThemeService.Brush("Semantic_13C8B5", "#13C8B5");
            // A full-height accent stripe (not a small dot) plus a solid,
            // near-opaque background - meant to stand out at a glance over
            // gameplay. "AccentBrush" is the live OS-accent-tracking resource,
            // so the badge follows the colour picked in Windows.
            Accent = new Border
            {
                Width = 5,
                Background = accentBrush,
                VerticalAlignment = VerticalAlignment.Stretch
            };
            Label = new TextBlock
            {
                Foreground = AppThemeService.Brush("Text_F5F9FF", "#F5F9FF"),
                FontWeight = FontWeight.Bold,
                FontSize = 17,
                VerticalAlignment = VerticalAlignment.Center,
                // An auto-clip's label has no length cap of its own and the
                // badge sizes itself to whatever the label measures at;
                // unwrapped, a long one stretched most of the way across the
                // screen.
                MaxWidth = 340,
                TextWrapping = TextWrapping.Wrap
            };
            HintRow = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 6,
                VerticalAlignment = VerticalAlignment.Center,
                IsVisible = false
            };

            var icon = new Image
            {
                Width = 26,
                Height = 26,
                VerticalAlignment = VerticalAlignment.Center,
                Source = new Bitmap(Avalonia.Platform.AssetLoader.Open(new Uri("avares://ClypDat/Assets/clypdat-icon-48.png")))
            };

            Badge = new Border
            {
                Background = AppThemeService.Brush("Surface_F5141D24", "#F5141D24"),
                BorderBrush = AppThemeService.Brush("Surface_3C4C5A", "#3C4C5A"),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(8),
                BoxShadow = BoxShadows.Parse("0 10 28 0 #70000000"),
                ClipToBounds = true,
                Child = new DockPanel
                {
                    Children =
                    {
                        Accent,
                        new StackPanel
                        {
                            Orientation = Orientation.Horizontal,
                            Spacing = 14,
                            Margin = new Thickness(18, 16, 24, 16),
                            Children =
                            {
                                icon,
                                new StackPanel
                                {
                                    Orientation = Orientation.Vertical,
                                    Spacing = 7,
                                    VerticalAlignment = VerticalAlignment.Center,
                                    Children = { Label, HintRow }
                                }
                            }
                        }
                    }
                }
            };

            Root = new Border
            {
                Background = Brushes.Transparent,
                RenderTransform = TranslateBy(0),
                Child = Badge
            };
        }

        public Border Root { get; }
        public Border Badge { get; }
        public Border Accent { get; }
        public TextBlock Label { get; }
        public StackPanel HintRow { get; }
        public string Text { get; private set; } = string.Empty;

        // Where the layer currently sits, read back off the transform rather
        // than tracked separately: a cancelled animation leaves the interpolated
        // value in place, and that drawn position is what the next motion has to
        // start from if it is not going to jump.
        public double Offset => Root.RenderTransform?.Value.M31 ?? 0;

        public void SetOffset(double x) => Root.RenderTransform = TranslateBy(x);
        public double Width { get; set; }
        public double Height { get; set; }

        public void Apply(ClipOverlayRequest request, bool isLeft)
        {
            Text = request.Text;
            Label.Text = request.Text;
            var showHint = !string.IsNullOrWhiteSpace(request.Hotkey);
            if (showHint) BuildHint(request.Hotkey!, request.HotkeyHint);
            HintRow.IsVisible = showHint;

            // Square on the side touching the screen edge, rounded on the side
            // facing in - a fully rounded badge sitting flush reads as a gap,
            // since the curve pulls the fill away from the edge at the corners.
            Badge.CornerRadius = isLeft ? new CornerRadius(0, 8, 8, 0) : new CornerRadius(8, 0, 0, 8);
            Accent.CornerRadius = new CornerRadius(0);
            DockPanel.SetDock(Accent, isLeft ? Dock.Left : Dock.Right);
            // Hugs the anchored edge, so during a crossing the two badges stay
            // pinned to the same side while the window carries the wider of
            // the two.
            Root.HorizontalAlignment = isLeft ? HorizontalAlignment.Left : HorizontalAlignment.Right;
        }

        // "Ctrl+Shift+F9" -> [Ctrl] + [Shift] + [F9] as keycap chips, followed
        // by whatever the hint says the keys do. Built from the live setting
        // rather than hardcoded, so a rebound hotkey teaches the right keys.
        private void BuildHint(string hotkey, string trailingText)
        {
            HintRow.Children.Clear();

            var keys = hotkey.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            for (var i = 0; i < keys.Length; i++)
            {
                if (i > 0)
                {
                    HintRow.Children.Add(new TextBlock
                    {
                        Text = "+",
                        Foreground = AppThemeService.Brush("Text_8DA0B4", "#8DA0B4"),
                        FontSize = 13,
                        VerticalAlignment = VerticalAlignment.Center
                    });
                }

                HintRow.Children.Add(new Border
                {
                    Background = AppThemeService.Brush("Surface_2A323C", "#2A323C"),
                    BorderBrush = AppThemeService.Brush("Surface_49525E", "#49525E"),
                    BorderThickness = new Thickness(1),
                    CornerRadius = new CornerRadius(4),
                    Padding = new Thickness(7, 2, 7, 3),
                    VerticalAlignment = VerticalAlignment.Center,
                    Child = new TextBlock
                    {
                        Text = keys[i],
                        Foreground = AppThemeService.Brush("Text_E8EEF6", "#E8EEF6"),
                        FontWeight = FontWeight.Bold,
                        FontSize = 13
                    }
                });
            }

            HintRow.Children.Add(new TextBlock
            {
                Text = trailingText,
                Foreground = AppThemeService.Brush("Text_A8B8C8", "#A8B8C8"),
                FontSize = 13,
                Margin = new Thickness(2, 0, 0, 0),
                VerticalAlignment = VerticalAlignment.Center
            });
        }
    }

    // ---- native ----------------------------------------------------------

    private IntPtr NativeHandle() => _window?.TryGetPlatformHandle()?.Handle ?? IntPtr.Zero;

    private void AddExtendedStyle(long styles)
    {
        var handle = NativeHandle();
        if (handle == IntPtr.Zero) return;
        var exStyle = (long)GetWindowLongPtr(handle, GwlExStyle);
        if ((exStyle & styles) == styles) return;
        SetWindowLongPtr(handle, GwlExStyle, (IntPtr)(exStyle | styles));
    }

    // Excluded from capture entirely - the window still renders on the physical
    // display, it just is not composited into anything that asks DWM for the
    // screen. Applied per notification because the setting is live and the flag
    // has to be clearable again.
    private void ApplyCaptureExclusion(bool exclude)
    {
        var handle = NativeHandle();
        if (handle == IntPtr.Zero) return;
        if (!SetWindowDisplayAffinity(handle, exclude ? WdaExcludeFromCapture : WdaNone) && exclude)
        {
            AppLog.Debug($"Overlay capture exclusion unavailable (needs Windows 10 build 19041): error={Marshal.GetLastWin32Error()}.");
        }
    }

    private static readonly IntPtr HwndTopmost = new(-1);
    private const uint SwpNoSize = 0x0001;
    private const uint SwpNoMove = 0x0002;
    private const uint SwpNoActivate = 0x0010;
    private const int GwlExStyle = -20;
    private const long WsExNoActivate = 0x08000000L;
    private const long WsExTransparent = 0x00000020L;
    private const long WsExToolWindow = 0x00000080L;
    private const uint WdaNone = 0x00000000;
    private const uint WdaExcludeFromCapture = 0x00000011;

    [StructLayout(LayoutKind.Sequential)]
    private struct Win32Rect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetWindowPos(IntPtr window, IntPtr insertAfter, int x, int y, int cx, int cy, uint flags);

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr window, out Win32Rect rect);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr GetWindowLongPtr(IntPtr window, int index);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetWindowLongPtr(IntPtr window, int index, IntPtr value);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetWindowDisplayAffinity(IntPtr window, uint affinity);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);
}
