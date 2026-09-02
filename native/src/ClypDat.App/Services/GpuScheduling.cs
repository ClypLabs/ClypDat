using System.Runtime.InteropServices;

namespace ClypDat.App.Services;

// Everything ClypDat submits to the GPU while a game is running - the crop
// copy, the Video Processor scale/convert Blt, the readback, and NVENC's own
// upload and encode - queues behind that game's 3D work at the same scheduling
// priority the game has. On a machine with GPU headroom that costs nothing. On
// one where the game is already using ~90% of the card, it is the whole
// problem: measured on a 4070 Ti at P2/1440p60, avgScaleMs went 0.07 -> 1.7ms
// and avgEncodeMs 0.63 -> 108ms the moment a GPU-bound game was in the
// foreground. NVENC is not 170x slower under load - it is a fixed-function
// block that does 1440p60 in single-digit milliseconds. It was waiting for a
// slot. The encode tick then blew past its 16.7ms budget, the 30-deep encode
// queue filled, frames were dropped, and clips came out at ~53fps with 60
// configured.
//
// EncoderTuningService's response to that was to demote the preset, then drop
// 60 -> 30fps, then 1440p -> 720p - three quality reductions aimed at an
// encoder that was never the bottleneck. Raising priority attacks the actual
// cause, so those levers stop being pulled for the wrong reason.
//
//   - Process scheduling priority class (D3DKMTSetProcessSchedulingPriorityClass)
//     covers EVERY GPU context this process owns. That matters because NVENC's
//     context is not ours: ffmpeg's h264_nvenc creates its own CUDA/D3D device
//     internally, so no per-device call we make can reach it. FFmpeg is linked
//     in-process here (FFmpeg.AutoGen calls avcodec_send_frame directly), so a
//     process-wide class does reach it. The recorder lives in a dedicated
//     ClypDatRecorder.exe process, so this cannot elevate Avalonia's renderer.
//
//   - Per-device GPU thread priority (IDXGIDevice::SetGPUThreadPriority) covers
//     the capture device specifically - the crop copy and the scale Blt. This
//     is the knob that targets the 1.7ms avgScaleMs, and it cannot affect any
//     device but the one it is handed.
//
// Both are best-effort and heavily logged. A driver or OS that refuses either
// leaves capture running exactly as it did before; nothing here is load-bearing.
internal static class GpuScheduling
{
    // D3DKMT_SCHEDULINGPRIORITYCLASS. The worker's GPU work is bounded by the
    // selected capture cadence. REALTIME is safe to try here because the UI is
    // no longer hosted in this process; HIGH and ABOVE_NORMAL remain fallbacks
    // for drivers or policies that refuse it.
    private const int SchedulingPriorityClassAboveNormal = 3;
    private const int SchedulingPriorityClassHigh = 4;
    private const int SchedulingPriorityClassRealtime = 5;

    // DXGI clamps to [-7, 7] and returns E_INVALIDARG outside it. 7 is what a
    // capture app wants: the work is small, bounded, and latency-critical
    // relative to the game's, so it should cut ahead rather than queue.
    private const int MaxGpuThreadPriority = 7;
    internal const int CaptureDevicePriority = 1;

    private static int _processPriorityRaised;

    [DllImport("gdi32.dll")]
    private static extern int D3DKMTSetProcessSchedulingPriorityClass(nint process, int priorityClass);

    [DllImport("gdi32.dll")]
    private static extern int D3DKMTGetProcessSchedulingPriorityClass(nint process, out int priorityClass);

    [DllImport("kernel32.dll")]
    private static extern nint GetCurrentProcess();

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int SetGpuThreadPriorityDelegate(nint self, int priority);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int GetGpuThreadPriorityDelegate(nint self, out int priority);

    // Only the capture worker calls this. The UI process stays NORMAL.
    internal static bool IsProcessPriorityElevationEnabled(string? value)
        => ResolveProcessPriority(value) is not null;

    internal static string? ResolveProcessPriority(string? value) => value?.Trim().ToLowerInvariant() switch
    {
        null or "" or "realtime" => "REALTIME",
        "high" or "1" => "HIGH",
        "above-normal" => "ABOVE_NORMAL",
        "normal" or "0" => null,
        _ => "HIGH"
    };

    internal static int ResolveDevicePriority(string? value) =>
        int.TryParse(value, out var parsed) && parsed is >= -7 and <= 7 ? parsed : CaptureDevicePriority;

    // Kept for existing callers/tests that were named before the two knobs
    // were separated.
    internal static bool IsPriorityElevationEnabled(string? value) => IsProcessPriorityElevationEnabled(value);

    private static bool IsProcessPriorityElevationEnabled()
        => IsProcessPriorityElevationEnabled(Environment.GetEnvironmentVariable("CLYPDAT_GPU_PROCESS_PRIORITY"));

    public static void TryRaiseProcessGpuPriority()
    {
        if (Interlocked.Exchange(ref _processPriorityRaised, 1) != 0) return;
        var requested = ResolveProcessPriority(Environment.GetEnvironmentVariable("CLYPDAT_GPU_PROCESS_PRIORITY"));
        if (requested is null) return;

        AppLog.Info($"Capture worker: raising process-wide GPU scheduling priority to {requested}.");

        if (requested == "REALTIME" && TrySetProcessPriorityClass(SchedulingPriorityClassRealtime, "REALTIME")) return;
        if (requested == "HIGH" && TrySetProcessPriorityClass(SchedulingPriorityClassHigh, "HIGH")) return;
        if (requested == "REALTIME" && TrySetProcessPriorityClass(SchedulingPriorityClassHigh, "HIGH")) return;
        if (TrySetProcessPriorityClass(SchedulingPriorityClassAboveNormal, "ABOVE_NORMAL")) return;

        AppLog.Info("Native capture: GPU process scheduling priority could not be raised - capture will queue behind the foreground game as before.");
    }

