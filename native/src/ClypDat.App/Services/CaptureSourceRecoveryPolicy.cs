using ClypDat.Capture.Abstractions;

namespace ClypDat.App.Services;

internal enum CaptureSourceRecoveryAction { None, RecreateDxgi, SwitchToWgc, RestartWorker }

// One pipeline policy: a full encoder queue can suppress DXGI just as surely
// as a quiet source. Do not infer cause from a vendor encoder name.
internal sealed class CaptureSourceRecoveryPolicy
{
    private const int RequiredWindows = 2;
    private static readonly TimeSpan RepeatWindow = TimeSpan.FromSeconds(30);
    private int _consecutiveUnhealthy;
    private DateTime? _lastDxgiRecreateUtc;

    internal CaptureSourceRecoveryAction Observe(ReplayCaptureHealth health, bool foreground, DateTime nowUtc)
    {
        if (!foreground || health.CapturePaused || health.SaveInProgress)
        {
            _consecutiveUnhealthy = 0;
            return CaptureSourceRecoveryAction.None;
        }

        var pressured = health.EncodeQueueCapacity > 0 && health.QueueDepth * 4 >= health.EncodeQueueCapacity * 3;
        var congestion = health.OutputFrameRate < health.TargetFrameRate * 0.5 && pressured &&
                         (health.DroppedFrames > 0 || health.EncoderSubmissionStalled);
        var sourceStarvation = health.InputFrameRate <= 0 && health.UniqueFrameRate <= 0 &&
                               health.OutputFrameRate < 1 && !pressured;
        if (!congestion && !sourceStarvation)
        {
            _consecutiveUnhealthy = 0;
            return CaptureSourceRecoveryAction.None;
        }

        if (++_consecutiveUnhealthy < RequiredWindows) return CaptureSourceRecoveryAction.None;
        _consecutiveUnhealthy = 0;
        // Queue-backed DXGI collapse has already proved acquisition shares the
        // blocked pipeline: recreate cannot drain it, WGC can.
        if (congestion) return CaptureSourceRecoveryAction.SwitchToWgc;
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
