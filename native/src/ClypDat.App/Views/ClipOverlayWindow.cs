using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Animation;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
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
// reused for the process lifetime, pinned flush to one monitor's edge, sliding
// in from that edge and back out again.
//
// Built once and reused because each notification used to construct and destroy
// a brand-new transparent topmost Window, so a single save churned two or three
// compositor surfaces in about two seconds - real GPU/DWM work landing exactly
// when the machine is busiest, and the most likely reason a save made the mouse
// feel choppy. Show/Hide costs none of that.
internal sealed class ClipOverlayWindow : IDisposable
{
    private const double TopMarginDips = 24;
    private static readonly TimeSpan SlideDuration = TimeSpan.FromMilliseconds(260);
    private static readonly TimeSpan DwellDuration = TimeSpan.FromMilliseconds(2200);
    private static readonly TimeSpan TopmostReassertInterval = TimeSpan.FromMilliseconds(500);

    private readonly Action _playSound;

    private Window? _window;
    private Border? _root;
    private Border? _badge;
    private Border? _accent;
    private TextBlock? _label;
    private StackPanel? _hintRow;
    private TranslateTransform? _translate;
    private ServerPerPixelOverlay? _perPixel;

    private DispatcherTimer? _dwellTimer;
    private DispatcherTimer? _entryTimer;
    private DispatcherTimer? _exitTimer;
    private DispatcherTimer? _topmostTimer;
    private DispatcherTimer? _watchdogTimer;

    private ClipOverlaySession? _visibleSession;
    private ClipOverlayTarget? _committedTarget;
    private PixelRect _committedRect;
    private ClipOverlaySide _committedSide;
    private DateTime _shownAtUtc;
    private double _travelDips;
    private bool _windowCloaked;
    private readonly ClipOverlayPresentationState _presentation = new();
    private int _presentationGeneration;

    // Server-SKU manual animation state. On workstation SKUs the slide is a
    // plain Avalonia transition on the translate; on Server the badge is
    // mirrored into a native layered window and the offset has to be stepped by
    // hand.
    private int _animationId;
    private double _animationStart;
    private double _animationTarget;
    private double _offset;
    private Action? _animationComplete;

    public ClipOverlayWindow(Action playSound) => _playSound = playSound;

    public bool IsShowing => _visibleSession is not null;

    // Carries the session that was taken down, not just "something hid":
    // hiding a superseded toast happens AFTER the next one's session already
    // exists, so the listener has to be able to tell the two apart.
    public event EventHandler<ClipOverlaySession>? Hidden;

    // Realises the window once, at startup, off the side of the virtual desktop.
    //
    // Two reasons this is not left to the first notification. Avalonia's Measure
    // falls back to different (observed: wider) text metrics for a control that
    // has never been attached to a TopLevel, so the session's first badge came
    // out visibly elongated. And the WS_EX_NOACTIVATE / WS_EX_TRANSPARENT styles
    // can only be applied to a real hwnd, which means the very first Show of a
    // reused window is the one Show that happens without them - previously
    // mid-game, where a window taking foreground for even one poll tick makes
    // the capture log "recording paused (window not foreground)" and stop
    // recording. Doing it here spends that one unstyled show at launch on an
    // off-screen position instead.
    public void Warm()
    {
        EnsureWindow();
        if (_window is null || _window.IsVisible) return;
        _window.Position = new PixelPoint(-32000, -32000);
        _window.Show();
        _window.Hide();
    }

