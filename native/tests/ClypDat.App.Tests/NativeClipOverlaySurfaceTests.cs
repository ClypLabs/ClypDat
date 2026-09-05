using System.Runtime.InteropServices;
using Avalonia;
using ClypDat.App.Services;
using ClypDat.App.Views;
using Xunit;

namespace ClypDat.App.Tests;

public sealed class NativeClipOverlaySurfaceTests
{
    [Fact]
    public void OneNoActivateHwndAtomicallyReplacesAndDisposes()
    {
        if (!OperatingSystem.IsWindows()) return;
        var foreground = GetForegroundWindow();
        using var game = new BorderlessTopmostWindow();
        using var surface = new NativeClipOverlaySurface(_ => new ClipOverlayFrame(300, 66, new byte[300 * 66 * 4]));
        var handle = surface.WindowHandle;
        Assert.NotEqual(IntPtr.Zero, handle);
        Assert.Equal("DirectComposition", surface.PresenterName);
        var style = GetWindowLongPtr(handle, -20).ToInt64();
        Assert.NotEqual(0, style & 0x00000008);
        Assert.NotEqual(0, style & 0x08000000);
        Assert.NotEqual(0, style & 0x00000020);
        Assert.NotEqual(0, style & 0x00200000);
        Assert.Equal(0, style & 0x00080000);
        Assert.Equal(foreground, GetForegroundWindow());

        surface.Publish(Presentation(1, true), _ => { });
        Assert.True(SpinWait.SpinUntil(() => surface.PublishCount == 1, 1000));
        // The window now carries the slide travel as well as the card, so it
        // starts one travel inward of where the card comes to rest.
        var layout = ClipOverlayLayout.Frame(Presentation(1, true).Event.Target, ClipOverlayPlacement.TopRight, 300, 66);
        Assert.Equal(1596, layout.Window.X);
        Assert.Equal(324, layout.Window.Width);
        Assert.True(SpinWait.SpinUntil(() => GetWindowRect(handle, out var rect) && rect.Left == layout.Window.X && rect.Top == layout.Window.Y, 1000));
        Assert.True(IsWindowVisible(handle));
        Assert.True(IsAbove(handle, game.Handle));
        Assert.True(SpinWait.SpinUntil(() => game.RaiseAbove(handle), 1000));
        Assert.True(SpinWait.SpinUntil(() => IsAbove(handle, game.Handle), 1000));
        Assert.Equal(foreground, GetForegroundWindow());
        Assert.Equal(0u, Cloaked(handle));
        Assert.True(GetWindowDisplayAffinity(handle, out var affinity));
        Assert.Equal(0x11u, affinity);
        surface.Publish(Presentation(2, false), _ => { });
        Assert.True(SpinWait.SpinUntil(() => surface.PublishCount == 2, 1000));
        Assert.Equal(handle, surface.WindowHandle);
        Assert.True(GetWindowDisplayAffinity(handle, out affinity));
        Assert.Equal(0u, affinity);
        Assert.Equal(foreground, GetForegroundWindow());

        surface.Dispose();
        Assert.Equal(IntPtr.Zero, surface.WindowHandle);
        Assert.Equal(foreground, GetForegroundWindow());
    }

    [Fact]
    public void AnimationDestinationsStayInsideTargetWorkArea()
    {
        if (!OperatingSystem.IsWindows()) return;
        var presenter = new RecordingPresenter();
        using var surface = new NativeClipOverlaySurface(
            _ => new ClipOverlayFrame(390, 87, new byte[390 * 87 * 4]),
            _ => presenter);
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
            _ => presenter);
        var target = Presentation(1, true).Event.Target;
        var layout = ClipOverlayLayout.Frame(target, ClipOverlayPlacement.TopRight, 300, 66);

