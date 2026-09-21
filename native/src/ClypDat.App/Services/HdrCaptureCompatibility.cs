using ClypDat.Capture.Abstractions;
using System.Runtime.InteropServices;
using Vortice.Direct3D11;
using Vortice.DXGI;

namespace ClypDat.App.Services;

// Output6 is Windows' authoritative active-output colour-space report. Keep
// this small and independent from capture so a display move/HDR toggle can be
// sampled with the recorder's existing one-second target check.
internal static class HdrCaptureCompatibility
{
    private static nint _lastWhiteLevelFallbackMonitor;
    internal readonly record struct DisplayProfile(bool IsHdr, float SdrWhiteLevelNits, float PeakLuminanceNits)
    {
        public static DisplayProfile Unknown => new(false, 80, 1000);
    }

    public static DisplayProfile GetDisplayProfile(ID3D11Device device, nint monitor)
    {
        try
        {
            using var dxgiDevice = device.QueryInterface<IDXGIDevice>();
            using var adapter = dxgiDevice.GetParent<IDXGIAdapter>();
            for (uint index = 0; ; index++)
            {
                var result = adapter.EnumOutputs(index, out var output);
                if (result.Failure) break;
                using (output)
                {
                    if (output.Description.Monitor != monitor) continue;
                    using var output6 = output.QueryInterface<IDXGIOutput6>();
                    var description = output6.Description1;
                    var whiteAvailable = TryGetSdrWhiteLevel(monitor, out var detectedWhite);
                    var white = whiteAvailable ? detectedWhite : 80f;
                    var peak = description.MaxLuminance <= 0 ? 1000f : description.MaxLuminance;
                    if (!whiteAvailable && Interlocked.Exchange(ref _lastWhiteLevelFallbackMonitor, monitor) != monitor)
                        AppLog.Info("Native capture: HDR SDR white level unavailable; assuming 80 nits.");
                    if (description.MaxLuminance <= 0) AppLog.Info("Native capture: HDR peak luminance unavailable; assuming 1000 nits.");
                    return new DisplayProfile(IsHdrColorSpace(description.ColorSpace), white, peak);
                }
            }
        }
        catch { }
        return DisplayProfile.Unknown;
    }

    private static bool TryGetSdrWhiteLevel(nint monitor, out float nits)
    {
        nits = 80;
        try
        {
            var monitorName = new MonitorInfoEx { DeviceName = string.Empty };
            monitorName.Size = (uint)Marshal.SizeOf<MonitorInfoEx>();
            if (!GetMonitorInfo(monitor, ref monitorName)) return false;
            uint pathCount = 0, modeCount = 0;
            QueryDisplayConfig(2, ref pathCount, null, ref modeCount, null, nint.Zero);
            if (pathCount == 0 || modeCount == 0) return false;
            var paths = new DisplayConfigPathInfo[pathCount];
            var modes = new DisplayConfigModeInfo[modeCount];
            if (QueryDisplayConfig(2, ref pathCount, paths, ref modeCount, modes, nint.Zero) != 0) return false;
            for (var i = 0; i < pathCount; i++)
            {
                var source = new DisplayConfigSourceName { Header = Header(1, Marshal.SizeOf<DisplayConfigSourceName>(), paths[i].SourceInfo.AdapterId, paths[i].SourceInfo.Id), ViewGdiDeviceName = string.Empty };
                if (DisplayConfigGetDeviceInfo(ref source) != 0 || !string.Equals(source.ViewGdiDeviceName, monitorName.DeviceName, StringComparison.OrdinalIgnoreCase)) continue;
                var white = new DisplayConfigSdrWhiteLevel { Header = Header(11, Marshal.SizeOf<DisplayConfigSdrWhiteLevel>(), paths[i].TargetInfo.AdapterId, paths[i].TargetInfo.Id) };
                if (DisplayConfigGetDeviceInfo(ref white) != 0 || white.SdrWhiteLevel < 1) return false;
                nits = 80f * white.SdrWhiteLevel / 1000f;
                return nits > 0 && nits < 10000;
            }
        }
        catch { }
        return false;
    }

    private static DisplayConfigDeviceInfoHeader Header(uint type, int size, Luid adapter, uint id) =>
        new() { Type = type, Size = (uint)size, AdapterId = adapter, Id = id };

    [DllImport("user32.dll", SetLastError = true)] private static extern int QueryDisplayConfig(uint flags, ref uint pathCount, [Out] DisplayConfigPathInfo[]? paths, ref uint modeCount, [Out] DisplayConfigModeInfo[]? modes, nint topologyId);
    [DllImport("user32.dll", SetLastError = true)] private static extern int DisplayConfigGetDeviceInfo(ref DisplayConfigSourceName requestPacket);
    [DllImport("user32.dll", SetLastError = true)] private static extern int DisplayConfigGetDeviceInfo(ref DisplayConfigSdrWhiteLevel requestPacket);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern bool GetMonitorInfo(nint monitor, ref MonitorInfoEx info);

    [StructLayout(LayoutKind.Sequential)] private struct Luid { public uint LowPart; public int HighPart; }
    [StructLayout(LayoutKind.Sequential)] private struct DisplayConfigDeviceInfoHeader { public uint Type, Size; public Luid AdapterId; public uint Id; }
    [StructLayout(LayoutKind.Sequential)] private struct DisplayConfigSourceInfo { public Luid AdapterId; public uint Id, ModeInfoIdx, StatusFlags; }
    [StructLayout(LayoutKind.Sequential)] private struct DisplayConfigTargetInfo { public Luid AdapterId; public uint Id, ModeInfoIdx, OutputTechnology, Rotation, Scaling; public long RefreshRate; public uint ScanLineOrdering, TargetAvailable, StatusFlags; }
    [StructLayout(LayoutKind.Sequential)] private struct DisplayConfigPathInfo { public DisplayConfigSourceInfo SourceInfo; public DisplayConfigTargetInfo TargetInfo; public uint Flags; }
    [StructLayout(LayoutKind.Explicit, Size = 64)] private struct DisplayConfigModeInfo { [FieldOffset(0)] public uint InfoType; [FieldOffset(4)] public uint Id; [FieldOffset(8)] public Luid AdapterId; }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)] private struct DisplayConfigSourceName { public DisplayConfigDeviceInfoHeader Header; [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string ViewGdiDeviceName; }
    [StructLayout(LayoutKind.Sequential)] private struct DisplayConfigSdrWhiteLevel { public DisplayConfigDeviceInfoHeader Header; public uint SdrWhiteLevel; }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)] private struct MonitorInfoEx { public uint Size; public Vortice.RawRect Monitor, Work; public uint Flags; [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string DeviceName; }

    public static ReplayHdrCompatibilityStatus Detect(ID3D11Device device, nint monitor)
    {
        var profile = GetDisplayProfile(device, monitor);
        return profile.IsHdr ? ReplayHdrCompatibilityStatus.PreparingConversion : ReplayHdrCompatibilityStatus.SdrDisplay;
    }

    // Windows desktop HDR commonly exposes linear scRGB (G10) rather than
    // PQ/HDR10. Both carry HDR headroom and require SDR conversion before the
    // existing BT.709 encoder path.
    internal static bool IsHdrColorSpace(ColorSpaceType colorSpace) => (int)colorSpace switch
    {
        1 => true, // RGB_FULL_G10_NONE_P709 (scRGB)
        12 or 13 or 14 or 16 => true, // ST.2084/PQ
        18 or 19 => true, // HLG
        25 => true, // RGB_FULL_G10_NONE_P2020
        _ => false
    };

}
