using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Avalonia;
using ClypDat.App.Services;
using ClypDat.App.Views;
using Xunit;
using Xunit.Abstractions;

namespace ClypDat.App.Tests;

public sealed class NativeClipOverlaySurfaceTests(ITestOutputHelper output)
{
    [Fact]
    public void OneNoActivateHwndAtomicallyReplacesAndDisposes()
    {
        if (!OperatingSystem.IsWindows()) return;
        // No-activate means the overlay never becomes the foreground window. This
        // used to assert the foreground window stayed exactly what it was before
        // the test, which reads the live desktop: in a full run another test's
        // window, or anything the user clicks, changed it and failed this at
        // random. Checking the overlay itself never takes focus is the real rule.
        var target = ClipOverlayTargeting.ResolvePrimary();
        using var game = new BorderlessTopmostWindow(target.Bounds);
        target = target with { Window = game.Handle };
        var counters = new ClipOverlayCounters();
        using var surface = new NativeClipOverlaySurface(_ => new ClipOverlayFrame(300, 66, new byte[300 * 66 * 4]), counters: counters);
        var handle = surface.WindowHandle;
        Assert.NotEqual(IntPtr.Zero, handle);
        Assert.Equal("DirectComposition", surface.PresenterName);
        var style = GetWindowLongPtr(handle, -20).ToInt64();
        Assert.NotEqual(0, style & 0x00000008);
        Assert.NotEqual(0, style & 0x08000000);
        Assert.NotEqual(0, style & 0x00000020);
        Assert.NotEqual(0, style & 0x00200000);
        Assert.Equal(0, style & 0x00080000);
        Assert.NotEqual(handle, GetForegroundWindow());
        Assert.False(surface.TimerArmed); // Idle: nothing wakes the overlay thread.

        var results = new Results();
        surface.Publish(Presentation(1, true, target: target), results.Add);
        var first = results.Wait(1);
        Assert.True(first.Presented, first.Reason);
        Assert.NotNull(first.Report);
        Assert.Equal("DirectComposition", first.Report!.Backend);
        Assert.Equal("excluded", first.Report.Affinity);
        Assert.Equal("none", first.Report.Recovery);
        Assert.Equal(target.DeviceName, first.Report.Monitor);
        // The window carries the slide travel as well as the card, so it
        // starts one travel inward of where the card comes to rest.
        var layout = ClipOverlayLayout.Frame(target, ClipOverlayPlacement.TopRight, 300, 66);
        Assert.True(GetWindowRect(handle, out var rect) && rect.Left == layout.Window.X && rect.Top == layout.Window.Y);
        Assert.Equal(layout.Window.Width, rect.Right - rect.Left);
        Assert.True(IsWindowVisible(handle));
        Assert.True(IsAbove(handle, game.Handle));
        Assert.True(SpinWait.SpinUntil(() => game.RaiseAbove(handle), 1000));
        Assert.True(SpinWait.SpinUntil(() => IsAbove(handle, game.Handle), 1000));
        Assert.True(counters.TopmostRecoveries >= 1);
        Assert.NotEqual(handle, GetForegroundWindow());
        Assert.Equal(0u, Cloaked(handle));
        Assert.True(GetWindowDisplayAffinity(handle, out var affinity));
        Assert.Equal(0x11u, affinity);
        surface.Publish(Presentation(2, false, target: target), results.Add);
        var second = results.Wait(2);
        Assert.True(second.Presented, second.Reason);
        Assert.Equal("included", second.Report!.Affinity);
        Assert.Equal(handle, surface.WindowHandle);
        Assert.True(GetWindowDisplayAffinity(handle, out affinity));
        Assert.Equal(0u, affinity);
        Assert.NotEqual(handle, GetForegroundWindow());

        surface.Dismiss(2);
        Assert.True(SpinWait.SpinUntil(() => !IsWindowVisible(handle), 1000));
        Assert.True(SpinWait.SpinUntil(() => !surface.TimerArmed, 1000));
        surface.Dispose();
        Assert.Equal(IntPtr.Zero, surface.WindowHandle);
        Assert.NotEqual(handle, GetForegroundWindow());
    }

    [Fact]
    public void AnimationDestinationsStayInsideTargetWorkArea()
    {
        if (!OperatingSystem.IsWindows()) return;
        var presenter = new RecordingPresenter();
        using var surface = new NativeClipOverlaySurface(
            _ => new ClipOverlayFrame(390, 87, new byte[390 * 87 * 4]),
            _ => presenter, counters: new ClipOverlayCounters());
        var target = new ClipOverlayTarget("DISPLAY1", new PixelRect(0, 0, 3840, 2160), new PixelRect(0, 0, 3840, 2080), 1.5, ClipOverlayTargetReason.Primary);
        var final = ClipOverlayLayout.Position(target, ClipOverlayPlacement.TopRight, 390, 87);

        surface.Publish(Presentation(1, true, target: target), _ => { });
        Assert.True(SpinWait.SpinUntil(() => presenter.Frames.Any(frame => frame.Opacity >= 0.999), 1000));
        surface.Dismiss(1);
        Assert.True(SpinWait.SpinUntil(() => presenter.HideCount == 1, 1000));

        var frames = presenter.Frames;
        Assert.Contains(frames, frame => frame.Destination.X < final.X);
        Assert.Contains(frames, frame => frame.Destination.X == final.X && frame.Opacity >= 0.999);
        Assert.All(frames, frame =>
        {
            Assert.InRange(frame.Destination.X, target.WorkArea.X, target.WorkArea.Right - frame.Width);
            Assert.InRange(frame.Destination.Y, target.WorkArea.Y, target.WorkArea.Bottom - frame.Height);
        });
    }