    public void Show(ClipOverlayRequest request)
    {
        EnsureWindow();
        if (_window is null || _badge is null || _root is null || _label is null || _accent is null || _translate is null) return;

        var target = request.Session.Target;
        var isLeft = request.Side == ClipOverlaySide.Left;

        if (_visibleSession is { } visible && ReferenceEquals(visible, request.Session))
        {
            var disposition = _presentation.Update(_presentationGeneration, request);
            if (disposition == ClipOverlayUpdateDisposition.Queue)
            {
                AppLog.Info($"Clip overlay update queued: id={visible.Id}, text='{request.Text}', phase={_presentation.Phase}.");
                return;
            }

            if (disposition == ClipOverlayUpdateDisposition.Apply) ApplyUpdate(request, target, isLeft);
            if (disposition != ClipOverlayUpdateDisposition.Restart) return;
            HideNow("update-during-exit");
        }

        if (_visibleSession is not null) HideNow("superseded");

        StopTimer(ref _dwellTimer);
        StopTimer(ref _entryTimer);
        StopTimer(ref _exitTimer);
        StopTimer(ref _watchdogTimer);
        StopAnimation();

        // Hide the old compositor surface before changing text on the reused HWND.
        _windowCloaked = StartupWindowPresentation.TryCloak(_window);
        if (!_windowCloaked) _window.Opacity = 0;
        _perPixel?.Hide();
        ApplyRequestAppearance(request, isLeft);
        ApplyCaptureExclusion(request.ExcludeFromCapture);

        var handle = NativeHandle();
        // Hand the still-hidden window to the target monitor BEFORE anything is
        // measured. A hidden window still receives WM_DPICHANGED on a
        // cross-monitor move, so this is what gives the hwnd the target's DPI
        // context - without it the DIP size below is converted against the
        // monitor the window was last shown on, which across a 100%/150% pair
        // is a badge either a third too narrow or half again too wide.
        if (handle != IntPtr.Zero)
        {
            SetWindowPos(handle, HwndTopmost, target.WorkArea.X, target.WorkArea.Y, 0, 0, SwpNoSize | SwpNoActivate);
        }

        _badge.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        var desired = _badge.DesiredSize;
        _travelDips = desired.Width;

        var widthPixels = (int)Math.Ceiling(desired.Width * target.Scaling);
        var heightPixels = (int)Math.Ceiling(desired.Height * target.Scaling);
        _window.Width = widthPixels / target.Scaling;
        _window.Height = heightPixels / target.Scaling;

        var rect = RectFor(target, isLeft, widthPixels, heightPixels);
        _committedTarget = target;
        _committedSide = isLeft ? ClipOverlaySide.Left : ClipOverlaySide.Right;
        _committedRect = rect;

        var transitions = _translate.Transitions;
        _translate.Transitions = null;
        SetOffset(isLeft ? -_travelDips : _travelDips);

        _window.Show();

        handle = NativeHandle();
        if (handle != IntPtr.Zero)
        {
            // One authoritative placement, synchronous with Show() so it lands
            // before the first paint. Nothing moves the window after this: the
            // slide is the inner transform inside a window sized exactly to the
            // badge, and the badge is clipped at the window's own bounds.
            SetWindowPos(handle, HwndTopmost, rect.X, rect.Y, rect.Width, rect.Height, SwpNoActivate);
            CorrectForActualSize(handle, target, isLeft, rect);
        }

        _visibleSession = request.Session;
        _presentationGeneration = _presentation.Begin();
        _shownAtUtc = DateTime.UtcNow;
        StartTopmostReassert();
        ArmWatchdog();

        AppLog.Info(
            $"Clip overlay requested: id={request.Session.Id}, trigger={request.Session.Trigger}, text='{request.Text}', " +
            $"side={(isLeft ? "left" : "right")}, monitor={target.DeviceName} ({ClipOverlayTargeting.LabelFor(target.DeviceName)}), " +
            $"reason={target.ReasonLabel}, work={Describe(target.WorkArea)}, scaling={target.Scaling:0.00}, " +
            $"requested={Describe(rect)}, actual={Describe(_committedRect)}, foreground={DescribeForeground()}.");

        var generation = _presentationGeneration;
        var topLevel = _window as TopLevel;
        if (topLevel is null) return;
        topLevel.RequestAnimationFrame(_ =>
        {
            if (!IsCurrentPresentation(request.Session, generation)) return;
            topLevel.RequestAnimationFrame(_ => BeginPreparedEntry(request, transitions, generation));
        });
    }

    public void HideNow(string reason)
    {
        StopTimer(ref _dwellTimer);
        StopTimer(ref _entryTimer);
        StopTimer(ref _exitTimer);
        StopTimer(ref _topmostTimer);
        StopTimer(ref _watchdogTimer);
        StopAnimation();

        var session = _visibleSession;
        _presentation.Hide();
        _presentationGeneration = 0;
        _visibleSession = null;
        _committedTarget = null;

        _perPixel?.Hide();
        _window?.Hide();
        RestoreWindowPresentation();

        if (session is not null)
        {
            AppLog.Info($"Clip overlay hidden: id={session.Id}, reason={reason}, visible={(DateTime.UtcNow - _shownAtUtc).TotalSeconds:0.00}s.");
            Hidden?.Invoke(this, session);
        }
    }

    public void Dispose()
    {
        HideNow("disposed");
        _perPixel?.Dispose();
        _perPixel = null;
        _window?.Close();
        _window = null;
    }

