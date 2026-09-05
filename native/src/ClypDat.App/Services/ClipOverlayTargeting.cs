using System.Runtime.InteropServices;
using Avalonia;

namespace ClypDat.App.Services;

internal enum ClipOverlayTargetReason
{
    Primary
}

// Device pixels throughout, and Scaling is the MONITOR's effective DPI rather
// than any window's RenderScaling - the badge is sized for the display it is
// being sent to, not for whichever display the reused overlay window happens
// to be sitting on when it is measured.
internal sealed record ClipOverlayTarget(
    string DeviceName,
    PixelRect Bounds,
    PixelRect WorkArea,
    double Scaling,
    ClipOverlayTargetReason Reason)
{
    public string ReasonLabel => "primary";
}

// Clip notifications always belong to the display Windows designates as the
// primary display. Win32 supplies device-pixel bounds, work area and monitor
// DPI without depending on the monitor containing an app or game window.
internal static class ClipOverlayTargeting
{
    private const uint MonitorDefaultToPrimary = 1;
    private const int MdtEffectiveDpi = 0;

    public static ClipOverlayTarget ResolvePrimary()
    {
        if (!OperatingSystem.IsWindows())
        {
            return new ClipOverlayTarget(string.Empty, new PixelRect(0, 0, 1920, 1080), new PixelRect(0, 0, 1920, 1080), 1.0, ClipOverlayTargetReason.Primary);
        }

        return FromMonitorHandle(MonitorFromPoint(default, MonitorDefaultToPrimary), ClipOverlayTargetReason.Primary);
    }

    private static ClipOverlayTarget FromMonitorHandle(nint monitor, ClipOverlayTargetReason reason)
    {
        var info = new MonitorInfoEx { Size = Marshal.SizeOf<MonitorInfoEx>() };
        if (monitor == IntPtr.Zero || !GetMonitorInfo(monitor, ref info))
        {
            return new ClipOverlayTarget(string.Empty, new PixelRect(0, 0, 1920, 1080), new PixelRect(0, 0, 1920, 1080), 1.0, reason);
        }

        var bounds = ToRect(info.Monitor);
        var work = ToRect(info.Work);
        if (work.Width <= 0 || work.Height <= 0) work = bounds;

        return new ClipOverlayTarget(info.DeviceName ?? string.Empty, bounds, work, ScalingOf(monitor), reason);
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

    [DllImport("user32.dll")]
    private static extern nint MonitorFromPoint(PointStruct point, uint flags);

    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    private static extern bool GetMonitorInfo(nint monitor, ref MonitorInfoEx info);

    [DllImport("shcore.dll")]
    private static extern int GetDpiForMonitor(nint monitor, int dpiType, out uint dpiX, out uint dpiY);
}
