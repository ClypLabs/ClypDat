using System.Runtime.InteropServices;
using Avalonia.Controls;

namespace ClypDat.App.Services;

internal static class OverlayTransparencyDiagnostics
{
    private const int GwlExStyle = -20;
    private const long WsExNoRedirectionBitmap = 0x00200000L;
    private const long WsExLayered = 0x00080000L;

    [DllImport("user32.dll")]
    private static extern IntPtr GetWindowLongPtr(IntPtr hWnd, int nIndex);

    public static void Log(Window window, string name)
    {
        var handle = window.TryGetPlatformHandle()?.Handle ?? IntPtr.Zero;
        if (handle == IntPtr.Zero)
        {
            AppLog.Info($"Overlay[{name}]: actual={window.ActualTransparencyLevel}; no native handle.");
            return;
        }

        var exStyle = (long)GetWindowLongPtr(handle, GwlExStyle);
        var line = $"Overlay[{name}]: actual={window.ActualTransparencyLevel}; exStyle=0x{exStyle:X8}; " +
            $"noRedirectionBitmap={(exStyle & WsExNoRedirectionBitmap) != 0}; layered={(exStyle & WsExLayered) != 0}.";
        // The hover bar reports on every show: 1,300+ identical lines in a day
        // of editing. Only a change in what a window reports is news.
        lock (LastLogged)
        {
            if (LastLogged.TryGetValue(name, out var previous) && previous == line) return;
            LastLogged[name] = line;
        }
        AppLog.Info(line);
    }

    private static readonly Dictionary<string, string> LastLogged = new(StringComparer.Ordinal);
}
