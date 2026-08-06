using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace ClypDat.App.Services;

// Raises this process's GPU scheduling priority.
//
// GPU work is dispatched by priority, and at normal priority a capture's
// encode submissions queue behind a game that is saturating the device. That
// is the shape of the problem measured here: avgEncodeMs sitting at 8-9ms
// while the desktop is calm and jumping to 37-63ms in exactly the windows
// where avgPresentGapMs shows the whole system presenting 10-25 times a
// second - NVENC is separate silicon, but the submissions that feed it are
// not, and they wait their turn like everything else.
//
// Found by looking at what SteelSeries GG Moments does: it captures the same
// way ClypDat does (DXGI Desktop Duplication) and encodes the same way
// (ffmpeg, D3D11/NV12 hardware context), but it calls this - and it holds
// 1440p60 on hardware where ClypDat does not.
//
// Best-effort by design. This is an undocumented (though long-stable) API, it
// can fail for lack of privilege, and nothing about capture depends on it
// succeeding - a failure just leaves the process where it already was.
[SupportedOSPlatform("windows")]
internal static class GpuSchedulingPriority
{
    // D3DKMT_SCHEDULINGPRIORITYCLASS. REALTIME is deliberately not used: it
    // outranks the desktop compositor, and a capture that makes the machine
    // feel worse than the frames it saves is a bad trade. HIGH is enough to
    // stop losing the race against a game's own submissions.
    private const int SchedulingPriorityClassHigh = 4;

    [DllImport("gdi32.dll", ExactSpelling = true)]
    private static extern int D3DKMTSetProcessSchedulingPriorityClass(nint processHandle, int priorityClass);

    private static bool _applied;

    public static void RaiseForCapture()
    {
        // Process-wide and permanent for the life of the process, so repeated
        // buffer arms do not need to keep re-applying it.
        if (_applied) return;
        _applied = true;

        try
        {
            var status = D3DKMTSetProcessSchedulingPriorityClass(
                Process.GetCurrentProcess().Handle, SchedulingPriorityClassHigh);
            if (status == 0)
            {
                AppLog.Info("GPU scheduling priority raised to HIGH for capture.");
            }
            else
            {
                // NTSTATUS, so a negative value is the failure detail worth
                // keeping - it distinguishes "not permitted" from "no such
                // entry point on this Windows build".
                AppLog.Info($"GPU scheduling priority unchanged (D3DKMTSetProcessSchedulingPriorityClass returned 0x{status:X8}).");
            }
        }
        catch (Exception error)
        {
            AppLog.Info($"GPU scheduling priority unavailable on this system: {error.Message}");
        }
    }
}