    private void ApplyRequestAppearance(ClipOverlayRequest request, bool isLeft)
    {
        if (_label is null || _badge is null || _accent is null) return;
        _label.Text = request.Text;
        var showHint = !string.IsNullOrWhiteSpace(request.Hotkey);
        if (showHint) BuildHint(request.Hotkey!, request.HotkeyHint);
        if (_hintRow is not null) _hintRow.IsVisible = showHint;
        _badge.CornerRadius = isLeft ? new CornerRadius(0, 8, 8, 0) : new CornerRadius(8, 0, 0, 8);
        _accent.CornerRadius = new CornerRadius(0);
        DockPanel.SetDock(_accent, isLeft ? Dock.Left : Dock.Right);
    }

    private void ApplyUpdate(ClipOverlayRequest request, ClipOverlayTarget target, bool isLeft)
    {
        ApplyRequestAppearance(request, isLeft);
        ApplyCaptureExclusion(request.ExcludeFromCapture);
        ResizeInPlace(target, isLeft);
        AppLog.Info($"Clip overlay update applied: id={request.Session.Id}, text='{request.Text}', rect={Describe(_committedRect)}.");
        if (request.PlaySound) _playSound();
        StartDwell();
    }

    private bool IsCurrentPresentation(ClipOverlaySession session, int generation) =>
        ReferenceEquals(_visibleSession, session) && _presentation.IsCurrent(generation);

    private void BeginPreparedEntry(ClipOverlayRequest request, Transitions? transitions, int generation)
    {
        if (_translate is null || !IsCurrentPresentation(request.Session, generation) || !_presentation.BeginEntry(generation)) return;

        RestoreWindowPresentation();
        if (WindowsPlatformProfile.IsServer())
        {
            _perPixel?.ShowAndRefresh();
            _perPixel?.SetCaptureExcluded(request.ExcludeFromCapture);
            StartAnimation(0, () => CompleteEntry(request.Session, generation));
        }
        else
        {
            _translate.Transitions = transitions;
            _translate.X = 0;
            StartEntryTimer(request.Session, generation);
        }

        if (request.PlaySound) _playSound();
        AppLog.Debug($"Clip overlay entry began: id={request.Session.Id}, text='{request.Text}', generation={generation}.");
        StartDwell();
    }

    private void StartEntryTimer(ClipOverlaySession session, int generation)
    {
        StopTimer(ref _entryTimer);
        var timer = new DispatcherTimer { Interval = SlideDuration };
        timer.Tick += (_, _) =>
        {
            StopTimer(ref _entryTimer);
            CompleteEntry(session, generation);
        };
        _entryTimer = timer;
        timer.Start();
    }

    private void CompleteEntry(ClipOverlaySession session, int generation)
    {
        if (!IsCurrentPresentation(session, generation)) return;
        var queued = _presentation.CompleteEntry(generation);
        if (_presentation.Phase != ClipOverlayPresentationPhase.Dwelling) return;
        AppLog.Debug($"Clip overlay entry completed: id={session.Id}, generation={generation}.");
        if (queued is not null) ApplyUpdate(queued, queued.Session.Target, queued.Side == ClipOverlaySide.Left);
    }

    private void RestoreWindowPresentation()
    {
        if (_window is null) return;
        if (_windowCloaked) StartupWindowPresentation.Reveal(_window);
        else _window.Opacity = 1;
        _windowCloaked = false;
    }

    // ---- placement -------------------------------------------------------

    private PixelRect RectFor(ClipOverlayTarget target, bool isLeft, int widthPixels, int heightPixels)
    {
        var area = target.WorkArea;
        var x = isLeft ? area.X : area.X + area.Width - widthPixels;
        x = Math.Clamp(x, area.X, Math.Max(area.X, area.X + area.Width - widthPixels));
        var y = area.Y + (int)Math.Round(TopMarginDips * target.Scaling);
        y = Math.Clamp(y, area.Y, Math.Max(area.Y, area.Y + area.Height - heightPixels));
        return new PixelRect(x, y, widthPixels, heightPixels);
    }

