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
        var queueCapacity = health.EncodeQueueCapacity;
        var fatal = !health.SaveInProgress &&
                    health.State == ReplayCaptureState.Degraded &&
                    health.DegradeReason == ReplayDegradeReason.EncoderOverload &&
                    health.OutputFrameRate < 1 &&
                    queueCapacity > 0 &&
                    health.QueueDepth * 4 >= queueCapacity * 3;
        _consecutiveFatalSamples = fatal ? _consecutiveFatalSamples + 1 : 0;
        return _consecutiveFatalSamples >= RequiredSamples;
    }

    internal void Reset() => _consecutiveFatalSamples = 0;
}
