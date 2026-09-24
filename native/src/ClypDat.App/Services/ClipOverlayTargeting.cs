using System.Runtime.InteropServices;
using Avalonia;

namespace ClypDat.App.Services;

// Why a notification was sent to the monitor it was sent to.
internal enum ClipOverlayTargetReason
{
    // The detected game's window.
    GameWindow,
    // The window replay is capturing.
    CaptureWindow,
    // The monitor replay is capturing in desktop mode.
    CaptureMonitor,
    // The desktop-capture monitor from settings.
    DesktopMonitor,
    // Nothing better was known: the Windows primary display.
    Primary
}

// Device pixels throughout, and Scaling is the MONITOR's effective DPI rather
// than any window's RenderScaling - the badge is sized for the display it is
// being sent to, not for whichever display the reused overlay window happens
// to be sitting on when it is measured. Window is the game or capture window
// the badge has to stay above, when one is known.
internal sealed record ClipOverlayTarget(
    string DeviceName,
    PixelRect Bounds,
    PixelRect WorkArea,
    double Scaling,
    ClipOverlayTargetReason Reason,
    nint Window = 0)
{
    public string ReasonLabel => Reason switch
    {
        ClipOverlayTargetReason.GameWindow => "game-window",
        ClipOverlayTargetReason.CaptureWindow => "capture-window",
        ClipOverlayTargetReason.CaptureMonitor => "capture-monitor",
        ClipOverlayTargetReason.DesktopMonitor => "desktop-monitor",
        _ => "primary"
    };

    // Primary is the fallback when nothing better was known; every other
    // reason came from the game or capture itself.
    public bool IsAuthoritative => Reason != ClipOverlayTargetReason.Primary;
}

// What is known about where the user is playing when a notification is raised.
internal readonly record struct ClipOverlayTargetHints(
    nint GameWindow = 0,
    nint CaptureWindow = 0,
    string? CaptureMonitorDeviceName = null,
    string? DesktopMonitorDeviceName = null);

internal readonly record struct ClipOverlayMonitor(string DeviceName, PixelRect Bounds, PixelRect WorkArea, double Scaling);

// Monitor lookups the resolution needs, so the policy is testable without the
// machine's real displays.
internal interface IClipOverlayMonitors
{
    // The monitor showing a live, unminimized window; null otherwise.
    ClipOverlayMonitor? FromWindow(nint window);
    ClipOverlayMonitor? FromDeviceName(string deviceName);
    ClipOverlayMonitor Primary();
}

// Notifications go where the user is playing: the monitor of the detected
// game, else of the window or monitor replay captures, else the configured
// desktop-capture monitor, and only then the Windows primary display. Resolved
// per notification, so a game moved between monitors is followed.
internal static class ClipOverlayTargeting
{
    public static ClipOverlayTarget Resolve(ClipOverlayTargetHints hints) => Resolve(hints, Win32Monitors.Instance);

    public static ClipOverlayTarget Resolve(ClipOverlayTargetHints hints, IClipOverlayMonitors monitors)
    {
        if (hints.GameWindow != 0 && monitors.FromWindow(hints.GameWindow) is { } game)
            return Target(game, ClipOverlayTargetReason.GameWindow, hints.GameWindow);
        if (hints.CaptureWindow != 0 && monitors.FromWindow(hints.CaptureWindow) is { } captured)
            return Target(captured, ClipOverlayTargetReason.CaptureWindow, hints.CaptureWindow);
        if (!string.IsNullOrWhiteSpace(hints.CaptureMonitorDeviceName) && monitors.FromDeviceName(hints.CaptureMonitorDeviceName) is { } capture)
            return Target(capture, ClipOverlayTargetReason.CaptureMonitor, 0);
        if (!string.IsNullOrWhiteSpace(hints.DesktopMonitorDeviceName) && monitors.FromDeviceName(hints.DesktopMonitorDeviceName) is { } desktop)
            return Target(desktop, ClipOverlayTargetReason.DesktopMonitor, 0);
        return Target(monitors.Primary(), ClipOverlayTargetReason.Primary, 0);
    }

    public static ClipOverlayTarget ResolvePrimary() => Resolve(default);

    private static ClipOverlayTarget Target(ClipOverlayMonitor monitor, ClipOverlayTargetReason reason, nint window)
        => new(monitor.DeviceName, monitor.Bounds, monitor.WorkArea, monitor.Scaling, reason, window);

