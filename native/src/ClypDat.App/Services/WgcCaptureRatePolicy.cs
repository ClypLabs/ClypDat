namespace ClypDat.App.Services;

internal readonly record struct WgcMinimumUpdateIntervalResult(
    bool InterfaceAvailable,
    TimeSpan Requested,
    TimeSpan? Applied,
    string? Failure = null);

internal static class WgcMinimumUpdateIntervalPolicy
{
    public static TimeSpan FromFrameRate(int frameRate) =>
        TimeSpan.FromSeconds(1d / Math.Clamp(frameRate, ReplayFrameTimingPolicy.MinimumFrameRate, ReplayFrameTimingPolicy.MaximumFrameRate));

    public static WgcMinimumUpdateIntervalResult Unsupported(int frameRate) =>
        Unavailable(frameRate);

    public static WgcMinimumUpdateIntervalResult Unavailable(int frameRate, string? failure = null) =>
        new(false, FromFrameRate(frameRate), null, failure);
}

// WGC can intentionally slow its producer while a window is backgrounded. Only
// foreground, encoder-healthy diagnostic windows can prove WGC is the limiter.
internal sealed class WgcCadenceFallbackPolicy
{
    private bool _warmupWindowIgnored;
    private int _consecutiveLowWindows;
    private bool _fallbackCommitted;

    public bool FallbackCommitted => _fallbackCommitted;

    public bool ShouldFallback(
        int targetFrameRate,
        double callbackFrameRate,
        bool foregroundAndVisible,
        bool encoderPressure,
        bool saveInProgress = false)
    {
        // A selected hook is kept for the lifetime of this game process.  It
        // avoids a source swap every time WGC has one good sample, which would
        // itself create the cadence oscillation this watchdog is meant to cure.
        if (_fallbackCommitted) return false;

        if (!foregroundAndVisible || encoderPressure || saveInProgress)
        {
            Reset();
            return false;
        }

        if (!_warmupWindowIgnored)
        {
            _warmupWindowIgnored = true;
            return false;
        }

        if (callbackFrameRate >= Math.Clamp(targetFrameRate, ReplayFrameTimingPolicy.MinimumFrameRate, ReplayFrameTimingPolicy.MaximumFrameRate) * 0.9)
        {
            _consecutiveLowWindows = 0;
            return false;
        }

        if (++_consecutiveLowWindows < 3) return false;
        _fallbackCommitted = true;
        return true;
    }

    public void Reset()
    {
        _warmupWindowIgnored = false;
        _consecutiveLowWindows = 0;
        _fallbackCommitted = false;
    }
}
