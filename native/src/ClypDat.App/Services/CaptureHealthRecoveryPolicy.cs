using ClypDat.Capture.Abstractions;

namespace ClypDat.App.Services;

// Health arrives every two seconds from the worker.  Do not turn one delayed
// sample into a restart, but never let a full encoder queue silently save a
// near-empty video stream either.
internal sealed class CaptureHealthRecoveryPolicy
{
    private const int RequiredSamples = 3;
    private int _consecutiveFatalSamples;

    internal bool Observe(ReplayCaptureHealth health)
    {
        // A D3D11 encoder rebind failed after device recovery. The worker no
        // longer owns valid frames for its encoder, so restart before another
        // capture tick can submit a stale native resource.
        if (health.PipelineRecoveryAction == ReplayPipelineRecoveryAction.RestartWorker &&
            health.LastFailure.StartsWith("D3D11 encoder could not rebind", StringComparison.Ordinal))
        {
            _consecutiveFatalSamples = RequiredSamples;
            return true;
        }

        var queueCapacity = health.EncodeQueueCapacity;
        var fatal = !health.SaveInProgress && !health.CapturePaused &&
                    (health.CaptureMode.Contains("WGC", StringComparison.OrdinalIgnoreCase) ||
                     health.CaptureMode.Contains("Graphics Capture", StringComparison.OrdinalIgnoreCase)) &&
                    health.OutputFrameRate < health.TargetFrameRate * 0.5 &&
                    queueCapacity > 0 &&
                    health.QueueDepth * 4 >= queueCapacity * 3 &&
                    (health.DroppedFrames > 0 || health.EncoderSubmissionStalled ||
                     health.PipelineRecoveryAction == ReplayPipelineRecoveryAction.RestartWorker);
        _consecutiveFatalSamples = fatal ? _consecutiveFatalSamples + 1 : 0;
        return _consecutiveFatalSamples >= RequiredSamples;
    }

    internal void Reset() => _consecutiveFatalSamples = 0;
}