    // The OS can hand back a different size than it was asked for (minimum
    // track size, DPI rounding). Re-derive x from the size it actually took so
    // the anchored edge stays flush, and refuse the correction outright if it
    // would put the badge on a different display than the one this notification
    // belongs to - that is the failure this whole class exists to make
    // impossible, so it is worth an assertion rather than a silent move.
    private void CorrectForActualSize(IntPtr handle, ClipOverlayTarget target, bool isLeft, PixelRect requested)
    {
        if (!GetWindowRect(handle, out var actual)) { _committedRect = requested; return; }

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

    // Same clip, longer or shorter text. Resizes on the monitor already
    // committed to - never re-resolves, never crosses a display.
    private void ResizeInPlace(ClipOverlayTarget target, bool isLeft)
    {
        if (_window is null || _badge is null) return;
        _badge.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        var desired = _badge.DesiredSize;
        _travelDips = desired.Width;

        var widthPixels = (int)Math.Ceiling(desired.Width * target.Scaling);
        var heightPixels = (int)Math.Ceiling(desired.Height * target.Scaling);
        _window.Width = widthPixels / target.Scaling;
        _window.Height = heightPixels / target.Scaling;

        var rect = RectFor(target, isLeft, widthPixels, heightPixels);
        var handle = NativeHandle();
        if (handle == IntPtr.Zero) return;
        SetWindowPos(handle, HwndTopmost, rect.X, rect.Y, rect.Width, rect.Height, SwpNoActivate);
        _committedRect = rect;
    }

    // A real DPI change is the only event that legitimately invalidates a
    // committed rect. Everything else that used to re-place the window
    // (Resized, PositionChanged) was a feedback loop with our own SetWindowPos.
    private void OnScalingChanged()
    {
        if (_visibleSession is null || _committedTarget is not { } target || _window is null) return;
        var scaling = _window.RenderScaling;
        if (scaling <= 0 || Math.Abs(scaling - target.Scaling) <= 0.01) return;

        AppLog.Info($"Clip overlay rescaled: id={_visibleSession.Id}, {target.Scaling:0.00} -> {scaling:0.00}, recommitting.");
        var updated = target with { Scaling = scaling };
        _committedTarget = updated;
        ResizeInPlace(updated, _committedSide == ClipOverlaySide.Left);
    }

    // ---- lifecycle timers ------------------------------------------------

    private void StartDwell()
    {
        StopTimer(ref _dwellTimer);
        var dwell = new DispatcherTimer { Interval = DwellDuration };
        dwell.Tick += (_, _) =>
        {
            StopTimer(ref _dwellTimer);
            BeginExit();
        };
        _dwellTimer = dwell;
        dwell.Start();

        ArmWatchdog();
    }

    private void ArmWatchdog()
    {
        StopTimer(ref _watchdogTimer);
        var watchdog = new DispatcherTimer { Interval = DwellDuration + SlideDuration + SlideDuration + TimeSpan.FromMilliseconds(500) };
        watchdog.Tick += (_, _) =>
        {
            StopTimer(ref _watchdogTimer);
            if (_visibleSession is null) return;
            AppLog.Error($"Clip overlay watchdog: id={_visibleSession.Id} was still visible past its exit; forcing hide.");
            HideNow("watchdog");
        };
        _watchdogTimer = watchdog;
        watchdog.Start();
    }

    private void BeginExit()
    {
        if (_translate is null || !_presentation.BeginExit()) return;
        var exitOffset = _committedSide == ClipOverlaySide.Left ? -_travelDips : _travelDips;

        if (WindowsPlatformProfile.IsServer())
        {
            StartAnimation(exitOffset, () => HideNow("dwell-elapsed"));
            return;
        }

        _translate.X = exitOffset;
        StopTimer(ref _exitTimer);
        var exit = new DispatcherTimer { Interval = SlideDuration };
        exit.Tick += (_, _) =>
        {
            StopTimer(ref _exitTimer);
            HideNow("dwell-elapsed");
        };
        _exitTimer = exit;
        exit.Start();
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
            if (_visibleSession is null) { StopTimer(ref _topmostTimer); return; }
            var handle = NativeHandle();
            if (handle == IntPtr.Zero) return;
            SetWindowPos(handle, HwndTopmost, 0, 0, 0, 0, SwpNoSize | SwpNoMove | SwpNoActivate);
        };
        _topmostTimer = timer;
        timer.Start();
    }

    private static void StopTimer(ref DispatcherTimer? timer)
    {
        timer?.Stop();
        timer = null;
    }

    // ---- Server-SKU manual slide ----------------------------------------

