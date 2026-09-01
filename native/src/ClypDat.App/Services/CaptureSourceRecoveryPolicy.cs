using ClypDat.Capture.Abstractions;

namespace ClypDat.App.Services;

internal enum CaptureSourceRecoveryAction { None, RecreateDxgi, SwitchToWgc }

// Source starvation is distinct from an overloaded encoder: CFR can keep
// outputting duplicates while no fresh pixels cross DXGI. Keep this pure so
// captured failure traces remain deterministic tests.
internal sealed class CaptureSourceRecoveryPolicy
{
    private const int RequiredWindows = 2;
    private static readonly TimeSpan RepeatWindow = TimeSpan.FromSeconds(30);
    private int _consecutiveStarvation;
    private DateTime? _lastDxgiRecreateUtc;

    internal CaptureSourceRecoveryAction Observe(ReplayCaptureHealth health, bool foreground, DateTime nowUtc)
    {
        if (!foreground || health.CapturePaused || health.SaveInProgress)
        {
            _consecutiveStarvation = 0;
            return CaptureSourceRecoveryAction.None;
        }

        var starving = health.InputFrameRate <= 0 && health.UniqueFrameRate <= 0 &&
                       health.OutputFrameRate < 1 && health.EncodeQueueCapacity > 0 &&
                       health.QueueDepth * 4 >= health.EncodeQueueCapacity * 3;
        if (!starving)
        {
            _consecutiveStarvation = 0;
            return CaptureSourceRecoveryAction.None;
        }

        if (++_consecutiveStarvation < RequiredWindows) return CaptureSourceRecoveryAction.None;
        _consecutiveStarvation = 0;
        if (_lastDxgiRecreateUtc is { } previous && nowUtc - previous <= RepeatWindow)
            return CaptureSourceRecoveryAction.SwitchToWgc;
        _lastDxgiRecreateUtc = nowUtc;
        return CaptureSourceRecoveryAction.RecreateDxgi;
    }
}

internal static class ReplayStartupQualificationPolicy
{
    internal static bool Passes(bool foreground, bool paused, bool hasRealFrame, int targetFrameRate,
        double outputFrameRate, double freshVisualFrameRate, long droppedFrames, int queueDepth, int queueCapacity)
        => foreground && !paused && hasRealFrame && droppedFrames == 0 && queueCapacity > 0 &&
           queueDepth * 4 < queueCapacity * 3 &&
           outputFrameRate >= targetFrameRate * ReplayEncoderQualificationPolicy.TargetThreshold &&
           freshVisualFrameRate >= targetFrameRate * ReplayEncoderQualificationPolicy.TargetThreshold;
}
