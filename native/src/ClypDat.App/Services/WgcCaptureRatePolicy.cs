namespace ClypDat.App.Services;

internal readonly record struct WgcMinimumUpdateIntervalResult(
    bool InterfaceAvailable,
    TimeSpan Requested,
    TimeSpan? Applied,
    string? Failure = null);

internal static class WgcMinimumUpdateIntervalPolicy
{
    // WGC does not deliver a frame the instant the requested interval elapses:
    // frames only leave the frame pool on a composition tick, so whatever is
    // requested is rounded UP to a whole number of refresh periods. That
    // rounded-up value - see DeliveryFloor - is the real minimum spacing
    // between captured frames, and it is a ceiling on the source rate.
    //
    // The ceiling must therefore stay clear of the rate the game actually
    // presents at, not merely clear of the capture target. Asking for the
    // largest grid that fits inside the target frame period put it exactly
    // there: at 240Hz a 90 FPS target asked for 6.25ms, which rounds up to two
    // ticks, so no frame could arrive sooner than 8.333ms - a 120 FPS ceiling,
    // landing on the present rate of a 120 FPS game. Every present that ran a
    // fraction early then waited a whole extra tick (12.5ms, 80 FPS pace), and
    // the source read 100-110 FPS instead of 120. Measured over 2224
    // diagnostic windows, the average source gap never once fell below 8.23ms,
    // and the median window had 61% of its presents pushed a tick late.
    //
    // So pick the COARSEST grid that still leaves the ceiling comfortably above
    // the target. The interval is only here to stop an uncapped source doing
    // hundreds of full-resolution copies a second for a 30 or 60 FPS
    // recording; it is not meant to pace anything near the target itself.
    private const double SourceHeadroom = 1.5;
    private const double QuantizationTolerance = 1e-6;

    public static TimeSpan FromFrameRate(int frameRate, double displayRefreshHz = 0)
    {
        var target = Math.Clamp(frameRate, ReplayFrameTimingPolicy.MinimumFrameRate, ReplayFrameTimingPolicy.MaximumFrameRate);
        var targetPeriodSeconds = 1d / target;
        // Unknown refresh rate: ask for half the target period. With no grid to
        // reason about, under-asking is the safe direction - whatever this
        // rounds up to on the display's real grid still cannot delay a source
        // running at the target rate.
        if (!double.IsFinite(displayRefreshHz) || displayRefreshHz <= 0) return TimeSpan.FromSeconds(targetPeriodSeconds / 2);

        var refreshPeriodSeconds = 1d / displayRefreshHz;
        // A display that cannot outrun the target by the headroom factor gets
        // the finest grid there is: one refresh period, i.e. no throttle at all.
        var periodsPerFrame = Math.Max(1, (int)Math.Floor(displayRefreshHz / (target * SourceHeadroom) + QuantizationTolerance));
        return TimeSpan.FromSeconds(refreshPeriodSeconds * (periodsPerFrame - 0.5));
    }

    /// <summary>
    /// The spacing WGC will actually enforce for a requested interval: the
    /// request rounded up to a whole number of composition ticks. This, not the
    /// requested or the echoed-back applied value, is what caps the source rate.
    /// </summary>
    public static TimeSpan DeliveryFloor(TimeSpan requested, double displayRefreshHz)
    {
        if (!double.IsFinite(displayRefreshHz) || displayRefreshHz <= 0 || requested <= TimeSpan.Zero) return requested;

        var refreshPeriodSeconds = 1d / displayRefreshHz;
        var ticks = Math.Max(1, (int)Math.Ceiling(requested.TotalSeconds / refreshPeriodSeconds - QuantizationTolerance));
        return TimeSpan.FromSeconds(refreshPeriodSeconds * ticks);
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