    // A wrong coefficient here does not throw - it leaves the badge invisible
    // or parked off its resting position - so the polynomial is checked against
    // the easing it replaces, computed independently.
    [Fact]
    public void CubicCoefficientsReproduceTheEasingsTheyReplace()
    {
        const double duration = 0.22;
        foreach (var (from, to, easeOut) in new[] { (0d, 1d, true), (0.4, 1d, true), (1d, 0d, false), (0.6, 0d, false) })
        {
            var curve = ClipOverlayAnimationCurve.Build(from, to, duration, easeOut);
            foreach (var fraction in new[] { 0d, 0.25, 0.5, 0.75, 1d })
            {
                var eased = easeOut ? 1 - Math.Pow(1 - fraction, 3) : fraction * fraction * fraction;
                // The coefficients are float, so compare with a tolerance
                // rather than by rounding to decimal places.
                Assert.Equal(from + (to - from) * eased, curve.Sample(duration * fraction), 1e-5);
            }
        }

        // A zero-length motion is a straight set to the destination value.
        var instant = ClipOverlayAnimationCurve.Build(0, 1, 0, true);
        Assert.Equal(1, instant.Sample(0), 1e-5);
        Assert.Equal(1, instant.Sample(10), 1e-5);
    }

    // The whole point of the compositor path: one handover per motion, one
    // upload per card, and no per-frame work while the badge dwells.
    [Fact]
    public void CompositorPathHandsOverWholeMotionsInsteadOfFrames()
    {
        if (!OperatingSystem.IsWindows()) return;
        var presenter = new AnimatingPresenter();
        using var surface = new NativeClipOverlaySurface(
            _ => new ClipOverlayFrame(300, 66, new byte[300 * 66 * 4]),
            _ => presenter, counters: new ClipOverlayCounters());
        var target = Presentation(1, true).Event.Target;
        var layout = ClipOverlayLayout.Frame(target, ClipOverlayPlacement.TopRight, 300, 66);

        var results = new Results();
        surface.Publish(Presentation(1, true), results.Add);
        Assert.True(SpinWait.SpinUntil(() => presenter.Motions.Count == 1, 1000));
        var enter = presenter.Motions[0];
        Assert.Equal(layout.Window.X, enter.WindowX);
        Assert.Equal(layout.Window.Width, enter.WindowWidth);
        Assert.Equal(0, enter.FromOpacity);
        Assert.Equal(1, enter.ToOpacity);
        Assert.Equal(layout.HiddenOffsetX, enter.FromOffsetX);
        Assert.Equal(layout.RestOffsetX, enter.ToOffsetX);
        Assert.True(enter.EaseOut);
        Assert.Equal(1, presenter.Uploads);
        Assert.True(results.Wait(1).Presented);

        // Two reasserts is past 500ms of dwell, by which point a 15ms per-frame
        // loop would have run about 30 times. Nothing more may be handed over.
        Assert.True(SpinWait.SpinUntil(() => presenter.Reasserts >= 2, 2000), "The badge still has to be kept above a fullscreen game.");
        Assert.Single(presenter.Motions);
        Assert.Equal(1, presenter.Uploads);

        surface.Dismiss(1);
        Assert.True(SpinWait.SpinUntil(() => presenter.Motions.Count == 2, 1000));
        var exit = presenter.Motions[1];
        Assert.Equal(1, exit.FromOpacity, 1e-3);
        Assert.Equal(0, exit.ToOpacity);
        Assert.Equal(layout.HiddenOffsetX, exit.ToOffsetX);
        Assert.False(exit.EaseOut);
        Assert.True(SpinWait.SpinUntil(() => presenter.HideCount == 1, 1000));
        // Hidden: the reassert timer stops with it.
        Assert.True(SpinWait.SpinUntil(() => !surface.TimerArmed, 1000));
        var reasserts = presenter.Reasserts;
        Thread.Sleep(600);
        Assert.Equal(reasserts, presenter.Reasserts);
    }

    [Fact]
    public void SameWorkflowStageUpdateKeepsTheCardVisible()
    {
        if (!OperatingSystem.IsWindows()) return;
        var presenter = new RecordingPresenter();
        using var surface = new NativeClipOverlaySurface(
            presentation => new ClipOverlayFrame(300, 66, Enumerable.Repeat((byte)presentation.Event.Stage, 300 * 66 * 4).ToArray()),
            _ => presenter, counters: new ClipOverlayCounters());
        var workflow = Guid.NewGuid();
        var results = new Results();

        surface.Publish(Presentation(1, true, workflow, 0), results.Add);
        Assert.True(results.Wait(1).Presented);
        Assert.True(SpinWait.SpinUntil(() => presenter.Frames.Any(frame => frame.Opacity >= .999), 1000));
        var before = presenter.Frames.Count;

        surface.Publish(Presentation(2, true, workflow, 1), results.Add);
        Assert.True(results.Wait(2).Presented);
        var updateFrames = presenter.Frames.Skip(before).ToArray();
        Assert.Contains(updateFrames, frame => frame.FrameMarker == 1 && frame.FrameChanged && frame.Opacity >= .999);
        Assert.DoesNotContain(updateFrames, frame => frame.Opacity <= 0.001);
        Assert.Equal(0, presenter.HideCount);
    }

