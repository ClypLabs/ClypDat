using System;
using System.Runtime.InteropServices;
using System.Text;

namespace ClypDat.App.Services;

// Windows only hands the foreground to a background process when nothing else
// holds it; otherwise Activate() flashes the taskbar button orange instead.
// A clip landing after the game closed may arrive while the user is already in
// another app, so focus is only taken when the desktop, the taskbar or ClypDat
// itself is in front - a move that cannot flash.
internal static class QuietForegroundPolicy
{
    public static bool Allows(bool hasForeground, bool ownedByThisProcess, string? className) =>
        !hasForeground || ownedByThisProcess || className is
            "Progman" or "WorkerW" or "Shell_TrayWnd" or "Shell_SecondaryTrayWnd";

    public static bool AllowsCurrent()
    {
        var foreground = GetForegroundWindow();
        if (foreground == IntPtr.Zero) return Allows(false, false, null);
        GetWindowThreadProcessId(foreground, out var processId);
        var className = new StringBuilder(64);
        var length = GetClassName(foreground, className, className.Capacity);
        return Allows(true, processId == (uint)Environment.ProcessId, length > 0 ? className.ToString() : null);
    }

    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassName(IntPtr hWnd, StringBuilder className, int maxCount);
}
