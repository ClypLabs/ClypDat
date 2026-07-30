using System;
using Avalonia.Controls;
using Avalonia.Media;

namespace ClypDat.App.Services;

// Avalonia's window transparency on Windows only works when the OS's WinUI
// Composition API initializes; on some non-NVIDIA GPU/driver combinations
// (reported on AMD) that silently fails and Avalonia falls back to
// WindowTransparencyLevel.None, painting these scrim/dim windows fully
// opaque instead of blending against whatever is behind them.
// Window.ActualTransparencyLevel reports what really got applied, so on the
// rare path where composition failed this swaps the scrim's own brush to
// fully opaque and instead blends the whole window using the older,
// GPU/driver-independent WS_EX_LAYERED + SetLayeredWindowAttributes alpha,
// which Windows has supported unconditionally since Windows 2000.
internal static class WindowTransparencyFallback
{
    private const int GwlExStyle = -20;
    private const long WsExLayered = 0x00080000L;
    private const uint LwaAlpha = 0x2;

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern IntPtr GetWindowLongPtr(IntPtr hWnd, int nIndex);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern IntPtr SetWindowLongPtr(IntPtr hWnd, int nIndex, IntPtr dwNewLong);

    [System.Runtime.InteropServices.DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetLayeredWindowAttributes(IntPtr hWnd, uint crKey, byte bAlpha, uint dwFlags);

    public static void ApplyIfNeeded(Window window, IBrush? background, Action<IBrush> setBackground)
    {
        if (window.ActualTransparencyLevel == WindowTransparencyLevel.Transparent) return;
        if (background is not ISolidColorBrush solid) return;

        var color = solid.Color;
        setBackground(new SolidColorBrush(new Color(255, color.R, color.G, color.B)));

        var handle = window.TryGetPlatformHandle()?.Handle ?? IntPtr.Zero;
        if (handle == IntPtr.Zero) return;

        var exStyle = (long)GetWindowLongPtr(handle, GwlExStyle);
        SetWindowLongPtr(handle, GwlExStyle, (IntPtr)(exStyle | WsExLayered));
        SetLayeredWindowAttributes(handle, 0, color.A, LwaAlpha);
    }
}