    // A stage that replaces one still being verified answers for it at once.
    [Fact]
    public void StageArrivingDuringVerificationSupersedesIt()
    {
        if (!OperatingSystem.IsWindows()) return;
        var presenter = new AnimatingPresenter();
        using var surface = new NativeClipOverlaySurface(_ => new ClipOverlayFrame(300, 66, new byte[300 * 66 * 4]), _ => presenter,
            counters: new ClipOverlayCounters(), verifyDelayMs: 400);
        var workflow = Guid.NewGuid();
        var results = new Results();
        surface.Publish(Presentation(1, true, workflow, 0), results.Add);
        Assert.True(SpinWait.SpinUntil(() => presenter.Motions.Count == 1, 1000));
        surface.Publish(Presentation(2, true, workflow, 1), results.Add);
        var saving = results.Wait(1);
        Assert.False(saving.Presented);
        Assert.Equal("superseded-by-stage", saving.Reason);
        Assert.True(results.Wait(2).Presented);
        Assert.Equal(0, presenter.HideCount);
    }

    // Presented means seen: a window that is not on screen is re-presented,
    // then the compositor rebuilt, then the layered presenter tried, and
    // only then reported as failed. Never more than that.
    [Fact]
    public void VerificationRecoversInBoundedStepsThenReportsFailure()
    {
        if (!OperatingSystem.IsWindows()) return;
        var created = 0;
        var counters = new ClipOverlayCounters();
        var layered = new AnimatingPresenter { Layered = true, FailVerifications = int.MaxValue };
        using var surface = new NativeClipOverlaySurface(_ => new ClipOverlayFrame(300, 66, new byte[300 * 66 * 4]),
            _ => { created++; return new AnimatingPresenter { FailVerifications = int.MaxValue }; },
            _ => layered, counters, verifyDelayMs: 20);
        var results = new Results();
        surface.Publish(Presentation(1, true), results.Add);
        var result = results.Wait(1, 3000);
        Assert.False(result.Presented);
        Assert.Equal("window-not-visible-after-recovery", result.Reason);
        Assert.Equal("window-not-visible+reassert+window-not-visible+rebuild+window-not-visible+layered+window-not-visible", result.Report!.Recovery);
        Assert.Equal(2, created);
        Assert.Equal(1, counters.DirectCompositionRebuilds);
        Assert.Equal(1, counters.LayeredFallbacks);
        Assert.Equal(3, counters.VerificationRecoveries);
        Assert.True(SpinWait.SpinUntil(() => layered.HideCount == 1, 1000));
    }

    [Fact]
    public void VerificationFailureRecoveredByReassertIsPresented()
    {
        if (!OperatingSystem.IsWindows()) return;
        var presenter = new AnimatingPresenter { FailVerifications = 1 };
        var counters = new ClipOverlayCounters();
        using var surface = new NativeClipOverlaySurface(_ => new ClipOverlayFrame(300, 66, new byte[300 * 66 * 4]), _ => presenter,
            counters: counters, verifyDelayMs: 20);
        var results = new Results();
        surface.Publish(Presentation(1, true), results.Add);
        var result = results.Wait(1);
        Assert.True(result.Presented, result.Reason);
        Assert.Equal("window-not-visible+reassert", result.Report!.Recovery);
        Assert.Equal(1, counters.VerificationRecoveries);
        Assert.Equal(0, counters.DirectCompositionRebuilds);
    }

    [Fact]
    public void FirstCompositorPresentFailureRebuildsOnce()
    {
        if (!OperatingSystem.IsWindows()) return;
        var presenters = new List<AnimatingPresenter>();
        var counters = new ClipOverlayCounters();
        using var surface = new NativeClipOverlaySurface(_ => new ClipOverlayFrame(300, 66, new byte[300 * 66 * 4]),
            _ => { var presenter = new AnimatingPresenter { ThrowOnAnimate = presenters.Count == 0 }; presenters.Add(presenter); return presenter; },
            counters: counters, verifyDelayMs: 20);
        var results = new Results();
        surface.Publish(Presentation(1, true), results.Add);
        var result = results.Wait(1);
        Assert.True(result.Presented, result.Reason);
        Assert.Equal(2, presenters.Count);
        Assert.Equal(1, counters.DirectCompositionRebuilds);
        Assert.Equal(0, counters.LayeredFallbacks);
        Assert.Contains("rebuild", result.Report!.Recovery);
    }

    [Fact]
    public void CompositorRebuildFailureFallsBackToLayered()
    {
        if (!OperatingSystem.IsWindows()) return;
        var created = 0;
        var counters = new ClipOverlayCounters();
        var layered = new RecordingPresenter { Layered = true };
        using var surface = new NativeClipOverlaySurface(_ => new ClipOverlayFrame(300, 66, new byte[300 * 66 * 4]),
            _ => ++created == 1 ? new AnimatingPresenter { ThrowOnAnimate = true } : throw new InvalidOperationException("DirectComposition unavailable"),
            _ => layered, counters, verifyDelayMs: 20);
        var results = new Results();
        surface.Publish(Presentation(1, true), results.Add);
        var result = results.Wait(1);
        Assert.True(result.Presented, result.Reason);
        Assert.Equal("recording", result.Report!.Backend);
        Assert.Equal(1, counters.LayeredFallbacks);
        Assert.Equal("recording", surface.PresenterName);
        Assert.True(SpinWait.SpinUntil(() => layered.Frames.Any(frame => frame.Opacity >= .999), 1000));
    }

