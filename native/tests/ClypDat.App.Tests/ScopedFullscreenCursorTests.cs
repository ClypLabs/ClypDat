using System.Runtime.InteropServices;
using Avalonia;
using ClypDat.App.Services;
using Xunit;

namespace ClypDat.App.Tests;

public sealed class ScopedFullscreenCursorTests
{
    [Theory]
    [InlineData(true, -100, 7, 7, true, true)]
    [InlineData(false, -100, 7, 7, true, false)]
    [InlineData(true, -99, 7, 7, true, false)]
    [InlineData(true, -100, 7, 8, true, false)]
    [InlineData(true, -100, 7, 7, false, false)]
    [InlineData(true, -100, 0, 0, true, false)]
    public void CursorOnlyHidesForStationaryPointerOnForegroundOwnedSurface(bool hidden,
        int x, int owner, int foreground, bool overSurface, bool expected)
    {
        Assert.Equal(expected, ScopedFullscreenCursor.ShouldSuppress(hidden, new PixelPoint(-100, 50),
            new PixelPoint(x, 50), owner, foreground, overSurface));
    }

    [Fact]
    public void RepeatedScopesForwardMessagesAndRestoreNativeChildProcedures()
    {
        using var windows = new HiddenWindows();
        var parentProcedure = GetWindowLongPtrW(windows.Parent, -4);
        var childProcedure = GetWindowLongPtrW(windows.Child, -4);
        using var cursor = new ScopedFullscreenCursor();
        for (var i = 0; i < 8; i++)
        {
            cursor.Update(0, default, hidden: false, windows.Parent);
            Assert.NotEqual(parentProcedure, GetWindowLongPtrW(windows.Parent, -4));
            Assert.NotEqual(childProcedure, GetWindowLongPtrW(windows.Child, -4));
            Assert.Equal((nint)4, SendMessageW(windows.Child, 0x000E, 0, 0)); // WM_GETTEXTLENGTH
            cursor.Restore();
            Assert.Equal(parentProcedure, GetWindowLongPtrW(windows.Parent, -4));
            Assert.Equal(childProcedure, GetWindowLongPtrW(windows.Child, -4));
        }
    }

    [Fact]
    public void NativeDestructionBeforeScopeDisposalIsSafe()
    {
        using var windows = new HiddenWindows();
        using var cursor = new ScopedFullscreenCursor();
        cursor.Update(0, default, hidden: false, windows.Parent);
        Assert.True(DestroyWindow(windows.Parent));
        cursor.Restore();
        Assert.False(IsWindow(windows.Child));
    }

    [Fact]
    public void LaterSubclassKeepsCursorDelegateAliveUntilWindowDestruction()
    {
        using var windows = new HiddenWindows();
        using var cursor = new ScopedFullscreenCursor();
        cursor.Update(0, default, hidden: false, windows.Parent);
        var cursorProcedure = GetWindowLongPtrW(windows.Child, -4);
        WindowProcedure later = (window, message, wParam, lParam) => CallWindowProcW(cursorProcedure, window, message, wParam, lParam);
        var address = Marshal.GetFunctionPointerForDelegate(later);
        SetWindowLongPtrW(windows.Child, -4, address);
        cursor.Restore();
        GC.Collect();
        GC.WaitForPendingFinalizers();
        Assert.Equal(address, GetWindowLongPtrW(windows.Child, -4));
        Assert.Equal((nint)4, SendMessageW(windows.Child, 0x000E, 0, 0));
        Assert.True(DestroyWindow(windows.Parent));
        GC.KeepAlive(later);
    }

    [Fact]
    public async Task ChildrenOnVideoOutputThreadRestoreWithoutCrossThreadSubclassHelpers()
    {
        var ready = new TaskCompletionSource<(nint Parent, nint Child)>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var finished = new ManualResetEventSlim();
        var thread = new Thread(() =>
        {
            try
            {
                using var windows = new HiddenWindows();
                ready.SetResult((windows.Parent, windows.Child));
                while (!finished.IsSet)
                {
                    while (PeekMessageW(out var message, 0, 0, 0, 1))
                    {
                        TranslateMessage(ref message);
                        DispatchMessageW(ref message);
                    }
                    finished.Wait(1);
                }
            }
            catch (Exception error) { ready.TrySetException(error); }
        }) { IsBackground = true };
        thread.Start();
        try
        {
            var windows = await ready.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var original = GetWindowLongPtrW(windows.Child, -4);
            using (var cursor = new ScopedFullscreenCursor())
            {
                cursor.Update(0, default, hidden: false, windows.Parent);
                Assert.NotEqual(original, GetWindowLongPtrW(windows.Child, -4));
            }
            Assert.Equal(original, GetWindowLongPtrW(windows.Child, -4));
        }
        finally
        {
            finished.Set();
            Assert.True(thread.Join(TimeSpan.FromSeconds(5)));
        }
    }

    // Never shown or activated; these tests send no mouse or keyboard input
    // and cannot place a test HWND under the user's cursor.
    private sealed class HiddenWindows : IDisposable
    {
        internal nint Parent { get; } = CreateWindowExW(0, "STATIC", "test", 0x80000000, -32000, -32000, 32, 32, 0, 0, 0, 0);
        internal nint Child { get; }
        internal HiddenWindows()
        {
            Assert.NotEqual((nint)0, Parent);
            Child = CreateWindowExW(0, "STATIC", "test", 0x40000000, 0, 0, 16, 16, Parent, 0, 0, 0);
            Assert.NotEqual((nint)0, Child);
        }
        public void Dispose() { if (IsWindow(Parent)) DestroyWindow(Parent); }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeMessage
    {
        internal nint Window;
        internal uint Message;
        internal nint WParam, LParam;
        internal uint Time;
        internal int X, Y;
        internal uint Private;
    }

    private delegate nint WindowProcedure(nint window, uint message, nint wParam, nint lParam);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern nint CreateWindowExW(uint extended, string className, string name, uint style, int x, int y, int width, int height, nint parent, nint menu, nint instance, nint parameter);
    [DllImport("user32.dll")] private static extern nint GetWindowLongPtrW(nint window, int index);
    [DllImport("user32.dll")] private static extern nint SetWindowLongPtrW(nint window, int index, nint value);
    [DllImport("user32.dll")] private static extern nint CallWindowProcW(nint previous, nint window, uint message, nint wParam, nint lParam);
    [DllImport("user32.dll")] private static extern nint SendMessageW(nint window, uint message, nint wParam, nint lParam);
    [DllImport("user32.dll")] private static extern bool DestroyWindow(nint window);
    [DllImport("user32.dll")] private static extern bool IsWindow(nint window);
    [DllImport("user32.dll")] private static extern bool PeekMessageW(out NativeMessage message, nint window, uint minimum, uint maximum, uint remove);
    [DllImport("user32.dll")] private static extern bool TranslateMessage(ref NativeMessage message);
    [DllImport("user32.dll")] private static extern nint DispatchMessageW(ref NativeMessage message);
}
