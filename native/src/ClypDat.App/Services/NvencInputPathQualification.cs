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
    internal const double D3D11PreferenceTolerance = 0.03;
    internal const double TargetThreshold = 0.95;

    internal readonly record struct Result(bool Available, double FramesPerSecond, bool TimedOut = false)
    {
        internal bool ReachedTarget(int targetFrameRate) =>
            Available && !TimedOut && FramesPerSecond >= targetFrameRate * TargetThreshold;
    }

    internal static NvencInputPath? Select(int targetFrameRate, Result d3d11, Result systemMemory)
    {
        var d3dAvailable = d3d11.Available && !d3d11.TimedOut;
        var systemAvailable = systemMemory.Available && !systemMemory.TimedOut;
        if (!d3dAvailable) return systemAvailable ? NvencInputPath.SystemMemory : null;
        if (!systemAvailable) return NvencInputPath.D3D11;

        // Zero-copy wins ties and near-ties. It avoids readback/upload pressure
        // during real gameplay even when an idle startup benchmark is equal.
        return d3d11.FramesPerSecond >= systemMemory.FramesPerSecond * (1 - D3D11PreferenceTolerance)
            ? NvencInputPath.D3D11
            : NvencInputPath.SystemMemory;
    }
}