    [Fact]
    public void PresenterLostWhileVisibleIsRebuiltAndRepresented()
    {
        if (!OperatingSystem.IsWindows()) return;
        var presenters = new List<AnimatingPresenter>();
        var counters = new ClipOverlayCounters();
        using var surface = new NativeClipOverlaySurface(_ => new ClipOverlayFrame(300, 66, new byte[300 * 66 * 4]),
            _ => { var presenter = new AnimatingPresenter(); lock (presenters) presenters.Add(presenter); return presenter; },
            counters: counters, verifyDelayMs: 20);
        var results = new Results();
        surface.Publish(Presentation(1, true), results.Add);
        Assert.True(results.Wait(1).Presented);
        AnimatingPresenter first; lock (presenters) first = presenters[0];
        first.Healthy = false;
        Assert.True(SpinWait.SpinUntil(() => { lock (presenters) return presenters.Count == 2; }, 2000));
        AnimatingPresenter second; lock (presenters) second = presenters[1];
        Assert.True(SpinWait.SpinUntil(() => second.Motions.Any(motion => motion.ToOpacity >= .999), 1000));
        Assert.Equal(1, counters.DirectCompositionRebuilds);
    }

    [Fact]
    public void EveryPublishGetsExactlyOneAnswer()
    {
        if (!OperatingSystem.IsWindows()) return;
        var presenter = new AnimatingPresenter();
        var results = new Results();
        var surface = new NativeClipOverlaySurface(presentation => presentation.Generation == 3
                ? throw new InvalidOperationException("raster")
                : new ClipOverlayFrame(300, 66, new byte[300 * 66 * 4]),
            _ => presenter, counters: new ClipOverlayCounters(), verifyDelayMs: 20);
        surface.Publish(Presentation(2, true), results.Add);
        surface.Publish(Presentation(1, true), results.Add); // older than one already sent
        surface.Publish(Presentation(3, true), results.Add); // rasterization fails
        Assert.True(results.Wait(2).Presented);
        Assert.Equal("stale-generation", results.Wait(1).Reason);
        Assert.Equal("render-failed:InvalidOperationException", results.Wait(3).Reason);
        surface.Dismiss(5);
        surface.Publish(Presentation(4, true), results.Add); // already dismissed
        Assert.Equal("dismissed-before-accept", results.Wait(4).Reason);
        surface.Dispose();
        surface.Publish(Presentation(6, true), results.Add);
        Assert.Equal("surface-disposed", results.Wait(6).Reason);
        Assert.Equal(5, results.Count);
    }

    // The window destroyed from under the surface is recreated on its thread,
    // the card on screen comes back, and the next notification works.
    [Fact]
    public void LostWindowIsRecreatedAndTheCardComesBack()
    {
        if (!OperatingSystem.IsWindows()) return;
        var counters = new ClipOverlayCounters();
        var target = ClipOverlayTargeting.ResolvePrimary();
        using var surface = new NativeClipOverlaySurface(_ => new ClipOverlayFrame(300, 66, new byte[300 * 66 * 4]), counters: counters, verifyDelayMs: 40);
        var results = new Results();
        surface.Publish(Presentation(1, true, target: target), results.Add);
        Assert.True(results.Wait(1).Presented);
        var lost = surface.WindowHandle;
        surface.LoseWindowForTest();
        Assert.True(SpinWait.SpinUntil(() => counters.WindowRecreations == 1 && surface.WindowHandle != lost && surface.WindowHandle != 0, 2000));
        Assert.True(SpinWait.SpinUntil(() => IsWindowVisible(surface.WindowHandle), 1000));
        Assert.False(IsWindow(lost));
        surface.Publish(Presentation(2, true, target: target), results.Add);
        var next = results.Wait(2);
        Assert.True(next.Presented, next.Reason);
        Assert.NotEqual(surface.WindowHandle, GetForegroundWindow());
    }

    // A borderless topmost "game" that raises itself above everything every
    // 20ms cannot keep the badge under it, and fighting it never activates,
    // moves, re-rasterizes or re-publishes the notification.
    [Fact]
    public void GameThatKeepsRaisingItselfCannotCoverTheBadge()
    {
        if (!OperatingSystem.IsWindows()) return;
        var target = ClipOverlayTargeting.ResolvePrimary();
        using var game = new BorderlessTopmostWindow(target.Bounds);
        target = target with { Window = game.Handle };
        var counters = new ClipOverlayCounters();
        using var surface = new NativeClipOverlaySurface(_ => new ClipOverlayFrame(300, 66, new byte[300 * 66 * 4]), counters: counters, verifyDelayMs: 40);
        var results = new Results();
        surface.Publish(Presentation(1, true, target: target), results.Add);
        Assert.True(results.Wait(1).Presented);
        var handle = surface.WindowHandle;
        Assert.True(GetWindowRect(handle, out var rest));
        var renders = surface.RenderCount;
        var publishes = surface.PublishCount;

        using var raising = game.KeepRaising(TimeSpan.FromMilliseconds(20));
        var watch = Stopwatch.StartNew();
        var above = 0; var samples = 0; var longestBelow = TimeSpan.Zero; var belowSince = (TimeSpan?)null;
        while (watch.Elapsed < TimeSpan.FromSeconds(2))
        {
            var isAbove = IsAbove(handle, game.Handle);
            samples++;
            if (isAbove) { above++; if (belowSince is { } since) longestBelow = Max(longestBelow, watch.Elapsed - since); belowSince = null; }
            else belowSince ??= watch.Elapsed;
            Assert.NotEqual(handle, GetForegroundWindow());
            Thread.Sleep(5);
        }
        raising.Dispose();
        if (belowSince is { } open) longestBelow = Max(longestBelow, watch.Elapsed - open);
        output.WriteLine($"above {above}/{samples} samples, longest below {longestBelow.TotalMilliseconds:F0}ms, recoveries {counters.TopmostRecoveries}, raises {game.Raises}");
        Assert.True(game.Raises > 50);
        Assert.True(counters.TopmostRecoveries >= 3, $"recoveries={counters.TopmostRecoveries}");
        // Each raise is answered when it happens: the game can win a moment,
        // never the dwell.
        Assert.True(above * 2 >= samples, $"above {above}/{samples}");
        Assert.True(longestBelow < TimeSpan.FromMilliseconds(300), $"longest below {longestBelow.TotalMilliseconds:F0}ms");
        Assert.True(SpinWait.SpinUntil(() => IsAbove(handle, game.Handle), 1000));
        Assert.True(GetWindowRect(handle, out var after));
        Assert.Equal(rest, after);
        Assert.Equal(renders, surface.RenderCount);
        Assert.Equal(publishes, surface.PublishCount);
        Assert.Equal(1, results.Count);
    }

