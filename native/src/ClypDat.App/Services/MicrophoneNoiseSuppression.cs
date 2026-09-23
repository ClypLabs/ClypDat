namespace ClypDat.App.Services;

// Shared UI bounds. RecorderCore applies same limits to the native microphone gate.
internal static class MicrophoneNoiseSuppression
{
    public const double MinimumGateThresholdDb = -100;
    public const double MaximumGateThresholdDb = -25;
    public const double DefaultGateThresholdDb = MinimumGateThresholdDb;

    public static double ClampGateThresholdDb(double value)
    {
        if (!double.IsFinite(value)) return DefaultGateThresholdDb;
        return Math.Clamp(value, MinimumGateThresholdDb, MaximumGateThresholdDb);
    }

}
