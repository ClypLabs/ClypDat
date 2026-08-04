using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace ClypDat.App.Services;

// Registers the calling thread with the Multimedia Class Scheduler Service
// ("Pro Audio" task) for as long as the scope lives.
//
// WASAPI capture is a hard real-time job with a soft deadline: miss the window
// in which the shared-mode buffer has to be drained and the data in it is gone,
// permanently, and the saved clip has a hole where that audio was. The capture
// loops used to be plain thread-pool work items at default priority, which on a
// machine whose GPU encoder is already overloaded (Iris Xe laptop, 1080p60 QSV
// sitting at 15fps of a requested 30) is exactly the work the Windows scheduler
// deprioritises. Real sessions lost 250-1400ms of audio at a time, on all three
// tracks simultaneously - the signature of the whole process being starved
// rather than any one device failing.
//
// MMCSS is what Windows gives audio software for precisely this: the registered
// thread gets scheduled in the Pro Audio priority band and is guaranteed CPU
// even while lower-priority threads are saturating every core.
[SupportedOSPlatform("windows")]
internal readonly struct MmcssScope : IDisposable
{
    private readonly IntPtr _handle;

    private MmcssScope(IntPtr handle) => _handle = handle;

    public static MmcssScope ProAudio(string context)
    {
        try
        {
            uint taskIndex = 0;
            var handle = AvSetMmThreadCharacteristics("Pro Audio", ref taskIndex);
            if (handle == IntPtr.Zero)
            {
                AppLog.Debug($"MMCSS registration failed for {context}: win32={Marshal.GetLastWin32Error()}. Capture continues at normal thread priority.");
                return default;
            }

            // Highest within the Pro Audio band. The band itself is what
            // matters; this just avoids sitting behind other MMCSS work.
            AvSetMmThreadPriority(handle, AvrtPriorityCritical);
            return new MmcssScope(handle);
        }
        catch (DllNotFoundException)
        {
            return default;
        }
        catch (EntryPointNotFoundException)
        {
            return default;
        }
    }

    public void Dispose()
    {
        if (_handle == IntPtr.Zero) return;
        try
        {
            AvRevertMmThreadCharacteristics(_handle);
        }
        catch
        {
            // Thread is ending anyway.
        }
    }

    private const int AvrtPriorityCritical = 2;

    [DllImport("avrt.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr AvSetMmThreadCharacteristics(string taskName, ref uint taskIndex);

    [DllImport("avrt.dll", SetLastError = true)]
    private static extern bool AvSetMmThreadPriority(IntPtr handle, int priority);

    [DllImport("avrt.dll", SetLastError = true)]
    private static extern bool AvRevertMmThreadCharacteristics(IntPtr handle);
}