    // The reassert and the compositor keep going while the thread that
    // publishes - the UI thread in the app - is stuck.
    [Fact]
    public void StalledCallerDoesNotStallThePresenter()
    {
        if (!OperatingSystem.IsWindows()) return;
        var target = ClipOverlayTargeting.ResolvePrimary();
        using var game = new BorderlessTopmostWindow(target.Bounds);
        target = target with { Window = game.Handle };
        var counters = new ClipOverlayCounters();
        using var surface = new NativeClipOverlaySurface(_ => new ClipOverlayFrame(300, 66, new byte[300 * 66 * 4]), counters: counters, verifyDelayMs: 40);
        var results = new Results();
        surface.Publish(Presentation(1, true, target: target), results.Add);
        Assert.True(results.Wait(1).Presented);
        using var raising = game.KeepRaising(TimeSpan.FromMilliseconds(100));
        var before = counters.TopmostRecoveries;
        Thread.Sleep(1200); // The caller is blocked; only the overlay thread runs.
        Assert.True(counters.TopmostRecoveries > before);
    }

    // Monitor targeting end to end: a game on a secondary monitor gets the
    // badge there, per-monitor DPI and all, and the next notification for the
    // primary moves it back.
    [Fact]
    public void GameOnSecondaryMonitorGetsTheBadgeThere()
    {
        if (!OperatingSystem.IsWindows()) return;
        var monitors = Monitors();
        var primary = ClipOverlayTargeting.ResolvePrimary();
        var secondary = monitors.FirstOrDefault(monitor => !string.Equals(monitor.DeviceName, primary.DeviceName, StringComparison.OrdinalIgnoreCase));
        if (secondary.DeviceName is null) { output.WriteLine("Single monitor: covered by ClipOverlayTargetingTests."); return; }
        using var game = new BorderlessTopmostWindow(secondary.Bounds);
        var target = ClipOverlayTargeting.Resolve(new ClipOverlayTargetHints(GameWindow: game.Handle));
        Assert.Equal(ClipOverlayTargetReason.GameWindow, target.Reason);
        Assert.Equal(secondary.DeviceName, target.DeviceName);
        Assert.Equal(game.Handle, target.Window);

        var counters = new ClipOverlayCounters();
        using var surface = new NativeClipOverlaySurface(_ => new ClipOverlayFrame(300, 66, new byte[300 * 66 * 4]), counters: counters, verifyDelayMs: 40);
        var results = new Results();
        surface.Publish(Presentation(1, true, target: target), results.Add);
        var result = results.Wait(1);
        Assert.True(result.Presented, result.Reason);
        Assert.Equal(secondary.DeviceName, result.Report!.Monitor);
        Assert.Equal(secondary.DeviceName, ClipOverlayTargeting.MonitorDeviceNameOf(surface.WindowHandle));
        Assert.True(IsAbove(surface.WindowHandle, game.Handle));

        surface.Publish(Presentation(2, true, target: primary), results.Add);
        var back = results.Wait(2);
        Assert.True(back.Presented, back.Reason);
        Assert.Equal(primary.DeviceName, back.Report!.Monitor);
        Assert.Equal(1, counters.Retargets);
        output.WriteLine($"secondary {secondary.DeviceName} scaling {target.Scaling}, primary {primary.DeviceName} scaling {primary.Scaling}");
    }

    // The real pipeline, 100 times over: coordinator, native thread,
    // DirectComposition and on-screen verification, above a topmost game,
    // through Saving and then Saved of the same workflow each time.
    [Fact]
    public void HundredSavingToSavedWorkflowsAllPresent()
    {
        if (!OperatingSystem.IsWindows()) return;
        var target = ClipOverlayTargeting.ResolvePrimary();
        using var game = new BorderlessTopmostWindow(target.Bounds);
        target = target with { Window = game.Handle, Reason = ClipOverlayTargetReason.GameWindow };
        var counters = new ClipOverlayCounters();
        var reports = new ConcurrentBag<ClipOverlayPresentationReport>();
        var surface = new ReportingSurface(new NativeClipOverlaySurface(_ => new ClipOverlayFrame(330, 87, new byte[330 * 87 * 4]), counters: counters, verifyDelayMs: 40), reports);
        var scheduler = new ManualScheduler();
        using var coordinator = new ClipOverlayCoordinator(surface, scheduler, _ => { }, counters: counters, log: (_, _) => { });
        var visibleThroughout = true;
        for (var index = 0; index < 100; index++)
        {
            var workflow = Guid.NewGuid();
            var now = DateTime.UtcNow;
            coordinator.Publish(Event(workflow, 0, ClipOverlayKind.Saving, now, target));
            Assert.True(SpinWait.SpinUntil(() => counters.Presented == index * 2 + 1, 3000), $"Saving {index}: {counters.Summary}");
            coordinator.Publish(Event(workflow, 1, ClipOverlayKind.Saved, now, target));
            Assert.True(SpinWait.SpinUntil(() => counters.Presented == index * 2 + 2, 3000), $"Saved {index}: {counters.Summary}");
            visibleThroughout &= IsWindowVisible(surface.Inner.WindowHandle);
            scheduler.FireDwell();
        }
        Assert.Equal(200, counters.Presented);
        Assert.Equal(0, counters.Failed);
        Assert.Equal(0, counters.Skipped);
        Assert.True(visibleThroughout);
        output.WriteLine(Latency(reports.ToArray()));
    }

