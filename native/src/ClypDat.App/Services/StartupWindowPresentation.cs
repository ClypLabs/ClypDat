using System.Runtime.InteropServices;
using Avalonia.Controls;

namespace ClypDat.App.Services;

// Avalonia creates the Win32 HWND while constructing Window, before Show().
// Cloaking it there prevents DWM from presenting the unpainted white client
// area; unlike opacity it still lets Avalonia render the first dark frame.
internal static class StartupWindowPresentation
{
    private const int DwmwaCloak = 13;

    public static bool TryCloak(Window window) => SetCloak(window, 1);

    public static void Reveal(Window window) => SetCloak(window, 0);

    private static bool SetCloak(Window window, int value)
    {
        if (!OperatingSystem.IsWindows()) return false;
        var handle = window.TryGetPlatformHandle()?.Handle ?? IntPtr.Zero;
        return handle != IntPtr.Zero && DwmSetWindowAttribute(handle, DwmwaCloak, ref value, sizeof(int)) == 0;
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr window, int attribute, ref int value, int valueSize);
}
