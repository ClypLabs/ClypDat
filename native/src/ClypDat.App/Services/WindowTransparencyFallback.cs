using System;
using System.Runtime.CompilerServices;
using Avalonia.Controls;
using Avalonia.Media;

namespace ClypDat.App.Services;

// Avalonia's Windows Server transparency path reports Transparent even when
// DWM paints the window black. Native WS_EX_LAYERED alpha works on the same
// system, so Server overlays use it instead. This is whole-window alpha:
// cards and controls fade slightly with their scrim, but retain their layout
// and remain usable above LibVLC's native child window.
internal static class WindowTransparencyFallback
{
    private const int GwlExStyle = -20;
    private const long WsExLayered = 0x00080000L;
    private const uint LwaAlpha = 0x2;

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern IntPtr GetWindowLongPtr(IntPtr hWnd, int nIndex);

    [System.Runtime.InteropServices.DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetWindowLongPtr(IntPtr hWnd, int nIndex, IntPtr dwNewLong);

    [System.Runtime.InteropServices.DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetLayeredWindowAttributes(IntPtr hWnd, uint crKey, byte bAlpha, uint dwFlags);

    private sealed class WindowAlpha
    {
        public required byte Value { get; init; }
    }

    private static readonly ConditionalWeakTable<Window, WindowAlpha> OriginalAlphas = new();

    public static void ApplyIfNeeded(Window window, IBrush? background, Action<IBrush> setBackground)
        => Apply(window, background, setBackground);

    // Per-pixel mirror windows need their Avalonia owner only for input. Keep
    // that owner effectively invisible without changing its visual tree; its
    // normal render is copied into the native companion before this alpha is
    // applied to the HWND.
    public static void ApplyInputSurfaceIfNeeded(Window window)
    {
        if (!WindowsPlatformProfile.IsServer()) return;

        var handle = window.TryGetPlatformHandle()?.Handle ?? IntPtr.Zero;
        if (handle == IntPtr.Zero) return;

        var exStyle = (long)GetWindowLongPtr(handle, GwlExStyle);
        SetWindowLongPtr(handle, GwlExStyle, (IntPtr)(exStyle | WsExLayered));
        if (!SetLayeredWindowAttributes(handle, 0, 1, LwaAlpha))
            AppLog.Error($"Layered hover input surface failed: error={System.Runtime.InteropServices.Marshal.GetLastWin32Error()}.");
    }

    private static void Apply(Window window, IBrush? background, Action<IBrush> setBackground)
    {
        if (background is not ISolidColorBrush solid) return;

        var mustUseLayeredAlpha = WindowsPlatformProfile.IsServer() ||
            window.ActualTransparencyLevel != WindowTransparencyLevel.Transparent;
        if (!mustUseLayeredAlpha) return;

        var alpha = OriginalAlphas.GetValue(window, _ => new WindowAlpha { Value = solid.Color.A }).Value;
        var color = solid.Color;
        setBackground(new SolidColorBrush(new Color(255, color.R, color.G, color.B)));

        var handle = window.TryGetPlatformHandle()?.Handle ?? IntPtr.Zero;
        if (handle == IntPtr.Zero) return;

        var exStyle = (long)GetWindowLongPtr(handle, GwlExStyle);
        System.Runtime.InteropServices.Marshal.SetLastPInvokeError(0);
        SetWindowLongPtr(handle, GwlExStyle, (IntPtr)(exStyle | WsExLayered));
        var styleError = System.Runtime.InteropServices.Marshal.GetLastPInvokeError();
        if (styleError != 0)
        {
            AppLog.Error($"Layered overlay style failed: error={styleError}.");
            return;
        }

        if (!SetLayeredWindowAttributes(handle, 0, alpha, LwaAlpha))
        {
            AppLog.Error($"Layered overlay alpha failed: error={System.Runtime.InteropServices.Marshal.GetLastWin32Error()}.");
            return;
        }

        AppLog.Info($"Layered overlay alpha applied: alpha={alpha}.");
    }
}
