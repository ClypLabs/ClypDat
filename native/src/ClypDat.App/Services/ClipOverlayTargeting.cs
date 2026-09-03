using System.Runtime.InteropServices;
using Avalonia;

namespace ClypDat.App.Services;

internal enum ClipOverlayTargetReason
{
    CaptureMonitorSetting,
    GameWindow,
    ForegroundWindow,
    Cursor,
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
    public string ReasonLabel => Reason switch
    {
        ClipOverlayTargetReason.CaptureMonitorSetting => "capture-monitor-setting",
        ClipOverlayTargetReason.GameWindow => "game-window",
        ClipOverlayTargetReason.ForegroundWindow => "foreground-window",
        ClipOverlayTargetReason.Cursor => "cursor",
        _ => "primary"
    };
}

// What the overlay needs to know about what is being recorded. Taken from the
// live ReplayBufferConfig rather than from raw settings: ReplayBufferConfig is
// where "Desktop capture, but auto-switched to Game Capture because a game was
// detected" has already been resolved (MainWindowViewModel.IsEffectiveDesktopCapture),
// so the badge follows what is actually in the clip.
internal readonly record struct ClipOverlayTargetInputs(
    string CaptureSource,
    string CaptureMonitorDeviceName,
    nint GameWindowHandle);

// Which monitor a clip notification belongs on.
//
// Win32 rather than Avalonia's Screens, for three reasons:
//
//   - Screens.ScreenFromPoint returns null for a point on no monitor, and a
//     multi-monitor desktop can have large gaps of virtual desktop between
//     displays. MONITOR_DEFAULTTONEAREST always answers, which removes a
//     whole class of "fell through to the fallback" bugs.
//   - DesktopMonitorService and NativeReplayBuffer.ResolveTargetMonitor already
//     key off Win32 device names and MonitorFromPoint. Resolving the overlay
//     the same way makes "the badge is on the monitor being recorded" true by
//     construction instead of by matching rects across two abstractions.
//   - Screen.Scaling is Avalonia's per-window DPI bookkeeping; GetDpiForMonitor
//     is the monitor's own number, which is what pixel placement needs.
internal static class ClipOverlayTargeting
{
    private const uint MonitorDefaultToNearest = 2;
    private const uint MonitorDefaultToPrimary = 1;
    private const uint MonitorInfoPrimary = 1;
    private const int MdtEffectiveDpi = 0;

    public static ClipOverlayTarget Resolve(ClipOverlayTargetInputs inputs)
    {
        if (!OperatingSystem.IsWindows())
        {
            return new ClipOverlayTarget(string.Empty, new PixelRect(0, 0, 1920, 1080), new PixelRect(0, 0, 1920, 1080), 1.0, ClipOverlayTargetReason.Primary);
        }

        // Desktop capture names the monitor it records. That is the exact
        // answer, and it stays right while the game is alt-tabbed, minimised or
        // mid-mode-switch - all the moments the window-handle paths below go
        // blind, and all the moments a clip is most likely to be saved.
        if (string.Equals(inputs.CaptureSource, "Desktop", StringComparison.OrdinalIgnoreCase))
        {
            var configured = DesktopMonitorService.Resolve(inputs.CaptureMonitorDeviceName);
            var centre = new PointStruct
            {
                X = configured.X + Math.Max(1, configured.Width / 2),
                Y = configured.Y + Math.Max(1, configured.Height / 2)
            };
            var monitor = MonitorFromPoint(centre, MonitorDefaultToNearest);
            if (monitor != IntPtr.Zero) return FromMonitorHandle(monitor, ClipOverlayTargetReason.CaptureMonitorSetting);
        }

        if (IsUsableWindow(inputs.GameWindowHandle))
        {
            var monitor = MonitorFromWindow(inputs.GameWindowHandle, MonitorDefaultToNearest);
            if (monitor != IntPtr.Zero) return FromMonitorHandle(monitor, ClipOverlayTargetReason.GameWindow);
        }

        // Whatever the user is actually looking at. The old fallback here was
        // the main window's monitor, which is worthless in the case that
        // matters: while playing, the main window is minimised to the tray.
        var foreground = GetForegroundWindow();
        if (IsUsableWindow(foreground) && !IsOurWindow(foreground))
        {
            var monitor = MonitorFromWindow(foreground, MonitorDefaultToNearest);
            if (monitor != IntPtr.Zero) return FromMonitorHandle(monitor, ClipOverlayTargetReason.ForegroundWindow);
        }

        if (GetCursorPos(out var cursor))
        {
            var monitor = MonitorFromPoint(cursor, MonitorDefaultToNearest);
            if (monitor != IntPtr.Zero) return FromMonitorHandle(monitor, ClipOverlayTargetReason.Cursor);
        }

        return FromMonitorHandle(MonitorFromPoint(default, MonitorDefaultToPrimary), ClipOverlayTargetReason.Primary);
    }

    public static ClipOverlayTarget FromMonitorHandle(nint monitor, ClipOverlayTargetReason reason)
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

    // Friendly "Display 2" style label for logs, matched by device name against
    // the same enumeration the capture settings show the user.
    public static string LabelFor(string deviceName)
    {
        if (string.IsNullOrWhiteSpace(deviceName)) return "unknown display";
        var option = DesktopMonitorService.GetMonitors().FirstOrDefault(m => string.Equals(m.DeviceName, deviceName, StringComparison.OrdinalIgnoreCase));
        return string.IsNullOrWhiteSpace(option?.Label) ? deviceName : option!.Label;
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

    private static bool IsUsableWindow(nint handle)
    {
        // IsWindowVisible stays true for a MINIMISED window, and a minimised
        // window's rect is parked out at (-32000, -32000) - a point that
        // resolves to a real monitor under DEFAULTTONEAREST and would silently
        // pin the badge to whichever display is closest to that corner.
        if (handle == IntPtr.Zero || !IsWindow(handle)) return false;
        return IsWindowVisible(handle) && !IsIconic(handle);
    }

    private static bool IsOurWindow(nint handle)
    {
        _ = GetWindowThreadProcessId(handle, out var pid);
        return pid == Environment.ProcessId;
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

    [DllImport("user32.dll")]
    private static extern nint MonitorFromWindow(nint window, uint flags);

    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    private static extern bool GetMonitorInfo(nint monitor, ref MonitorInfoEx info);

    [DllImport("user32.dll")]
    private static extern nint GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(nint window, out uint processId);

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out PointStruct point);

    [DllImport("user32.dll")]
    private static extern bool IsWindow(nint window);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(nint window);

    [DllImport("user32.dll")]
    private static extern bool IsIconic(nint window);

    [DllImport("shcore.dll")]
    private static extern int GetDpiForMonitor(nint monitor, int dpiType, out uint dpiX, out uint dpiY);

    // MonitorInfoPrimary is not read here today; kept so the struct's flag word
    // has a name at the one call site that will want it.
    public static bool IsPrimary(uint flags) => (flags & MonitorInfoPrimary) != 0;
}
