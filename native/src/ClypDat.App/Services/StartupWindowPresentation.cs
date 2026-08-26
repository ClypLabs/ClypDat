using System.Runtime.InteropServices;
using Avalonia.Controls;

namespace ClypDat.App.Services;

// Avalonia creates the Win32 HWND while constructing Window, before Show().
// Cloaking it there prevents DWM from presenting the unpainted white client
// area; unlike opacity it still lets Avalonia render the first dark frame.
internal static class StartupWindowPresentation
{
    private const int DwmwaCloak = 13;
    private const int DwmwaWindowCornerPreference = 33;
    private const int DwmwcpRound = 2;

    public static bool TryCloak(Window window) => SetCloak(window, 1);

    public static void Reveal(Window window) => SetCloak(window, 0);

    /// <summary>
    /// Asks DWM to round the window's corners. This is the composited frame's
    /// own geometry, so a frameless OPAQUE window gets smooth, correctly
    /// anti-aliased corners without going anywhere near per-pixel transparency
    /// - which on this machine is the layered/ANGLE path that keeps losing its
    /// device. Silently does nothing before Windows 11.
    /// </summary>
    public static void TryRoundCorners(Window window) =>
        SetAttribute(window, DwmwaWindowCornerPreference, DwmwcpRound);

    private static bool SetCloak(Window window, int value) => SetAttribute(window, DwmwaCloak, value);

    private static bool SetAttribute(Window window, int attribute, int value)
    {
        if (!OperatingSystem.IsWindows()) return false;
        var handle = window.TryGetPlatformHandle()?.Handle ?? IntPtr.Zero;
        return handle != IntPtr.Zero && DwmSetWindowAttribute(handle, attribute, ref value, sizeof(int)) == 0;
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr window, int attribute, ref int value, int valueSize);
}
