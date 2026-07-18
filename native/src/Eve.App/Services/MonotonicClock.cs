using System.Diagnostics;

namespace Eve.App.Services;

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

    // How far the system clock has stepped away from this timeline since
    // process start. ~0 normally; jumps when NTP/manual adjustments happen.
    public static TimeSpan SystemClockOffset => DateTime.UtcNow - UtcNow;
}
