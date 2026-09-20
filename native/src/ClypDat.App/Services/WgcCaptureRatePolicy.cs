namespace ClypDat.App.Services;

internal readonly record struct WgcMinimumUpdateIntervalResult(
    bool InterfaceAvailable,
    TimeSpan Requested,
    TimeSpan? Applied,
    string? Failure = null);

internal static class WgcMinimumUpdateIntervalPolicy
{
    // WGC does not deliver a frame the instant the requested interval elapses:
    // frames only leave the frame pool on a composition tick, so the interval
    // is effectively rounded UP to a whole number of refresh periods. Asking
    // for the exact target frame period is therefore the worst thing to ask
    // for whenever the display refreshes faster than the capture target and is
    // not an exact multiple of it: at 240Hz (4.167ms) a requested 11.111ms
    // (90 FPS) lands between two and three refreshes, so every frame waits for
    // the third - 12.5ms, 80 FPS, and the 90 FPS target can never be met.
    //
    // So request the LARGEST whole number of refresh periods that still fits
    // inside the target frame period, minus half a period of jitter margin.
    // At 240Hz/90 FPS that is 6.25ms: frames arrive every second refresh
    // (120 FPS) and the pacing gate downsamples to the 90 FPS target, which is
    // what DXGI Desktop Duplication was already doing.
    private const double QuantizationTolerance = 1e-6;

    public static TimeSpan FromFrameRate(int frameRate, double displayRefreshHz = 0)
    {
        var targetPeriodSeconds = 1d / Math.Clamp(frameRate, ReplayFrameTimingPolicy.MinimumFrameRate, ReplayFrameTimingPolicy.MaximumFrameRate);
        // Unknown refresh rate: ask for the target period, which is what this
        // did before the refresh rate was available. Over-delivery is the only
        // safe direction to be wrong in, but so is not guessing a grid.
        if (!double.IsFinite(displayRefreshHz) || displayRefreshHz <= 0) return TimeSpan.FromSeconds(targetPeriodSeconds);

        var refreshPeriodSeconds = 1d / displayRefreshHz;
        // A display slower than the capture target cannot feed it either way;
        // one refresh period is then the finest grid there is.
        var periodsPerFrame = Math.Max(1, (int)Math.Floor(targetPeriodSeconds / refreshPeriodSeconds + QuantizationTolerance));
        return TimeSpan.FromSeconds(refreshPeriodSeconds * (periodsPerFrame - 0.5));
    }

    public static WgcMinimumUpdateIntervalResult Unsupported(int frameRate, double displayRefreshHz = 0) =>
        Unavailable(frameRate, displayRefreshHz);

    public static WgcMinimumUpdateIntervalResult Unavailable(int frameRate, double displayRefreshHz = 0, string? failure = null) =>
        new(false, FromFrameRate(frameRate, displayRefreshHz), null, failure);
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

        if (callbackFrameRate >= Math.Clamp(targetFrameRate, ReplayFrameTimingPolicy.MinimumFrameRate, ReplayFrameTimingPolicy.MaximumFrameRate) * 0.99)
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

// DXGI Desktop Duplication can keep reporting a healthy acquisition rate while
// its cropped output stops advancing for an uncapped game. Once the encoder is
// healthy and the target is foreground, repeated low fresh-frame windows prove
// this is a source failure, not an encode failure. WGC captures the window
// directly and avoids DWM's desktop-composition cadence.
internal sealed class DxgiCadenceFallbackPolicy
{
    private const double MinimumFreshFrameRateRatio = 0.5;
    private bool _warmupWindowIgnored;
    private int _consecutiveLowWindows;
    private bool _fallbackCommitted;

    public bool ShouldFallback(
        int targetFrameRate,
        double freshFrameRate,
        bool foregroundAndVisible,
        bool encoderPressure,
        bool saveInProgress = false)
    {
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

        var target = Math.Clamp(targetFrameRate, ReplayFrameTimingPolicy.MinimumFrameRate, ReplayFrameTimingPolicy.MaximumFrameRate);
        if (freshFrameRate >= target * MinimumFreshFrameRateRatio)
        {
            _consecutiveLowWindows = 0;
            return false;
        }

        return ++_consecutiveLowWindows >= 3;
    }

    public void MarkFallbackCommitted() => _fallbackCommitted = true;

    private void Reset()
    {
        _warmupWindowIgnored = false;
        _consecutiveLowWindows = 0;
        _fallbackCommitted = false;
    }
}
