using System.Diagnostics;
using System.Runtime.InteropServices;

namespace ClypDat.App.Services;

// A steady UTC-like timeline for capture/save bookkeeping, immune to system
// clock steps (NTP corrections, manual time changes). A backward ~11s NTP
// step mid-session once tore every DateTime.UtcNow-anchored comparison apart
// (end-anchoring, pad-to-now, video frame stamps) while the QPC-placed WAV
// data stayed correct - clips saved after the step came out seconds desynced.
// Stopwatch reads the same QPC that WASAPI packet timestamps use, so as long
// as every timeline-relevant timestamp comes from here, the whole pipeline
// stays self-consistent no matter what the wall clock does. User-facing
// dates (sidecar CreatedAt, filenames, log lines) should keep DateTime.
internal static class MonotonicClock
{
    private static readonly DateTime _utcBase = DateTime.UtcNow;
    private static readonly long _stopwatchBase = Stopwatch.GetTimestamp();

    public static DateTime UtcNow => _utcBase + Stopwatch.GetElapsedTime(_stopwatchBase, Stopwatch.GetTimestamp());

    public static double ToSharedSeconds(DateTime utc) =>
        (double)_stopwatchBase / Stopwatch.Frequency + (utc - _utcBase).TotalSeconds;

    public static double SharedSeconds => (double)Stopwatch.GetTimestamp() / Stopwatch.Frequency;

    public static string BootId { get; } = ReadBootId();
    private static string ReadBootId()
    {
        // SystemBootEnvironmentInformation identifies this boot independently
        // of wall-clock corrections and app/recorder restart order.
        var buffer = Marshal.AllocHGlobal(32);
        try
        {
            if (OperatingSystem.IsWindows() && NtQuerySystemInformation(90, buffer, 32, out _) == 0)
                return Marshal.PtrToStructure<Guid>(buffer).ToString();
        }
        finally { Marshal.FreeHGlobal(buffer); }
        return Guid.NewGuid().ToString(); // Unknown clock identity cannot recover history.
    }
    [DllImport("ntdll.dll")]
    private static extern int NtQuerySystemInformation(int informationClass, IntPtr buffer, int length, out int returnedLength);

    // How far the system clock has stepped away from this timeline since
    // process start. ~0 normally; jumps when NTP/manual adjustments happen.
    public static TimeSpan SystemClockOffset => DateTime.UtcNow - UtcNow;
}