    private void StartAnimation(double targetOffset, Action? completed = null)
    {
        if (_translate is null) return;

        StopAnimation();
        _animationStart = _offset;
        _animationTarget = targetOffset;
        _animationComplete = completed;

        // Frames come off the OVERLAY window, not the main window. The main
        // window is minimised to the tray for the entire time anyone is
        // playing, and a tray-minimised toplevel stops producing animation
        // frames - which on Server left the completion callback (and with it
        // the only call to HideNow) never running.
        var topLevel = _window as TopLevel;
        if (topLevel is null || Math.Abs(_animationStart - targetOffset) < 0.01)
        {
            SetOffset(targetOffset);
            var immediate = _animationComplete;
            _animationComplete = null;
            immediate?.Invoke();
            return;
        }

        var animationId = ++_animationId;
        TimeSpan? startTime = null;

        void Step(TimeSpan frameTime)
        {
            if (animationId != _animationId) return;
            startTime ??= frameTime;
            var progress = Math.Clamp((frameTime - startTime.Value).TotalMilliseconds / SlideDuration.TotalMilliseconds, 0, 1);
            var eased = 1 - Math.Pow(1 - progress, 3);
            SetOffset(_animationStart + (_animationTarget - _animationStart) * eased);
            if (progress < 1)
            {
                topLevel.RequestAnimationFrame(Step);
                return;
            }

            var complete = _animationComplete;
            StopAnimation();
            complete?.Invoke();
        }

        topLevel.RequestAnimationFrame(Step);
    }

    private void StopAnimation()
    {
        _animationId++;
        _animationComplete = null;
    }

    private void SetOffset(double offset)
    {
        if (_translate is null) return;
        // Snapped to the OVERLAY window's pixel grid. This used to round
        // against the main window's RenderScaling, which is the wrong monitor's
        // grid whenever the two windows are on displays with different DPI.
        var scaling = _window?.RenderScaling > 0 ? _window.RenderScaling : 1;
        _offset = Math.Round(offset * scaling) / scaling;
        if (WindowsPlatformProfile.IsServer())
        {
            _translate.X = 0;
            _perPixel?.SetPositionOffset(new Vector(_offset, 0));
        }
        else
        {
            _translate.X = _offset;
        }

        _perPixel?.Refresh();
    }

    // ---- construction ----------------------------------------------------

    private void EnsureWindow()
    {
        if (_window is not null) return;

        // A full-height accent stripe (not a small dot) plus a solid, near-
        // opaque background - meant to actually stand out at a glance over
        // gameplay. "AccentBrush" is the live, OS-accent-tracking resource, so
        // the badge follows the colour the user picked in Windows.
        var accentBrush = (Application.Current?.Resources["AccentBrush"] as IBrush) ?? AppThemeService.Brush("Semantic_13C8B5", "#13C8B5");
        _accent = new Border
        {
            Width = 5,
            Background = accentBrush,
            VerticalAlignment = VerticalAlignment.Stretch
        };
        _label = new TextBlock
        {
            Foreground = AppThemeService.Brush("Text_F5F9FF", "#F5F9FF"),
            FontWeight = FontWeight.Bold,
            FontSize = 17,
            VerticalAlignment = VerticalAlignment.Center,
            // An auto-clip's label has no length cap of its own and this badge
            // sizes itself to whatever the label measures at; unwrapped, a long
            // one stretched the badge most of the way across the screen.
            MaxWidth = 340,
            TextWrapping = TextWrapping.Wrap
        };
        // Second line, only populated when a message has a hotkey to teach.
        _hintRow = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 6,
            VerticalAlignment = VerticalAlignment.Center,
            IsVisible = false
        };
        var textColumn = new StackPanel
        {
            Orientation = Orientation.Vertical,
            Spacing = 7,
            VerticalAlignment = VerticalAlignment.Center,
            Children = { _label, _hintRow }
        };

        var icon = new Image
        {
            Width = 26,
            Height = 26,
            VerticalAlignment = VerticalAlignment.Center,
            Source = new Bitmap(Avalonia.Platform.AssetLoader.Open(new Uri("avares://ClypDat/Assets/clypdat-icon-48.png")))
        };

