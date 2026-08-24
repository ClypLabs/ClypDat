namespace ClypDat.App.Services;

// The configured frame rate is a ceiling in variable-frame-rate mode. Keeping
// the policy independent from the native capture loop makes the persisted
// setting, scheduler and tests agree on the same small set of invariants.
public static class ReplayFrameTimingPolicy
{
    public const int MinimumFrameRate = 30;
    public const int MaximumFrameRate = 120;
    public const string Variable = "VFR";
    public const string Constant = "CFR";

    // The pacing thread wakes close to, but not exactly on, every frame boundary.
    // Treating a sub-millisecond early/late wake-up as a new clock origin makes
    // that small scheduler jitter accumulate into a 30-40fps VFR recording.
    // Keep a stable target timeline instead, as dedicated capture frame-rate
    // stabilizers do, while leaving a narrow lead for Windows' timer jitter.
    private static readonly TimeSpan VariableDeadlineLead = TimeSpan.FromMilliseconds(0.75);

    public static string Normalize(string? value) =>
        string.Equals(value, Variable, StringComparison.OrdinalIgnoreCase) ? Variable : Constant;

    public static bool IsVariable(string? value) =>
        string.Equals(Normalize(value), Variable, StringComparison.Ordinal);

    // Keep only a small, bounded amount of work in front of the encoder. A
    // replay needs the most recent game frame, not a second of stale history.
    public static int EncodeQueueCapacity(int frameRate) =>
        Math.Clamp((int)Math.Ceiling(Math.Clamp(frameRate, MinimumFrameRate, MaximumFrameRate) / 8.0), 4, 15);

    public static long RealPtsMicroseconds(TimeSpan elapsed, long previousPts) =>
        Math.Max(previousPts + 1, (long)Math.Round(Math.Max(0, elapsed.TotalMilliseconds) * 1_000));

    /// <summary>
    /// Advances a variable-frame-rate capture deadline without allowing normal
    /// scheduler jitter to lower the selected FPS. Long gaps are coalesced into
    /// one advance so VFR never synthesizes duplicate frames to catch up.
    /// </summary>
    public static bool TryAdvanceVariableDeadline(TimeSpan now, TimeSpan frameInterval, ref TimeSpan lastScheduledAt)
    {
        if (frameInterval <= TimeSpan.Zero) return false;

        var elapsed = now - lastScheduledAt;
        if (elapsed + VariableDeadlineLead < frameInterval) return false;

        var intervals = Math.Max(1L, (long)Math.Floor((elapsed + VariableDeadlineLead).Ticks / (double)frameInterval.Ticks));
        lastScheduledAt += TimeSpan.FromTicks(checked(frameInterval.Ticks * intervals));
        return true;
    }
}
