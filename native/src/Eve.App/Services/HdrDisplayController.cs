using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Eve.App.Services;

// Toggles Windows' per-display "HDR" (advanced color) state off/on around a
// replay session, via the same public display-config API Windows Settings
// and tools like NVIDIA's overlay use (DisplayConfigGetDeviceInfo /
// DisplayConfigSetDeviceInfo with DISPLAYCONFIG_..._ADVANCED_COLOR_*). This
// is OS-level, so it applies identically whatever capture backend (OBS or
// Windows Capture) is recording underneath it.
[SupportedOSPlatform("windows")]
public static class HdrDisplayController
{
    private const uint QDC_ONLY_ACTIVE_PATHS = 0x00000002;
    private const int DISPLAYCONFIG_DEVICE_INFO_GET_ADVANCED_COLOR_INFO = 9;
    private const int DISPLAYCONFIG_DEVICE_INFO_SET_ADVANCED_COLOR_STATE = 10;
    private const int ERROR_SUCCESS = 0;

    private static readonly object Lock = new();
    private static List<(LUID AdapterId, uint TargetId, bool WasEnabled)>? _restoreState;

    public static void ForceSdrOn()
    {
        lock (Lock)
        {
            if (_restoreState is not null) return;
            try
            {
                var restore = new List<(LUID, uint, bool)>();
                foreach (var (adapterId, targetId) in EnumerateActiveTargets())
                {
                    if (!TryGetAdvancedColorInfo(adapterId, targetId, out var supported, out var enabled)) continue;
                    if (!supported) continue;
                    restore.Add((adapterId, targetId, enabled));
                    if (enabled) SetAdvancedColorEnabled(adapterId, targetId, false);
                }

                _restoreState = restore;
                AppLog.Info($"Force SDR: disabled HDR on {restore.Count(r => r.Item3)} of {restore.Count} display(s).");
            }
            catch (Exception error)
            {
                AppLog.Error("Force SDR: failed to disable HDR", error);
                _restoreState = null;
            }
        }
    }

    public static void RestoreHdr()
    {
        lock (Lock)
        {
            if (_restoreState is null) return;
            try
            {
                foreach (var (adapterId, targetId, wasEnabled) in _restoreState)
                {
                    if (wasEnabled) SetAdvancedColorEnabled(adapterId, targetId, true);
                }

                AppLog.Info("Force SDR: restored previous HDR state.");
            }
            catch (Exception error)
            {
                AppLog.Error("Force SDR: failed to restore HDR", error);
            }
            finally
            {
                _restoreState = null;
            }
        }
    }

    private static IEnumerable<(LUID AdapterId, uint TargetId)> EnumerateActiveTargets()
    {
        var result = GetDisplayConfigBufferSizes(QDC_ONLY_ACTIVE_PATHS, out var pathCount, out var modeCount);
        if (result != ERROR_SUCCESS)
        {
            AppLog.Error($"Force SDR: GetDisplayConfigBufferSizes failed, code={result}.", null);
            yield break;
        }

        var paths = new DISPLAYCONFIG_PATH_INFO[pathCount];
        var modes = new DISPLAYCONFIG_MODE_INFO_RAW[modeCount];
        result = QueryDisplayConfig(QDC_ONLY_ACTIVE_PATHS, ref pathCount, paths, ref modeCount, modes, IntPtr.Zero);
        if (result != ERROR_SUCCESS)
        {
            AppLog.Error($"Force SDR: QueryDisplayConfig failed, code={result}, pathCount={pathCount}.", null);
            yield break;
        }

        AppLog.Info($"Force SDR: QueryDisplayConfig returned {pathCount} path(s).");
        for (var i = 0; i < pathCount; i++)
        {
            yield return (paths[i].targetInfo.adapterId, paths[i].targetInfo.id);
        }
    }

    private static bool TryGetAdvancedColorInfo(LUID adapterId, uint targetId, out bool supported, out bool enabled)
    {
        supported = false;
        enabled = false;
        var request = new DISPLAYCONFIG_GET_ADVANCED_COLOR_INFO
        {
            header = new DISPLAYCONFIG_DEVICE_INFO_HEADER
            {
                type = DISPLAYCONFIG_DEVICE_INFO_GET_ADVANCED_COLOR_INFO,
                size = Marshal.SizeOf<DISPLAYCONFIG_GET_ADVANCED_COLOR_INFO>(),
                adapterId = adapterId,
                id = targetId
            }
        };

        var result = DisplayConfigGetDeviceInfo(ref request);
        if (result != ERROR_SUCCESS)
        {
            AppLog.Error($"Force SDR: DisplayConfigGetDeviceInfo failed, code={result}.");
            return false;
        }

        supported = (request.value & 0x1) != 0;
        enabled = (request.value & 0x2) != 0;
        AppLog.Info($"Force SDR: target id={targetId} supported={supported} enabled={enabled} rawValue=0x{request.value:X}.");
        return true;
    }