    private static bool TrySetProcessPriorityClass(int priorityClass, string name)
    {
        try
        {
            // NTSTATUS, not HRESULT - 0 is STATUS_SUCCESS and anything else is
            // a failure, so the usual "negative means error" HRESULT check
            // would wrongly accept every positive NTSTATUS warning code.
            var status = D3DKMTSetProcessSchedulingPriorityClass(GetCurrentProcess(), priorityClass);
            if (status == 0)
            {
                try
                {
                    var readBackStatus = D3DKMTGetProcessSchedulingPriorityClass(GetCurrentProcess(), out var applied);
                    AppLog.Info(readBackStatus == 0
                        ? $"Native capture: GPU process scheduling priority requested={name}, applied={ProcessPriorityName(applied)}."
                        : $"Native capture: GPU process scheduling priority raised to {name}; read-back refused (status=0x{readBackStatus:X8}).");
                }
                catch (Exception error)
                {
                    AppLog.Info($"Native capture: GPU process scheduling priority raised to {name}; read-back unavailable (non-fatal): {error.Message}");
                }
                return true;
            }

            AppLog.Info($"Native capture: GPU process scheduling priority {name} refused (status=0x{status:X8}).");
            return false;
        }
        catch (Exception error)
        {
            // EntryPointNotFoundException on an OS without the export, or a
            // DllNotFoundException in a headless/server SKU. Neither is fatal.
            AppLog.Info($"Native capture: GPU process scheduling priority {name} unavailable (non-fatal): {error.Message}");
            return false;
        }
    }

    internal static string ProcessPriorityName(int value) => value switch
    {
        0 => "IDLE",
        1 => "BELOW_NORMAL",
        2 => "NORMAL",
        3 => "ABOVE_NORMAL",
        4 => "HIGH",
        5 => "REALTIME",
        _ => $"UNKNOWN({value})"
    };

    // Same raw-vtable approach as TryMarkDeviceMultithreadProtected in
    // NativeReplayBuffer, and for the same reason: this call has to reach
    // IDXGIDevice on whatever COM pointer the device exposes, without
    // depending on a Vortice wrapper existing or keeping its name across
    // package versions.
    //
    // IDXGIDevice vtable, after IUnknown (QueryInterface/AddRef/Release = 0-2)
    // and IDXGIObject (SetPrivateData/SetPrivateDataInterface/GetPrivateData/
    // GetParent = 3-6): GetAdapter=7, CreateSurface=8, QueryResourceResidency=9,
    // SetGPUThreadPriority=10, GetGPUThreadPriority=11.
    public static int? TryRaiseDeviceGpuPriority(nint devicePointer, string role)
    {
        var dxgiDeviceIid = new Guid("54ec77fa-1377-44e6-8c32-88fd5f44c84c");
        var dxgiDevicePtr = nint.Zero;
        try
        {
            var hr = Marshal.QueryInterface(devicePointer, in dxgiDeviceIid, out dxgiDevicePtr);
            if (hr != 0 || dxgiDevicePtr == nint.Zero)
            {
                AppLog.Info($"Native capture: {role} D3D11 device does not expose IDXGIDevice (hr=0x{hr:X8}); GPU thread priority remains default.");
                return null;
            }

            var vtable = Marshal.ReadIntPtr(dxgiDevicePtr, 0);
            var setPriorityPtr = Marshal.ReadIntPtr(vtable, 10 * nint.Size);
            var setPriority = Marshal.GetDelegateForFunctionPointer<SetGpuThreadPriorityDelegate>(setPriorityPtr);
            var requested = ResolveDevicePriority(Environment.GetEnvironmentVariable("CLYPDAT_GPU_DEVICE_PRIORITY"));
            var result = setPriority(dxgiDevicePtr, requested);
            if (result == 0)
            {
                var getPriorityPtr = Marshal.ReadIntPtr(vtable, 11 * nint.Size);
                var getPriority = Marshal.GetDelegateForFunctionPointer<GetGpuThreadPriorityDelegate>(getPriorityPtr);
                var readBackResult = getPriority(dxgiDevicePtr, out var applied);
                if (readBackResult == 0)
                {
                    AppLog.Info($"Native capture: {role} D3D11 device GPU thread priority requested={requested}, applied={applied}.");
                    return applied;
                }
                AppLog.Info($"Native capture: {role} D3D11 device GPU thread priority set to {requested}; read-back refused (hr=0x{readBackResult:X8}).");
                return requested;
            }
            else
            {
                AppLog.Info($"Native capture: {role} D3D11 device GPU thread priority refused (hr=0x{result:X8}); continuing at default.");
                return null;
            }
        }
        catch (Exception error)
        {
            AppLog.Info($"Native capture: could not set {role} D3D11 device GPU thread priority (non-fatal): {error.Message}");
            return null;
        }
        finally
        {
            if (dxgiDevicePtr != nint.Zero) Marshal.Release(dxgiDevicePtr);
        }
    }
}
