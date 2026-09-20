using System.Runtime.InteropServices;

namespace ClypDat.App.Services;

// The refresh rate of the display a capture target sits on. WGC hands frames
// out on composition ticks, so its minimum update interval has to be expressed
// on that display's grid rather than on an arbitrary millisecond value - see
// WgcMinimumUpdateIntervalPolicy.
internal static class DisplayRefreshService
{
    private const int EnumCurrentSettings = -1;
    private const uint MonitorDefaultToNearest = 2;

    public static double GetRefreshHzForWindow(nint windowHandle)
    {
        if (!OperatingSystem.IsWindows()) return 0;
        var monitor = windowHandle != 0
            ? MonitorFromWindow(windowHandle, MonitorDefaultToNearest)
            : nint.Zero;
        return GetRefreshHz(monitor);
    }

    // 0 means "not known" - callers fall back to refresh-agnostic behaviour
    // rather than acting on a guessed grid.
    public static double GetRefreshHz(nint monitorHandle)
    {
        if (!OperatingSystem.IsWindows()) return 0;
        try
        {
            string? deviceName = null;
            if (monitorHandle != nint.Zero)
            {
                var info = new MonitorInfoEx { Size = Marshal.SizeOf<MonitorInfoEx>() };
                if (GetMonitorInfo(monitorHandle, ref info)) deviceName = info.DeviceName;
            }

            var mode = new DevMode { Size = (short)Marshal.SizeOf<DevMode>() };
            // A null device name asks for the settings of the display the
            // calling thread's desktop is on, which is the right answer for a
            // handle Windows would not resolve.
            if (!EnumDisplaySettings(deviceName, EnumCurrentSettings, ref mode)) return 0;
            // Windows reports 0 or 1 for "hardware default" on some adapters.
            return mode.DisplayFrequency > 1 ? mode.DisplayFrequency : 0;
        }
        catch (Exception error)
        {
            AppLog.Info($"Display refresh rate unavailable ({error.Message}).");
            return 0;
        }
    }

    [DllImport("user32.dll")]
    private static extern nint MonitorFromWindow(nint window, uint flags);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "GetMonitorInfoW")]
    private static extern bool GetMonitorInfo(nint monitor, ref MonitorInfoEx info);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "EnumDisplaySettingsW")]
    private static extern bool EnumDisplaySettings(string? deviceName, int modeNumber, ref DevMode mode);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
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

    // DEVMODEW. Only the display fields matter here, but the whole structure
    // has to be laid out exactly or EnumDisplaySettings writes past what it was
    // given: dmSize is what tells Windows how much of it is ours.
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DevMode
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string DeviceName;
        public short SpecVersion;
        public short DriverVersion;
        public short Size;
        public short DriverExtra;
        public int Fields;
        public int PositionX;
        public int PositionY;
        public int DisplayOrientation;
        public int DisplayFixedOutput;
        public short Color;
        public short Duplex;
        public short YResolution;
        public short TtOption;
        public short Collate;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string FormName;
        public short LogPixels;
        public int BitsPerPel;
        public int PelsWidth;
        public int PelsHeight;
        public int DisplayFlags;
        public int DisplayFrequency;
        public int IcmMethod;
        public int IcmIntent;
        public int MediaType;
        public int DitherType;
        public int Reserved1;
        public int Reserved2;
        public int PanningWidth;
        public int PanningHeight;
    }
}
