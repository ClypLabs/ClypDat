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
        using var surface = new NativeClipOverlaySurface(_ => new ClipOverlayFrame(300, 66, new byte[300 * 66 * 4]));
        var handle = surface.WindowHandle;
        Assert.NotEqual(IntPtr.Zero, handle);
        var style = GetWindowLongPtr(handle, -20).ToInt64();
        Assert.NotEqual(0, style & 0x08000000);
        Assert.NotEqual(0, style & 0x00000020);

        surface.Publish(Presentation(1, true));
        Assert.True(SpinWait.SpinUntil(() => surface.PublishCount == 1, 1000));
        Assert.True(SpinWait.SpinUntil(() => GetWindowRect(handle, out var rect) && rect.Left == 1620 && rect.Top == 32, 1000));
        Assert.True(IsWindowVisible(handle));
        Assert.Equal(0u, Cloaked(handle));
        Assert.True(GetWindowDisplayAffinity(handle, out var affinity));
        Assert.Equal(0x11u, affinity);
        surface.Publish(Presentation(2, false));
        Assert.True(SpinWait.SpinUntil(() => surface.PublishCount == 2, 1000));
        Assert.Equal(handle, surface.WindowHandle);
        Assert.True(GetWindowDisplayAffinity(handle, out affinity));
        Assert.Equal(0u, affinity);

        surface.Dispose();
        Assert.Equal(IntPtr.Zero, surface.WindowHandle);
    }

    private static ClipOverlayPresentation Presentation(long generation, bool excluded)
    {
        var now = DateTime.UtcNow;
        return new ClipOverlayPresentation(generation, new ClipOverlayEvent(
            Guid.NewGuid(), 0, now, now, 30, ClipOverlayKind.Standalone, "Clip Saved", null,
            new ClipOverlayTarget("DISPLAY1", new PixelRect(0, 0, 1920, 1080), new PixelRect(0, 0, 1920, 1040), 1, ClipOverlayTargetReason.Primary),
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

    [DllImport("dwmapi.dll")]
    private static extern int DwmGetWindowAttribute(IntPtr window, int attribute, out uint value, int size);

    private static uint Cloaked(IntPtr window)
    {
        Assert.Equal(0, DwmGetWindowAttribute(window, 14, out var value, sizeof(uint)));
        return value;
    }

    private struct Rect { public int Left, Top, Right, Bottom; }
}
