namespace ClypDat.App.Services;

// Capture owns CPU, disk, and network latency during its cold start and active
// lifetime. Background library polish must yield until capture stops.
internal static class CaptureBackgroundWorkGate
{
    private static readonly object Sync = new();
    private static CancellationTokenSource _captureCancellation = new();
    private static bool _isCaptureActive;
    internal static event Action<bool>? StateChanged;

    internal static bool IsCaptureActive
    {
        get { lock (Sync) return _isCaptureActive; }
    }

    internal static CancellationToken CaptureCancellation
    {
        get { lock (Sync) return _captureCancellation.Token; }
    }

    internal static void BeginCapture()
    {
        lock (Sync)
        {
            if (_isCaptureActive) return;
            _isCaptureActive = true;
            _captureCancellation.Cancel();
            _captureCancellation.Dispose();
            _captureCancellation = new CancellationTokenSource();
        }
        AppLog.Debug("Capture background work paused.");
        StateChanged?.Invoke(true);
    }

    internal static void EndCapture()
    {
        lock (Sync)
        {
            if (!_isCaptureActive) return;
            _isCaptureActive = false;
            _captureCancellation.Cancel();
            _captureCancellation.Dispose();
            _captureCancellation = new CancellationTokenSource();
        }
        AppLog.Debug("Capture background work resumed.");
        StateChanged?.Invoke(false);
    }
}
