namespace ClypDat.App.Services;

// The configured frame rate is a ceiling in variable-frame-rate mode. Keeping
// the policy independent from the native capture loop makes the persisted
// setting, scheduler and tests agree on the same small set of invariants.
public static class ReplayFrameTimingPolicy
{
    public const string Variable = "VFR";
    public const string Constant = "CFR";

    public static string Normalize(string? value) =>
        string.Equals(value, Constant, StringComparison.OrdinalIgnoreCase) ? Constant : Variable;

    public static bool IsVariable(string? value) =>
        string.Equals(Normalize(value), Variable, StringComparison.Ordinal);

    // Keep only a small, bounded amount of work in front of the encoder. A
    // replay needs the most recent game frame, not a second of stale history.
    public static int EncodeQueueCapacity(int frameRate) =>
        Math.Clamp((int)Math.Ceiling(Math.Clamp(frameRate, 30, 144) / 8.0), 4, 18);

    public static long RealPtsMicroseconds(TimeSpan elapsed, long previousPts) =>
        Math.Max(previousPts + 1, (long)Math.Round(Math.Max(0, elapsed.TotalMilliseconds) * 1_000));
}
