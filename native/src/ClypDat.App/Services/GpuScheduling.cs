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
// Two knobs. Only the second is on by default - see each for why:
//
//   - Process scheduling priority class (D3DKMTSetProcessSchedulingPriorityClass)
//     covers EVERY GPU context this process owns. That matters because NVENC's
//     context is not ours: ffmpeg's h264_nvenc creates its own CUDA/D3D device
//     internally, so no per-device call we make can reach it. FFmpeg is linked
//     in-process here (FFmpeg.AutoGen calls avcodec_send_frame directly), so a
//     process-wide class does reach it. It also reaches the UI's renderer,
//     which is why it is now opt-in and off by default.
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
    // D3DKMT_SCHEDULINGPRIORITYCLASS. REALTIME is deliberately not in this
    // list and never attempted: it outranks the desktop compositor, and a
    // background recorder that can starve DWM is a worse bug than a clip that
    // records at 53fps.
    private const int SchedulingPriorityClassAboveNormal = 3;
    private const int SchedulingPriorityClassHigh = 4;

    // DXGI clamps to [-7, 7] and returns E_INVALIDARG outside it. 7 is what a
    // capture app wants: the work is small, bounded, and latency-critical
    // relative to the game's, so it should cut ahead rather than queue.
    private const int MaxGpuThreadPriority = 7;

    private static int _processPriorityRaised;

    [DllImport("gdi32.dll")]
    private static extern int D3DKMTSetProcessSchedulingPriorityClass(nint process, int priorityClass);

    [DllImport("kernel32.dll")]
    private static extern nint GetCurrentProcess();

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int SetGpuThreadPriorityDelegate(nint self, int priority);

    // DISABLED - opt-in only, via CLYPDAT_GPU_PROCESS_PRIORITY=1.
    //
    // This was on by default for one build, and that build is the one an AMD
    // Radeon RX 9070 XT on Windows Server 2025 rendered as a solid white
    // window, with the clip toast a solid black block - a window that creates
    // and lays out fine and then presents nothing.
    //
    // The mechanism is not a guess about the driver, it is what this call
    // does. D3DKMTSetProcessSchedulingPriorityClass is PROCESS-wide, and that
    // was the whole reason for choosing it: ffmpeg's h264_nvenc builds its own
    // CUDA/D3D device internally, so nothing scoped to our capture device can
    // reach it. But "every GPU context in the process" is not only the encoder
    // - it is also Avalonia's ANGLE/D3D11 render device, which draws the UI.
    // Raising the whole process to the HIGH scheduling class reorders the UI's
    // presents against the compositor's, and a window whose presents never
    // land is exactly a blank one.
    //
    // The per-device call below is kept on by default and is not implicated:
    // it targets the capture device explicitly and cannot touch the renderer.
    // What is given up by disabling this is the lever aimed at NVENC's own
    // context - which was never isolated as a win either, since the encode
    // times it was meant to rescue were measured before and after other
    // changes, never against this one alone.
    //
    // Left in place rather than deleted so it can be re-tested deliberately,
    // on a machine where a blank window would be noticed immediately, instead
    // of shipping to everyone again on a hypothesis.
    public static void TryRaiseProcessGpuPriority()
    {
        if (Environment.GetEnvironmentVariable("CLYPDAT_GPU_PROCESS_PRIORITY")?.Trim() != "1") return;
        if (Interlocked.Exchange(ref _processPriorityRaised, 1) != 0) return;

        AppLog.Info("Native capture: CLYPDAT_GPU_PROCESS_PRIORITY=1 - raising process-wide GPU scheduling priority. This also applies to the UI's render device; a blank or white window means it is not supported here.");

        // HIGH first, ABOVE_NORMAL as the fallback. Some drivers/OS
        // configurations refuse HIGH for an unprivileged process; ABOVE_NORMAL
        // still puts capture ahead of the game's default NORMAL, which is the
        // entire point, so a refusal at HIGH is a downgrade and not a failure.
        if (TrySetProcessPriorityClass(SchedulingPriorityClassHigh, "HIGH")) return;
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
                AppLog.Info($"Native capture: GPU process scheduling priority raised to {name}.");
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
    public static void TryRaiseDeviceGpuPriority(nint devicePointer)
    {
        var dxgiDeviceIid = new Guid("54ec77fa-1377-44e6-8c32-88fd5f44c84c");
        var dxgiDevicePtr = nint.Zero;
        try
        {
            var hr = Marshal.QueryInterface(devicePointer, in dxgiDeviceIid, out dxgiDevicePtr);
            if (hr != 0 || dxgiDevicePtr == nint.Zero)
            {
                AppLog.Info($"Native capture: device does not expose IDXGIDevice (hr=0x{hr:X8}), leaving GPU thread priority at default.");
                return;
            }

            var vtable = Marshal.ReadIntPtr(dxgiDevicePtr, 0);
            var setPriorityPtr = Marshal.ReadIntPtr(vtable, 10 * nint.Size);
            var setPriority = Marshal.GetDelegateForFunctionPointer<SetGpuThreadPriorityDelegate>(setPriorityPtr);
            var result = setPriority(dxgiDevicePtr, MaxGpuThreadPriority);
            if (result == 0)
            {
                AppLog.Info($"Native capture: D3D11 device GPU thread priority set to {MaxGpuThreadPriority}.");
            }
            else
            {
                AppLog.Info($"Native capture: D3D11 device GPU thread priority refused (hr=0x{result:X8}), continuing at default.");
            }
        }
        catch (Exception error)
        {
            AppLog.Info($"Native capture: could not set D3D11 device GPU thread priority (non-fatal): {error.Message}");
        }
        finally
        {
            if (dxgiDevicePtr != nint.Zero) Marshal.Release(dxgiDevicePtr);
        }
    }
}