    internal static string Latency(IReadOnlyList<ClipOverlayPresentationReport> reports)
    {
        string Line(string name, Func<ClipOverlayPresentationReport, double> value)
        {
            var sorted = reports.Select(value).OrderBy(item => item).ToArray();
            double At(double rank) => sorted.Length == 0 ? 0 : sorted[Math.Min(sorted.Length - 1, (int)Math.Ceiling(sorted.Length * rank) - 1)];
            return $"{name}: p50={At(.5):F2} p95={At(.95):F2} max={(sorted.Length == 0 ? 0 : sorted[^1]):F2}ms";
        }
        return string.Join("\n", Line("coordinator->raster start (queue)", report => report.QueueMs), Line("raster", report => report.RasterMs),
            Line("raster->native thread (post)", report => report.PostMs), Line("native thread->first present", report => report.PresentMs),
            Line("publish->visible (total)", report => report.TotalMs), Line("verification", report => report.VerifyMs)) + $"\nsamples={reports.Count}";
    }

    internal static ClipOverlayEvent Event(Guid workflow, int stage, ClipOverlayKind kind, DateTime now, ClipOverlayTarget target) => new(
        workflow, stage, now, now, kind is ClipOverlayKind.Failure ? 100 : 80, kind, kind == ClipOverlayKind.Saved ? "Clip Saved" : "Clip Saving…", null,
        target, ClipOverlayPlacement.TopRight, true);

    private static TimeSpan Max(TimeSpan a, TimeSpan b) => a > b ? a : b;

    private static ClipOverlayPresentation Presentation(long generation, bool excluded, Guid? workflow = null, int stage = 0, ClipOverlayTarget? target = null)
    {
        var now = DateTime.UtcNow;
        return new ClipOverlayPresentation(generation, new ClipOverlayEvent(
            workflow ?? Guid.NewGuid(), stage, now, now, 30, ClipOverlayKind.Standalone, "Clip Saved", null,
            target ?? new ClipOverlayTarget("DISPLAY1", new PixelRect(0, 0, 1920, 1080), new PixelRect(0, 0, 1920, 1040), 1, ClipOverlayTargetReason.Primary),
            ClipOverlayPlacement.TopRight, excluded));
    }

