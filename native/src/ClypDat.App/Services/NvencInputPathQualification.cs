namespace ClypDat.App.Services;

internal enum NvencInputPath
{
    D3D11,
    SystemMemory
}

/// <summary>
/// Chooses the NVENC input path from startup measurements. Keeping this policy
/// independent from FFmpeg makes the preference and failure rules testable.
/// </summary>
internal static class NvencInputPathQualification
{
    internal const double TargetThreshold = 0.95;

    internal readonly record struct Result(
        bool Available,
        double FramesPerSecond,
        bool TimedOut = false,
        IReadOnlyList<double>? WindowFramesPerSecond = null)
    {
        internal double MinimumWindow => WindowFramesPerSecond is { Count: > 0 }
            ? WindowFramesPerSecond.Min()
            : FramesPerSecond;

        internal bool ReachedTarget(int targetFrameRate) =>
            Available && !TimedOut &&
            FramesPerSecond >= targetFrameRate * TargetThreshold &&
            (WindowFramesPerSecond is not { Count: > 0 } ||
             WindowFramesPerSecond.All(rate => rate >= targetFrameRate * TargetThreshold));
    }

    internal static NvencInputPath? Select(int targetFrameRate, Result d3d11, Result systemMemory)
    {
        var d3dAvailable = d3d11.Available && !d3d11.TimedOut;
        var systemAvailable = systemMemory.Available && !systemMemory.TimedOut;
        if (!d3dAvailable) return systemAvailable ? NvencInputPath.SystemMemory : null;
        if (!systemAvailable) return NvencInputPath.D3D11;

        // An idle benchmark cannot reveal D3D11 queue contention from a
        // foreground game. Prefer the independent upload path whenever it is
        // available; D3D11 is strictly an open-time fallback.
        return NvencInputPath.SystemMemory;
    }
}
