namespace ClypDat.App.Services;

internal readonly record struct WgcMinimumUpdateIntervalResult(
    bool InterfaceAvailable,
    TimeSpan Requested,
    TimeSpan? Applied,
    string? Failure = null);

internal static class WgcMinimumUpdateIntervalPolicy
{
    public static TimeSpan FromFrameRate(int frameRate) =>
        TimeSpan.FromSeconds(1d / Math.Clamp(frameRate, 30, 144));

    public static WgcMinimumUpdateIntervalResult Unsupported(int frameRate) =>
        new(false, FromFrameRate(frameRate), null);
}

// WGC can intentionally slow its producer while a window is backgrounded. Only
// foreground, encoder-healthy diagnostic windows can prove WGC is the limiter.
internal sealed class WgcCadenceFallbackPolicy
{
    private bool _warmupWindowIgnored;
    private int _consecutiveLowWindows;

    public bool ShouldFallback(int targetFrameRate, double callbackFrameRate, bool foregroundAndVisible, bool encoderPressure)
    {
        if (!foregroundAndVisible || encoderPressure)
        {
            Reset();
            return false;
        }

        if (!_warmupWindowIgnored)
        {
            _warmupWindowIgnored = true;
            return false;
        }

        if (callbackFrameRate >= Math.Clamp(targetFrameRate, 30, 144) * 0.9)
        {
            _consecutiveLowWindows = 0;
            return false;
        }

        return ++_consecutiveLowWindows >= 3;
    }

    public void Reset()
    {
        _warmupWindowIgnored = false;
        _consecutiveLowWindows = 0;
    }
}