    internal static IReadOnlyList<ClipOverlayMonitor> Monitors()
    {
        var result = new List<ClipOverlayMonitor>();
        EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (monitor, _, _, _) =>
        {
            var info = new MonitorInfoEx { Size = Marshal.SizeOf<MonitorInfoEx>() };
            if (GetMonitorInfo(monitor, ref info))
                result.Add(new ClipOverlayMonitor(info.DeviceName, new PixelRect(info.Monitor.Left, info.Monitor.Top, info.Monitor.Right - info.Monitor.Left, info.Monitor.Bottom - info.Monitor.Top),
                    new PixelRect(info.Work.Left, info.Work.Top, info.Work.Right - info.Work.Left, info.Work.Bottom - info.Work.Top), 1));
            return true;
        }, IntPtr.Zero);
        return result;
    }

    // Collects completions and waits for a given generation's.
    internal sealed class Results
    {
        private readonly object _gate = new();
        private readonly Dictionary<long, ClipOverlayPresentationResult> _results = new();
        private readonly List<long> _duplicates = new();
        private int _count;
        public int Count { get { lock (_gate) { Assert.Empty(_duplicates); return _count; } } }
        // Runs on the overlay thread: records, never throws there.
        public void Add(ClipOverlayPresentationResult result)
        {
            lock (_gate)
            {
                if (_results.ContainsKey(result.Generation)) _duplicates.Add(result.Generation);
                _results[result.Generation] = result; _count++;
                Monitor.PulseAll(_gate);
            }
        }
        public ClipOverlayPresentationResult Wait(long generation, int milliseconds = 2000)
        {
            var deadline = Stopwatch.StartNew();
            lock (_gate)
            {
                Assert.Empty(_duplicates);
                while (!_results.ContainsKey(generation))
                {
                    var remaining = milliseconds - (int)deadline.ElapsedMilliseconds;
                    Assert.True(remaining > 0, $"No completion for generation {generation}.");
                    Monitor.Wait(_gate, remaining);
                }
                return _results[generation];
            }
        }
    }

    private sealed class ReportingSurface(NativeClipOverlaySurface inner, ConcurrentBag<ClipOverlayPresentationReport> reports) : IClipOverlaySurface
    {
        public NativeClipOverlaySurface Inner => inner;
        public void Publish(ClipOverlayPresentation presentation, Action<ClipOverlayPresentationResult> completion)
            => inner.Publish(presentation, result => { if (result.Presented && result.Report is { } report) reports.Add(report); completion(result); });
        public void Dismiss(long generation) => inner.Dismiss(generation);
        public void Dispose() => inner.Dispose();
    }

    // Timeouts never fire unless asked; dwell dismissals fire on request.
    internal sealed class ManualScheduler : IClipOverlayScheduler
    {
        private readonly object _gate = new();
        private readonly List<(TimeSpan Delay, Scheduled Item)> _items = new();
        public IDisposable Schedule(TimeSpan delay, Action callback)
        {
            var item = new Scheduled(callback);
            lock (_gate) _items.Add((delay, item));
            return item;
        }
        public void FireDwell()
        {
            (TimeSpan Delay, Scheduled Item)[] due;
            lock (_gate) { due = _items.Where(item => item.Delay <= TimeSpan.FromSeconds(3) && !item.Item.Cancelled).ToArray(); _items.RemoveAll(item => item.Delay <= TimeSpan.FromSeconds(3)); }
            foreach (var (_, item) in due) item.Run();
        }
        public void Dispose() { }
        internal sealed class Scheduled(Action callback) : IDisposable
        {
            private int _cancelled;
            public bool Cancelled => Volatile.Read(ref _cancelled) != 0;
            public void Run() { if (!Cancelled) callback(); }
            public void Dispose() => Interlocked.Exchange(ref _cancelled, 1);
        }
    }

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static extern IntPtr GetWindowLongPtr(IntPtr window, int index);

    [DllImport("user32.dll")]
    private static extern bool GetWindowDisplayAffinity(IntPtr window, out uint affinity);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr window);

    [DllImport("user32.dll")]
    private static extern bool IsWindow(IntPtr window);

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr window, out Rect rect);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateWindowEx(int extendedStyle, string className, string windowName, uint style, int x, int y, int width, int height, IntPtr parent, IntPtr menu, IntPtr instance, IntPtr parameter);

    [DllImport("user32.dll")]
    private static extern bool DestroyWindow(IntPtr window);

    [DllImport("user32.dll")]
    private static extern bool SetLayeredWindowAttributes(IntPtr window, uint colorKey, byte alpha, uint flags);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern IntPtr GetTopWindow(IntPtr window);

    [DllImport("user32.dll")]
    private static extern IntPtr GetWindow(IntPtr window, uint command);

    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(IntPtr window, IntPtr insertAfter, int x, int y, int width, int height, uint flags);

    [DllImport("user32.dll")]
    private static extern int GetMessage(out NativeMessage message, IntPtr window, uint minimum, uint maximum);

    [DllImport("user32.dll")]
    private static extern IntPtr DispatchMessage(ref NativeMessage message);

    [DllImport("user32.dll")]
    private static extern bool PostThreadMessage(uint threadId, uint message, IntPtr wParam, IntPtr lParam);

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();

    [DllImport("dwmapi.dll")]
    private static extern int DwmGetWindowAttribute(IntPtr window, int attribute, out uint value, int size);

    private delegate bool MonitorEnumProc(IntPtr monitor, IntPtr dc, IntPtr rect, IntPtr data);

    [DllImport("user32.dll")]
    private static extern bool EnumDisplayMonitors(IntPtr dc, IntPtr clip, MonitorEnumProc callback, IntPtr data);

    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    private static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfoEx info);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
    private struct MonitorInfoEx
    {
        public int Size;
        public Rect Monitor;
        public Rect Work;
        public uint Flags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string DeviceName;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeMessage { public IntPtr Window; public uint Value; public IntPtr WParam, LParam; public uint Time; public int X, Y; public uint Private; }

    private static uint Cloaked(IntPtr window)
    {
        Assert.Equal(0, DwmGetWindowAttribute(window, 14, out var value, sizeof(uint)));
        return value;
    }

    private static bool IsAbove(IntPtr candidate, IntPtr other)
    {
        for (var window = GetTopWindow(IntPtr.Zero); window != IntPtr.Zero; window = GetWindow(window, 2))
        {
            if (window == candidate) return true;
            if (window == other) return false;
        }
        return false;
    }

    // A topmost stand-in for a borderless game, on its own thread with a
    // message loop, as a game's window would be. Layered at zero alpha and
    // click-through: visible and topmost as far as Windows' z-order and
    // monitor lookups go, invisible and inert on the actual display, so the
    // suite never flashes over whoever is using the machine.
    private sealed class BorderlessTopmostWindow : IDisposable
    {
        private static readonly IntPtr HwndTopmost = new(-1);
        // Small: only its z-order and the monitor it is on matter.
        private const int GameSize = 64;
        private readonly Thread _thread;
        private uint _threadId;
        private int _raises;
        public BorderlessTopmostWindow(PixelRect bounds)
        {
            using var ready = new ManualResetEventSlim();
            _thread = new Thread(() =>
            {
                _threadId = GetCurrentThreadId();
                Handle = CreateWindowEx(0x00000008 | 0x08000000 | 0x00000080 | 0x00080000 | 0x00000020, "STATIC", "ClypDat overlay test game", 0x80000000, bounds.X, bounds.Y, GameSize, GameSize, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
                if (Handle != IntPtr.Zero)
                {
                    SetLayeredWindowAttributes(Handle, 0, 0, 0x2);
                    SetWindowPos(Handle, HwndTopmost, bounds.X, bounds.Y, GameSize, GameSize, 0x0010 | 0x0040);
                }
                ready.Set();
                while (GetMessage(out var message, IntPtr.Zero, 0, 0) > 0) DispatchMessage(ref message);
                if (Handle != IntPtr.Zero) DestroyWindow(Handle);
            }) { IsBackground = true, Name = "overlay test game" };
            _thread.Start();
            ready.Wait(2000);
            Assert.NotEqual(IntPtr.Zero, Handle);
        }

        public IntPtr Handle { get; private set; }
        public int Raises => Volatile.Read(ref _raises);

        public bool RaiseAbove(IntPtr other) => Raise() && IsAbove(Handle, other);

        private bool Raise()
        {
            var raised = SetWindowPos(Handle, HwndTopmost, 0, 0, 0, 0, 0x0001 | 0x0002 | 0x0010);
            if (raised) Interlocked.Increment(ref _raises);
            return raised;
        }

        // Raises the game above everything on a period until disposed.
        public IDisposable KeepRaising(TimeSpan period)
        {
            var stop = new CancellationTokenSource();
            var thread = new Thread(() => { while (!stop.IsCancellationRequested) { Raise(); Thread.Sleep(period); } }) { IsBackground = true };
            thread.Start();
            return new Stopper(stop, thread);
        }

        public void Dispose()
        {
            PostThreadMessage(_threadId, 0x0012, IntPtr.Zero, IntPtr.Zero); // WM_QUIT
            _thread.Join(2000);
        }

        private sealed class Stopper(CancellationTokenSource stop, Thread thread) : IDisposable
        {
            private int _disposed;
            public void Dispose()
            {
                if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
                stop.Cancel(); thread.Join(1000); stop.Dispose();
            }
        }
    }

    private sealed class RecordingPresenter : NativeClipOverlaySurface.INativeClipOverlayPresenter
    {
        private readonly object _gate = new();
        private readonly List<PresentedFrame> _frames = new();
        private int _hideCount;

        public string Name => "recording";
        public bool Layered { get; init; }
        public bool IsLayered => Layered;
        // Stands in for the layered fallback: the surface must keep driving
        // every frame of the fade itself.
        public bool AnimatesItself => false;
        public IReadOnlyList<PresentedFrame> Frames { get { lock (_gate) return _frames.ToArray(); } }
        public int HideCount => Volatile.Read(ref _hideCount);

        public void Present(ClipOverlayFrame frame, NativeClipOverlaySurface.PointNative destination, int width, int height, double opacity, bool frameChanged)
        {
            lock (_gate) _frames.Add(new PresentedFrame(destination, width, height, opacity, frameChanged, frame.Pixels[0]));
        }

        public void Animate(ClipOverlayFrame frame, in ClipOverlayMotionPlan plan, bool applyAnimation, bool frameChanged)
            => Present(frame, new NativeClipOverlaySurface.PointNative(plan.WindowX + (int)Math.Round(plan.ToOffsetX), plan.WindowY),
                plan.CardWidth, plan.CardHeight, plan.ToOpacity, frameChanged);

        public void ReassertTopmost() { }
        public void Hide() => Interlocked.Increment(ref _hideCount);
        public string? Verify(in ClipOverlayVerification check) => null;
        public void Dispose() { }
    }

    // The compositor path: one motion is handed over whole, and the surface is
    // expected to stop waking up per frame afterwards.
    private sealed class AnimatingPresenter : NativeClipOverlaySurface.INativeClipOverlayPresenter
    {
        private readonly object _gate = new();
        private readonly List<ClipOverlayMotionPlan> _motions = new();
        private int _uploads, _reasserts, _hideCount, _verifications;

        public string Name => Layered ? "layered-fake" : "animating";
        public bool Layered { get; init; }
        public bool IsLayered => Layered;
        public bool AnimatesItself => true;
        public bool ThrowOnAnimate { get; init; }
        public int FailVerifications { get; init; }
        public bool Healthy { get; set; } = true;
        public IReadOnlyList<ClipOverlayMotionPlan> Motions { get { lock (_gate) return _motions.ToArray(); } }
        public int Uploads => Volatile.Read(ref _uploads);
        public int Reasserts => Volatile.Read(ref _reasserts);
        public int HideCount => Volatile.Read(ref _hideCount);

        public void Present(ClipOverlayFrame frame, NativeClipOverlaySurface.PointNative destination, int width, int height, double opacity, bool frameChanged)
            => Animate(frame, new ClipOverlayMotionPlan(destination.X, destination.Y, width, height, width, height, 0, false, opacity, opacity, 0, 0), true, frameChanged);

        public void Animate(ClipOverlayFrame frame, in ClipOverlayMotionPlan plan, bool applyAnimation, bool frameChanged)
        {
            if (ThrowOnAnimate) throw new InvalidOperationException("DXGI_ERROR_DEVICE_REMOVED");
            if (frameChanged) Interlocked.Increment(ref _uploads);
            if (!applyAnimation) return;
            lock (_gate) _motions.Add(plan);
        }

        public void ReassertTopmost() => Interlocked.Increment(ref _reasserts);
        public void Hide() => Interlocked.Increment(ref _hideCount);
        public string? Verify(in ClipOverlayVerification check) => Interlocked.Increment(ref _verifications) <= FailVerifications ? "window-not-visible" : null;
        public void Dispose() { }
    }

    private readonly record struct PresentedFrame(NativeClipOverlaySurface.PointNative Destination, int Width, int Height, double Opacity, bool FrameChanged, byte FrameMarker);

    private struct Rect { public int Left, Top, Right, Bottom; }
}
