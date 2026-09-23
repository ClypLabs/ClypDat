using ClypDat.Capture.Abstractions;

namespace ClypDat.App.Services;

// Native startup polls every 25 ms and running health can arrive every 250 ms.
// Count distinct two-second windows, not messages, and let startup finish before
// judging throughput. Explicit native failures still request immediate recovery.
internal sealed class CaptureHealthRecoveryPolicy
{
    private const int RequiredSamples = 3;
    private static readonly TimeSpan SampleInterval = TimeSpan.FromSeconds(2);
    private int _consecutiveFatalSamples;
    private DateTime? _lastFatalSampleUtc;

    internal bool Observe(ReplayCaptureHealth health)
    {
        // A D3D11 encoder rebind failed after device recovery. The worker no
        // longer owns valid frames for its encoder, so restart before another
        // capture tick can submit a stale native resource.
        if (health.PipelineRecoveryAction == ReplayPipelineRecoveryAction.RestartWorker &&
            (health.State == ReplayCaptureState.Failed ||
             health.LastFailure.StartsWith("D3D11 encoder could not rebind", StringComparison.Ordinal)))
        {
            _consecutiveFatalSamples = RequiredSamples;
            return true;
        }

        var queueCapacity = health.EncodeQueueCapacity;
        var fatal = (health.State is ReplayCaptureState.Healthy or ReplayCaptureState.Degraded) &&
                    !health.SaveInProgress && !health.CapturePaused &&
                    (health.CaptureMode.Contains("WGC", StringComparison.OrdinalIgnoreCase) ||
                     health.CaptureMode.Contains("Graphics Capture", StringComparison.OrdinalIgnoreCase)) &&
                    health.OutputFrameRate < health.TargetFrameRate * 0.5 &&
                    queueCapacity > 0 &&
                    health.QueueDepth * 4 >= queueCapacity * 3 &&
                    (health.DroppedFrames > 0 || health.EncoderSubmissionStalled ||
                     health.PipelineRecoveryAction == ReplayPipelineRecoveryAction.RestartWorker);
        if (!fatal)
        {
            Reset();
            return false;
        }
        if (_lastFatalSampleUtc is { } previous && health.UpdatedUtc - previous < SampleInterval)
            return false;
        _lastFatalSampleUtc = health.UpdatedUtc;
        _consecutiveFatalSamples++;
        return _consecutiveFatalSamples >= RequiredSamples;
    }

    internal void Reset()
    {
        _consecutiveFatalSamples = 0;
        _lastFatalSampleUtc = null;
    }
}
