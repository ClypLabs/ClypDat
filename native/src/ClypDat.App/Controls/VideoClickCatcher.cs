using Avalonia;
using Avalonia.Controls;
using System.ComponentModel;
using System.Runtime.InteropServices;

namespace ClypDat.App.Controls;

// LibVLC renders into a native child HWND, above Avalonia's visual tree. This
// is a native owned popup, not an Avalonia transparent Window: transparent
// Avalonia windows can become click-through on some Windows GPU paths. A
// one-alpha layered HWND remains imperceptible while still accepting clicks.
internal sealed class VideoClickCatcher : IDisposable
{
    private const uint WsPopup = 0x80000000;
    private const uint WsExLayered = 0x00080000;
    private const uint WsExToolWindow = 0x00000080;
    private const uint WsExNoActivate = 0x08000000;
    private const int GwlWndProc = -4;
    private const uint LwaAlpha = 0x00000002;
    private const uint SwpNoActivate = 0x0010;
    private const uint SwpShowWindow = 0x0040;
    private const int SwHide = 0;
    private const uint WmLButtonDown = 0x0201;
    private const uint WmMouseActivate = 0x0021;
    private const uint WmEraseBackground = 0x0014;
    private const uint WmNcDestroy = 0x0082;
    private const int MaNoActivate = 3;

    private readonly Window _owner;
    private readonly WindowProc _windowProc;
    private IntPtr _handle;
    private IntPtr _previousWindowProc;

    public VideoClickCatcher(Window owner)
    {
        _owner = owner;
        _windowProc = WindowProcedure;
    }

    public event EventHandler? Clicked;
    public bool IsVisible { get; private set; }

    public void Show(PixelPoint position, PixelSize size)
    {
        var ownerHandle = _owner.TryGetPlatformHandle()?.Handle ?? IntPtr.Zero;
        if (ownerHandle == IntPtr.Zero) throw new InvalidOperationException("Main window native handle is unavailable.");
        EnsureCreated(ownerHandle);

        var width = Math.Max(1, size.Width);
        var height = Math.Max(1, size.Height);
        if (!SetWindowPos(_handle, IntPtr.Zero, position.X, position.Y, width, height, SwpNoActivate | SwpShowWindow))
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not position editor video click surface.");
        IsVisible = true;
    }

    public void Hide()
    {
        if (_handle != IntPtr.Zero) ShowWindow(_handle, SwHide);
        IsVisible = false;
    }

    public void Dispose()
    {
        IsVisible = false;
        if (_handle == IntPtr.Zero) return;

        if (IsWindow(_handle) && _previousWindowProc != IntPtr.Zero)
            SetWindowLongPtr(_handle, GwlWndProc, _previousWindowProc);
        if (IsWindow(_handle)) DestroyWindow(_handle);
        _handle = IntPtr.Zero;
        _previousWindowProc = IntPtr.Zero;
    }

    private void EnsureCreated(IntPtr ownerHandle)
    {
        if (_handle != IntPtr.Zero && IsWindow(_handle)) return;

        _handle = CreateWindowExW(
            WsExLayered | WsExToolWindow | WsExNoActivate,
            "STATIC",
            "",
            WsPopup,
            0, 0, 1, 1,
            ownerHandle,
            IntPtr.Zero,
            IntPtr.Zero,
            IntPtr.Zero);
        if (_handle == IntPtr.Zero)
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not create editor video click surface.");

        // Alpha must stay nonzero. Fully transparent layered windows can be
        // excluded from hit-testing; 1/255 is visually imperceptible.
        if (!SetLayeredWindowAttributes(_handle, 0, 1, LwaAlpha))
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not make editor video click surface transparent.");

        _previousWindowProc = SetWindowLongPtr(_handle, GwlWndProc, Marshal.GetFunctionPointerForDelegate(_windowProc));
        if (_previousWindowProc == IntPtr.Zero && Marshal.GetLastWin32Error() != 0)
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not attach editor video click handler.");
    }

    private IntPtr WindowProcedure(IntPtr handle, uint message, IntPtr wParam, IntPtr lParam)
    {
        switch (message)
        {
            case WmLButtonDown:
                Clicked?.Invoke(this, EventArgs.Empty);
                return IntPtr.Zero;
            case WmMouseActivate:
                return new IntPtr(MaNoActivate);
            case WmEraseBackground:
                return new IntPtr(1);
            case WmNcDestroy:
                _handle = IntPtr.Zero;
                _previousWindowProc = IntPtr.Zero;
                IsVisible = false;
                break;
        }

        return _previousWindowProc == IntPtr.Zero
            ? DefWindowProcW(handle, message, wParam, lParam)
            : CallWindowProcW(_previousWindowProc, handle, message, wParam, lParam);
    }

    private delegate IntPtr WindowProc(IntPtr handle, uint message, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateWindowExW(uint exStyle, string className, string windowName, uint style,
        int x, int y, int width, int height, IntPtr parent, IntPtr menu, IntPtr instance, IntPtr parameter);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool DestroyWindow(IntPtr handle);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetLayeredWindowAttributes(IntPtr handle, uint colorKey, byte alpha, uint flags);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetWindowPos(IntPtr handle, IntPtr insertAfter, int x, int y, int width, int height, uint flags);

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr handle, int command);

    [DllImport("user32.dll")]
    private static extern bool IsWindow(IntPtr handle);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW", SetLastError = true)]
    private static extern IntPtr SetWindowLongPtr(IntPtr handle, int index, IntPtr value);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr CallWindowProcW(IntPtr previousWindowProc, IntPtr handle, uint message, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr DefWindowProcW(IntPtr handle, uint message, IntPtr wParam, IntPtr lParam);
}
