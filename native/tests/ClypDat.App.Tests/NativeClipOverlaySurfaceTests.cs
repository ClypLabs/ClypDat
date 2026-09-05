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

        surface.Publish(Presentation(1, true));
        Assert.True(SpinWait.SpinUntil(() => surface.PublishCount == 1, 1000));
        Assert.True(SpinWait.SpinUntil(() => GetWindowRect(handle, out var rect) && rect.Left == 1620 && rect.Top == 32, 1000));
        Assert.True(IsWindowVisible(handle));
        Assert.True(IsAbove(handle, game.Handle));
        Assert.True(SpinWait.SpinUntil(() => game.RaiseAbove(handle), 1000));
        Assert.True(SpinWait.SpinUntil(() => IsAbove(handle, game.Handle), 1000));
        Assert.Equal(foreground, GetForegroundWindow());
        Assert.Equal(0u, Cloaked(handle));
        Assert.True(GetWindowDisplayAffinity(handle, out var affinity));
        Assert.Equal(0x11u, affinity);
        surface.Publish(Presentation(2, false));
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

        surface.Publish(Presentation(1, true, target));
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

    private static ClipOverlayPresentation Presentation(long generation, bool excluded, ClipOverlayTarget? target = null)
    {
        var now = DateTime.UtcNow;
        return new ClipOverlayPresentation(generation, new ClipOverlayEvent(
            Guid.NewGuid(), 0, now, now, 30, ClipOverlayKind.Standalone, "Clip Saved", null,
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
        public IReadOnlyList<PresentedFrame> Frames { get { lock (_gate) return _frames.ToArray(); } }
        public int HideCount => Volatile.Read(ref _hideCount);

        public void Present(ClipOverlayFrame frame, NativeClipOverlaySurface.PointNative destination, int width, int height, double opacity, bool frameChanged)
        {
            lock (_gate) _frames.Add(new PresentedFrame(destination, width, height, opacity));
        }

        public void Hide() => Interlocked.Increment(ref _hideCount);
        public void Dispose() { }
    }

    private readonly record struct PresentedFrame(NativeClipOverlaySurface.PointNative Destination, int Width, int Height, double Opacity);

    private struct Rect { public int Left, Top, Right, Bottom; }
}
