using ClypDat.Capture.Abstractions;

namespace ClypDat.App.Services;

internal enum CaptureSourceRecoveryAction { None, RecreateDxgi, SwitchToWgc, RestartWorker }

// Distinguishes acquisition failure from encoder congestion using ownership
// telemetry, never an encoder vendor name. CFR output can remain exactly at
// target while it pads a starved source, so output rate is not source health.
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

        var pressured = health.EncodeQueueCapacity > 0 &&
                        health.QueueDepth * 4 >= health.EncodeQueueCapacity * 3;
        var encoderCongestion = health.EncoderSubmissionStalled ||
                                (pressured && health.DroppedFrames > 0);
        if (encoderCongestion)
        {
            _consecutiveUnhealthy = 0;
            return CaptureSourceRecoveryAction.None;
        }

        var sharedSurfacesExhausted = health.SurfaceCapacity > 0 &&
                                      health.SurfacesInUse >= health.SurfaceCapacity &&
                                      health.TransportAllBusyDrops > 0 &&
                                      health.TransportReleaseLagFrames >= health.SurfaceCapacity;
        var sourceSilent = health.InputFrameRate <= 0 && health.UniqueFrameRate <= 0 &&
                           (health.OutputFrameRate < 1 || health.DegradeReason == ReplayDegradeReason.CaptureStall);
        if (!sharedSurfacesExhausted && !sourceSilent)
        {
            _consecutiveUnhealthy = 0;
            return CaptureSourceRecoveryAction.None;
        }

        if (++_consecutiveUnhealthy < RequiredWindows) return CaptureSourceRecoveryAction.None;
        _consecutiveUnhealthy = 0;
        // Every shared slot waiting on a release fence proves recreating the
        // same transport cannot remove its ownership cycle. WGC owns callback
        // textures independently, so switch directly.
        if (sharedSurfacesExhausted) return CaptureSourceRecoveryAction.SwitchToWgc;
        if (_lastDxgiRecreateUtc is { } previous && nowUtc - previous <= RepeatWindow)
            return CaptureSourceRecoveryAction.SwitchToWgc;
        _lastDxgiRecreateUtc = nowUtc;
        return CaptureSourceRecoveryAction.RecreateDxgi;
    }
}

internal static class ReplayStartupQualificationPolicy
{
    internal enum WindowDisposition { WaitingForFirstPacket, Prime, Pass, Fail }

    internal static WindowDisposition ClassifyWindow(bool primed, long packetsOut, bool passes) =>
        !primed
            ? packetsOut > 0 ? WindowDisposition.Prime : WindowDisposition.WaitingForFirstPacket
            : passes ? WindowDisposition.Pass : WindowDisposition.Fail;

    internal static bool Passes(bool foreground, bool paused, bool hasRealFrame, int targetFrameRate,
        double outputFrameRate, double freshVisualFrameRate, long droppedFrames, int queueDepth, int queueCapacity)
        => foreground && !paused && hasRealFrame && droppedFrames == 0 && queueCapacity > 0 &&
           queueDepth * 4 < queueCapacity * 3 &&
           outputFrameRate >= targetFrameRate * ReplayEncoderQualificationPolicy.TargetThreshold &&
           freshVisualFrameRate >= targetFrameRate * ReplayEncoderQualificationPolicy.TargetThreshold;
}