    private static void SetAdvancedColorEnabled(LUID adapterId, uint targetId, bool enable)
    {
        var request = new DISPLAYCONFIG_SET_ADVANCED_COLOR_STATE
        {
            header = new DISPLAYCONFIG_DEVICE_INFO_HEADER
            {
                type = DISPLAYCONFIG_DEVICE_INFO_SET_ADVANCED_COLOR_STATE,
                size = Marshal.SizeOf<DISPLAYCONFIG_SET_ADVANCED_COLOR_STATE>(),
                adapterId = adapterId,
                id = targetId
            },
            value = enable ? 1u : 0u
        };

        DisplayConfigSetDeviceInfo(ref request);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct LUID
    {
        public uint LowPart;
        public int HighPart;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DISPLAYCONFIG_RATIONAL
    {
        public uint Numerator;
        public uint Denominator;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DISPLAYCONFIG_PATH_SOURCE_INFO
    {
        public LUID adapterId;
        public uint id;
        public uint modeInfoIdx;
        public uint statusFlags;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DISPLAYCONFIG_PATH_TARGET_INFO
    {
        public LUID adapterId;
        public uint id;
        public uint modeInfoIdx;
        public int outputTechnology;
        public int rotation;
        public int scaling;
        public DISPLAYCONFIG_RATIONAL refreshRate;
        public int scanLineOrdering;
        public int targetAvailable;
        public uint statusFlags;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DISPLAYCONFIG_PATH_INFO
    {
        public DISPLAYCONFIG_PATH_SOURCE_INFO sourceInfo;
        public DISPLAYCONFIG_PATH_TARGET_INFO targetInfo;
        public uint flags;
    }

    // Opaque 64-byte placeholder matching sizeof(DISPLAYCONFIG_MODE_INFO) -
    // contents are never read, QueryDisplayConfig just needs a correctly
    // sized buffer to write into.
    [StructLayout(LayoutKind.Sequential)]
    private struct DISPLAYCONFIG_MODE_INFO_RAW
    {
        public ulong _0, _1, _2, _3, _4, _5, _6, _7;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DISPLAYCONFIG_DEVICE_INFO_HEADER
    {
        public int type;
        public int size;
        public LUID adapterId;
        public uint id;
    }

    // Windows 11 24H2 (build 26100+) extended this struct with colorEncoding
    // and bitsPerColorChannel; DisplayConfigGetDeviceInfo validates
    // header.size against the OS's known struct size and returns
    // ERROR_INVALID_PARAMETER (87) if it doesn't match, so the older
    // 24-byte (header + value only) layout fails on current Windows.
    [StructLayout(LayoutKind.Sequential)]
    private struct DISPLAYCONFIG_GET_ADVANCED_COLOR_INFO
    {
        public DISPLAYCONFIG_DEVICE_INFO_HEADER header;
        public uint value;
        public uint colorEncoding;
        public uint bitsPerColorChannel;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DISPLAYCONFIG_SET_ADVANCED_COLOR_STATE
    {
        public DISPLAYCONFIG_DEVICE_INFO_HEADER header;
        public uint value;
    }

    [DllImport("user32.dll")]
    private static extern int GetDisplayConfigBufferSizes(uint flags, out uint numPathArrayElements, out uint numModeInfoArrayElements);

    [DllImport("user32.dll")]
    private static extern int QueryDisplayConfig(
        uint flags,
        ref uint numPathArrayElements,
        [Out] DISPLAYCONFIG_PATH_INFO[] pathArray,
        ref uint numModeInfoArrayElements,
        [Out] DISPLAYCONFIG_MODE_INFO_RAW[] modeInfoArray,
        IntPtr currentTopologyId);

    [DllImport("user32.dll")]
    private static extern int DisplayConfigGetDeviceInfo(ref DISPLAYCONFIG_GET_ADVANCED_COLOR_INFO requestPacket);

    [DllImport("user32.dll")]
    private static extern int DisplayConfigSetDeviceInfo(ref DISPLAYCONFIG_SET_ADVANCED_COLOR_STATE requestPacket);
}
