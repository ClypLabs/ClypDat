using System.Runtime.InteropServices;
using ClypDat.App.Controls;
using Xunit;

namespace ClypDat.App.Tests;

public sealed class NativeVideoHostTests
{
    [Fact]
    public void VlcHostClipsChildrenPreservesOtherStylesAndDoesNotChangeParent()
    {
        if (!OperatingSystem.IsWindows()) return;
        // Both windows stay hidden; test the real HWND style required by VLC.
        var parent = CreateWindowExW(0, "STATIC", "ClypDat native video host test", unchecked((int)0x80000000), -32000, -32000, 1, 1, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
        Assert.NotEqual(IntPtr.Zero, parent);
        var child = IntPtr.Zero;
        try
        {
            child = CreateWindowExW(0, "STATIC", "", 0x44000000, 0, 0, 1, 1, parent, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
            Assert.NotEqual(IntPtr.Zero, child);
            var parentStyle = GetWindowLongW(parent, -16);
            var childStyle = GetWindowLongW(child, -16);
            Assert.Equal(0, childStyle & 0x02000000);

            ClickableVideoView.EnsureChildClipping(child);
            Assert.Equal(childStyle | 0x02000000, GetWindowLongW(child, -16));
            Assert.Equal(parentStyle, GetWindowLongW(parent, -16));

            ClickableVideoView.EnsureChildClipping(child);
            Assert.Equal(childStyle | 0x02000000, GetWindowLongW(child, -16));
        }
        finally
        {
            if (child != IntPtr.Zero) DestroyWindow(child);
            DestroyWindow(parent);
        }
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateWindowExW(int extendedStyle, string className, string windowName, int style, int x, int y, int width, int height, IntPtr parent, IntPtr menu, IntPtr instance, IntPtr parameter);
    [DllImport("user32.dll", SetLastError = true)]
    private static extern int GetWindowLongW(IntPtr window, int index);
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyWindow(IntPtr window);
}
