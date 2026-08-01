using System.Runtime.InteropServices;

namespace ClypDat.App.Services;

public sealed record DesktopMonitorOption(string DeviceName, string Label, int X, int Y, int Width, int Height, bool IsPrimary)
{
    public static DesktopMonitorOption PrimaryFallback { get; } = new(string.Empty, "Primary display", 0, 0, 1920, 1080, true);
}

// Win32 device names are stable across app launches and are accepted by both
// ScreenRecorderLib and DXGI output matching. Avalonia screen IDs are not.
public static class DesktopMonitorService
{
    private const uint MonitorInfoPrimary = 1;

    public static IReadOnlyList<DesktopMonitorOption> GetMonitors()
    {
        var monitors = new List<DesktopMonitorOption>();
        EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (monitor, _, _, _) =>
        {
            var info = new MonitorInfoEx { Size = Marshal.SizeOf<MonitorInfoEx>() };
            if (!GetMonitorInfo(monitor, ref info)) return true;
            var width = Math.Max(1, info.Monitor.Right - info.Monitor.Left);
            var height = Math.Max(1, info.Monitor.Bottom - info.Monitor.Top);
            var primary = (info.Flags & MonitorInfoPrimary) != 0;
            var displayNumber = info.DeviceName.Replace("\\\\.\\DISPLAY", "Display ", StringComparison.OrdinalIgnoreCase);
            var label = $"{displayNumber} — {width}×{height}" + (primary ? " (Primary)" : string.Empty);
            monitors.Add(new DesktopMonitorOption(info.DeviceName, label, info.Monitor.Left, info.Monitor.Top, width, height, primary));
            return true;
        }, IntPtr.Zero);

        return monitors
            .OrderBy(monitor => monitor.IsPrimary ? 0 : 1)
            .ThenBy(monitor => monitor.X)
            .ThenBy(monitor => monitor.Y)
            .ToArray();
    }

    public static DesktopMonitorOption Resolve(string? deviceName, IReadOnlyList<DesktopMonitorOption>? monitors = null)
    {
        monitors ??= GetMonitors();
        return monitors.FirstOrDefault(m => !string.IsNullOrWhiteSpace(deviceName) && string.Equals(m.DeviceName, deviceName, StringComparison.OrdinalIgnoreCase))
               ?? monitors.FirstOrDefault(m => m.IsPrimary)
               ?? monitors.FirstOrDefault()
               ?? DesktopMonitorOption.PrimaryFallback;
    }

    [DllImport("user32.dll")]
    private static extern bool EnumDisplayMonitors(IntPtr hdc, IntPtr clip, MonitorEnumProc callback, IntPtr data);

    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    private static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfoEx info);

    private delegate bool MonitorEnumProc(IntPtr monitor, IntPtr hdc, IntPtr rect, IntPtr data);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
    private struct MonitorInfoEx
    {
        public int Size;
        public Rect Monitor;
        public Rect Work;
        public uint Flags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string DeviceName;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }
}