    // The monitor a window is on, by device name, for checking where the
    // overlay actually landed.
    public static string MonitorDeviceNameOf(nint window)
        => OperatingSystem.IsWindows() && window != 0 && Win32Monitors.Describe(MonitorFromWindow(window, MonitorDefaultToNearest)) is { } monitor
            ? monitor.DeviceName : string.Empty;

    private const uint MonitorDefaultToNull = 0, MonitorDefaultToPrimary = 1, MonitorDefaultToNearest = 2;
    private const int MdtEffectiveDpi = 0;

    private sealed class Win32Monitors : IClipOverlayMonitors
    {
        public static Win32Monitors Instance { get; } = new();

        public ClipOverlayMonitor? FromWindow(nint window)
        {
            if (!OperatingSystem.IsWindows() || window == 0 || !IsWindow(window) || !IsWindowVisible(window) || IsIconic(window)) return null;
            return Describe(MonitorFromWindow(window, MonitorDefaultToNull));
        }

        public ClipOverlayMonitor? FromDeviceName(string deviceName)
        {
            if (!OperatingSystem.IsWindows()) return null;
            ClipOverlayMonitor? found = null;
            EnumDisplayMonitors(0, 0, (monitor, _, _, _) =>
            {
                if (Describe(monitor) is { } candidate && string.Equals(candidate.DeviceName, deviceName, StringComparison.OrdinalIgnoreCase))
                {
                    found = candidate;
                    return false;
                }
                return true;
            }, 0);
            return found;
        }

        public ClipOverlayMonitor Primary()
            => OperatingSystem.IsWindows() && Describe(MonitorFromPoint(default, MonitorDefaultToPrimary)) is { } primary
                ? primary
                : new ClipOverlayMonitor(string.Empty, new PixelRect(0, 0, 1920, 1080), new PixelRect(0, 0, 1920, 1080), 1.0);

        public static ClipOverlayMonitor? Describe(nint monitor)
        {
            var info = new MonitorInfoEx { Size = Marshal.SizeOf<MonitorInfoEx>() };
            if (monitor == 0 || !GetMonitorInfo(monitor, ref info)) return null;
            var bounds = ToRect(info.Monitor);
            var work = ToRect(info.Work);
            if (work.Width <= 0 || work.Height <= 0) work = bounds;
            return new ClipOverlayMonitor(info.DeviceName ?? string.Empty, bounds, work, ScalingOf(monitor));
        }
    }

    private static double ScalingOf(nint monitor)
    {
        try
        {
            if (GetDpiForMonitor(monitor, MdtEffectiveDpi, out var dpiX, out _) == 0 && dpiX > 0) return dpiX / 96.0;
        }
        catch (DllNotFoundException)
        {
            // shcore is present on every Windows version this app supports;
            // treating its absence as 100% is still better than throwing out of
            // a notification.
        }
        catch (EntryPointNotFoundException)
        {
        }

        return 1.0;
    }

    private static PixelRect ToRect(RectStruct rect) => new(
        rect.Left,
        rect.Top,
        Math.Max(0, rect.Right - rect.Left),
        Math.Max(0, rect.Bottom - rect.Top));

    [StructLayout(LayoutKind.Sequential)]
    private struct PointStruct
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RectStruct
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
    private struct MonitorInfoEx
    {
        public int Size;
        public RectStruct Monitor;
        public RectStruct Work;
        public uint Flags;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string DeviceName;
    }

    private delegate bool MonitorEnumProc(nint monitor, nint dc, nint rect, nint data);

    [DllImport("user32.dll")]
    private static extern nint MonitorFromPoint(PointStruct point, uint flags);

    [DllImport("user32.dll")]
    private static extern nint MonitorFromWindow(nint window, uint flags);

    [DllImport("user32.dll")]
    private static extern bool EnumDisplayMonitors(nint dc, nint clip, MonitorEnumProc callback, nint data);

    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    private static extern bool GetMonitorInfo(nint monitor, ref MonitorInfoEx info);

    [DllImport("user32.dll")]
    private static extern bool IsWindow(nint window);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(nint window);

    [DllImport("user32.dll")]
    private static extern bool IsIconic(nint window);

    [DllImport("shcore.dll")]
    private static extern int GetDpiForMonitor(nint monitor, int dpiType, out uint dpiX, out uint dpiY);
}