        var content = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 14,
            Margin = new Thickness(18, 16, 24, 16),
            Children = { icon, textColumn }
        };

        _translate = new TranslateTransform();
        _badge = new Border
        {
            Background = AppThemeService.Brush("Surface_F5141D24", "#F5141D24"),
            BorderBrush = AppThemeService.Brush("Surface_3C4C5A", "#3C4C5A"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            BoxShadow = BoxShadows.Parse("0 10 28 0 #70000000"),
            RenderTransform = _translate,
            ClipToBounds = true,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            Child = new DockPanel { Children = { _accent, content } }
        };
        _root = new Border
        {
            Background = Brushes.Transparent,
            ClipToBounds = true,
            Child = _badge
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
            // Manual, not SizeToContent.Height: the window's size is computed in
            // device pixels for the target monitor and applied once. Letting
            // Avalonia resize it during a toast re-entered the placement path on
            // every layout pass.
            SizeToContent = SizeToContent.Manual,
            WindowStartupLocation = WindowStartupLocation.Manual,
            Content = _root
        };

        _window.Opened += (_, _) =>
        {
            OverlayTransparencyDiagnostics.Log(_window, "clip-toast");
            // Applied here, against the first realised hwnd, rather than after
            // every Show: these are what stop the badge taking foreground from
            // a game, and applying them after Show left the very first one
            // exposed.
            MakeNonActivating();
            MakeClickThrough();
            if (WindowsPlatformProfile.IsServer())
            {
                _perPixel?.Dispose();
                _perPixel = new ServerPerPixelOverlay(_window!, _root!);
                _perPixel.SetPositionOffset(new Vector(_offset, 0));
                WindowTransparencyFallback.ApplyInputSurfaceIfNeeded(_window!);
            }
            else
            {
                WindowTransparencyFallback.ApplyIfNeeded(_window!, _badge!.Background, b => _badge!.Background = b, "clip-toast");
            }
        };
        _window.Closed += (_, _) =>
        {
            _perPixel?.Dispose();
            _perPixel = null;
        };
        _window.ScalingChanged += (_, _) => OnScalingChanged();

        // Movement only, deliberately no opacity transition: the badge slides in
        // and out from off screen and never fades. A cross-fade on top of the
        // slide is what made the old nudge read as "appears and disappears"
        // rather than as something arriving from the edge.
        _translate.Transitions =
        [
            new DoubleTransition
            {
                Property = TranslateTransform.XProperty,
                Duration = SlideDuration,
                Easing = new Avalonia.Animation.Easings.CubicEaseOut()
            }
        ];
    }

    // "Ctrl+Shift+F9" -> [Ctrl] + [Shift] + [F9] as keycap chips, followed by
    // whatever the hint says the keys do. Built from the live setting rather
    // than hardcoded, so a rebound hotkey teaches the right keys.
    private void BuildHint(string hotkey, string trailingText)
    {
        if (_hintRow is null) return;
        _hintRow.Children.Clear();

        var keys = hotkey.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        for (var i = 0; i < keys.Length; i++)
        {
            if (i > 0)
            {
                _hintRow.Children.Add(new TextBlock
                {
                    Text = "+",
                    Foreground = AppThemeService.Brush("Text_8DA0B4", "#8DA0B4"),
                    FontSize = 13,
                    VerticalAlignment = VerticalAlignment.Center
                });
            }

            _hintRow.Children.Add(new Border
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

        _hintRow.Children.Add(new TextBlock
        {
            Text = trailingText,
            Foreground = AppThemeService.Brush("Text_A8B8C8", "#A8B8C8"),
            FontSize = 13,
            Margin = new Thickness(2, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center
        });
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

    // ---- native ----------------------------------------------------------

    private IntPtr NativeHandle() => _window?.TryGetPlatformHandle()?.Handle ?? IntPtr.Zero;

    private void MakeNonActivating()
    {
        var handle = NativeHandle();
        if (handle == IntPtr.Zero) return;
        var exStyle = (long)GetWindowLongPtr(handle, GwlExStyle);
        if ((exStyle & WsExNoActivate) != 0) return;
        SetWindowLongPtr(handle, GwlExStyle, (IntPtr)(exStyle | WsExNoActivate));
    }

    private void MakeClickThrough()
    {
        var handle = NativeHandle();
        if (handle == IntPtr.Zero) return;
        var exStyle = (long)GetWindowLongPtr(handle, GwlExStyle);
        if ((exStyle & WsExTransparent) != 0) return;
        SetWindowLongPtr(handle, GwlExStyle, (IntPtr)(exStyle | WsExTransparent));
    }

    // Excluded from capture entirely - the window still renders on the physical
    // display, it just is not composited into anything that asks DWM for the
    // screen. Applied per show because the setting is live and the flag has to
    // be clearable again.
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
