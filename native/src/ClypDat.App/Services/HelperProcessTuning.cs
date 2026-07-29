using System.Diagnostics;

namespace ClypDat.App.Services;

// Shared shaping for every short-lived helper process ClypDat spawns (ffmpeg
// and ffprobe, from AudioCapturePipeline and MediaProbeService).
//
// The problem this exists for: ffmpeg defaults to `-threads 0`, meaning "use
// every logical core". On a 32-thread machine a single thumbnail grab - which
// only ever decodes a handful of frames - would spin up 32 decode threads, and
// a save can have several such processes in flight at once. BelowNormal
// priority stops them starving the game, but priority does nothing about the
// cost that actually shows up in a frame-time graph: a hundred threads landing
// across every core evicts the game's working set from L2/L3 and drags its own
// threads through the scheduler. Measured as a game dropping from 240 to 190
// fps for the length of a clip save, on a machine with 31 idle cores.
//
// Capping threads and confining the work to a slice of the machine keeps the
// same total work off the cores the game is actually running hot on. Saves get
// no slower in practice: these are either single-frame grabs, short audio
// filter graphs, or stream copies that are I/O bound rather than CPU bound.
internal static class HelperProcessTuning
{
    // A quarter of the machine, floor 1, ceiling 4. Enough that a filter graph
    // or a long stream copy still parallelises usefully, nowhere near enough to
    // saturate the box.
    public static int ThreadCount { get; } = Math.Clamp(Environment.ProcessorCount / 8, 1, 4);

    // Only worth confining on a machine with cores to spare - below this the
    // slice would be most of the machine anyway, and pinning a background
    // process onto the same cores the game wants is worse than leaving the
    // scheduler alone.
    private const int MinimumProcessorsForAffinity = 8;

    // ffmpeg accepts all three as global options, so they go in front of
    // everything else. -threads alone still leaves libavfilter free to fan out
    // across every core on a filter_complex graph, which is exactly what the
    // audio segment builds use.
    public static IEnumerable<string> WithThreadLimits(string fileName, IEnumerable<string> args)
    {
        if (!IsFfmpeg(fileName)) return args;
        var limit = ThreadCount.ToString();
        return new[] { "-threads", limit, "-filter_threads", limit, "-filter_complex_threads", limit }.Concat(args);
    }

    private static bool IsFfmpeg(string fileName) =>
        Path.GetFileNameWithoutExtension(fileName).Equals("ffmpeg", StringComparison.OrdinalIgnoreCase);

    // BelowNormal plus a top-end slice of the logical processors. Both are
    // best-effort: a process that has already exited (a fast failure, a bad
    // argument list) throws from either, and neither is worth failing a save
    // over.
    public static void ApplyBackgroundShaping(Process process)
    {
        try
        {
            process.PriorityClass = ProcessPriorityClass.BelowNormal;
        }
        catch
        {
            // Priority is a nice-to-have; never let it block starting the process.
        }

        try
        {
            var mask = BackgroundAffinityMask();
            if (mask != 0) process.ProcessorAffinity = (IntPtr)mask;
        }
        catch
        {
            // Affinity is unavailable on some platforms/configurations, and the
            // process may already be gone. Same deal - best effort.
        }
    }

    // The TOP ThreadCount logical processors. Deliberately the high end: core 0
    // carries a disproportionate share of driver and interrupt work, and games
    // commonly pin their own critical threads to the low ones.
    private static long BackgroundAffinityMask()
    {
        var processors = Environment.ProcessorCount;
        if (processors < MinimumProcessorsForAffinity) return 0;
        // Never wider than 62 bits - ProcessorAffinity is a pointer-sized mask,
        // and a machine reporting more than that is outside what a single
        // affinity mask can address anyway.
        var width = Math.Min(ThreadCount, Math.Min(processors, 62));
        var mask = 0L;
        for (var i = 0; i < width; i++) mask |= 1L << (Math.Min(processors, 62) - 1 - i);
        return mask;
    }
}
