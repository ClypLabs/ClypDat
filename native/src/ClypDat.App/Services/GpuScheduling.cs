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
// Two knobs, deliberately both:
//
//   - Process scheduling priority class (D3DKMTSetProcessSchedulingPriorityClass)
//     covers EVERY GPU context this process owns. That matters because NVENC's
//     context is not ours: ffmpeg's h264_nvenc creates its own CUDA/D3D device
//     internally, so no per-device call we make can reach it. FFmpeg is linked
//     in-process here (FFmpeg.AutoGen calls avcodec_send_frame directly), so a
//     process-wide class does reach it. This is the knob that targets the 108ms.
//
//   - Per-device GPU thread priority (IDXGIDevice::SetGPUThreadPriority) covers
//     the capture device specifically - the crop copy and the scale Blt. This
//     is the knob that targets the 1.7ms avgScaleMs.
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

    // Process-wide, and raised once for the life of the process rather than
    // per capture session. It is not lowered again on stop: a session ending
    // does not mean the next one is far away (the buffer restarts on every
    // game/monitor/settings change), and toggling a process-wide GPU class
    // from whichever thread happens to stop capture is more moving parts than
    // the gain justifies. The cost of leaving it raised while idle is nil -
    // an idle process submits no GPU work to prioritize.
    public static void TryRaiseProcessGpuPriority()
    {
        if (Interlocked.Exchange(ref _processPriorityRaised, 1) != 0) return;

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