        surface.Publish(Presentation(1, true), _ => { });
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
    }

    [Fact]
    public void SameWorkflowStageUpdateKeepsTheCardVisible()
    {
        if (!OperatingSystem.IsWindows()) return;
        var presenter = new RecordingPresenter();
        using var surface = new NativeClipOverlaySurface(
            presentation => new ClipOverlayFrame(300, 66, Enumerable.Repeat((byte)presentation.Event.Stage, 300 * 66 * 4).ToArray()),
            _ => presenter);
        var workflow = Guid.NewGuid();

        surface.Publish(Presentation(1, true, workflow, 0), _ => { });
        Assert.True(SpinWait.SpinUntil(() => presenter.Frames.Any(frame => frame.Opacity >= .999), 1000));
        var before = presenter.Frames.Count;

        surface.Publish(Presentation(2, true, workflow, 1), _ => { });
        Assert.True(SpinWait.SpinUntil(() => presenter.Frames.Count > before, 1000));
        var updateFrames = presenter.Frames.Skip(before).ToArray();
        Assert.Contains(updateFrames, frame => frame.FrameMarker == 1 && frame.FrameChanged && frame.Opacity >= .999);
        Assert.DoesNotContain(updateFrames, frame => frame.Opacity <= 0.001);
        Assert.Equal(0, presenter.HideCount);
    }

    private static ClipOverlayPresentation Presentation(long generation, bool excluded, Guid? workflow = null, int stage = 0, ClipOverlayTarget? target = null)
    {
        var now = DateTime.UtcNow;
        return new ClipOverlayPresentation(generation, new ClipOverlayEvent(
            workflow ?? Guid.NewGuid(), stage, now, now, 30, ClipOverlayKind.Standalone, "Clip Saved", null,
            target ?? new ClipOverlayTarget("DISPLAY1", new PixelRect(0, 0, 1920, 1080), new PixelRect(0, 0, 1920, 1040), 1, ClipOverlayTargetReason.Primary),
            ClipOverlayPlacement.TopRight, excluded));
    }

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static extern IntPtr GetWindowLongPtr(IntPtr window, int index);

    [DllImport("user32.dll")]
    private static extern bool GetWindowDisplayAffinity(IntPtr window, out uint affinity);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr window);

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr window, out Rect rect);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateWindowEx(int extendedStyle, string className, string windowName, uint style, int x, int y, int width, int height, IntPtr parent, IntPtr menu, IntPtr instance, IntPtr parameter);

    [DllImport("user32.dll")]
    private static extern bool DestroyWindow(IntPtr window);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern IntPtr GetTopWindow(IntPtr window);

    [DllImport("user32.dll")]
    private static extern IntPtr GetWindow(IntPtr window, uint command);

    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(IntPtr window, IntPtr insertAfter, int x, int y, int width, int height, uint flags);

    [DllImport("dwmapi.dll")]
    private static extern int DwmGetWindowAttribute(IntPtr window, int attribute, out uint value, int size);

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

    private sealed class BorderlessTopmostWindow : IDisposable
    {
        private static readonly IntPtr HwndTopmost = new(-1);
        public BorderlessTopmostWindow()
        {
            Handle = CreateWindowEx(0x00000008 | 0x08000000 | 0x00000080, "STATIC", "ClypDat overlay test game", 0x80000000, 0, 0, 1920, 1080, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
            Assert.NotEqual(IntPtr.Zero, Handle);
            Assert.True(SetWindowPos(Handle, HwndTopmost, 0, 0, 1920, 1080, 0x0010 | 0x0040));
        }

        public IntPtr Handle { get; }

        public bool RaiseAbove(IntPtr other) => SetWindowPos(Handle, HwndTopmost, 0, 0, 0, 0, 0x0001 | 0x0002 | 0x0010) && IsAbove(Handle, other);

        public void Dispose() => DestroyWindow(Handle);
    }

    private sealed class RecordingPresenter : NativeClipOverlaySurface.INativeClipOverlayPresenter
    {
        private readonly object _gate = new();
        private readonly List<PresentedFrame> _frames = new();
        private int _hideCount;

        public string Name => "recording";
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
        public void Dispose() { }
    }

    // The compositor path: one motion is handed over whole, and the surface is
    // expected to stop waking up per frame afterwards.
    private sealed class AnimatingPresenter : NativeClipOverlaySurface.INativeClipOverlayPresenter
    {
        private readonly object _gate = new();
        private readonly List<ClipOverlayMotionPlan> _motions = new();
        private int _uploads, _reasserts, _hideCount;

        public string Name => "animating";
        public bool AnimatesItself => true;
        public IReadOnlyList<ClipOverlayMotionPlan> Motions { get { lock (_gate) return _motions.ToArray(); } }
        public int Uploads => Volatile.Read(ref _uploads);
        public int Reasserts => Volatile.Read(ref _reasserts);
        public int HideCount => Volatile.Read(ref _hideCount);

        public void Present(ClipOverlayFrame frame, NativeClipOverlaySurface.PointNative destination, int width, int height, double opacity, bool frameChanged)
            => throw new InvalidOperationException("The compositor path must not be driven frame by frame.");

        public void Animate(ClipOverlayFrame frame, in ClipOverlayMotionPlan plan, bool applyAnimation, bool frameChanged)
        {
            if (frameChanged) Interlocked.Increment(ref _uploads);
            if (!applyAnimation) return;
            lock (_gate) _motions.Add(plan);
        }

        public void ReassertTopmost() => Interlocked.Increment(ref _reasserts);
        public void Hide() => Interlocked.Increment(ref _hideCount);
        public void Dispose() { }
    }

    private readonly record struct PresentedFrame(NativeClipOverlaySurface.PointNative Destination, int Width, int Height, double Opacity, bool FrameChanged, byte FrameMarker);

    private struct Rect { public int Left, Top, Right, Bottom; }
}
