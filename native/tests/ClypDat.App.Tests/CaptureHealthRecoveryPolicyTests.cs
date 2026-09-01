using ClypDat.App.Services;
using ClypDat.Capture.Abstractions;
using Xunit;

namespace ClypDat.App.Tests;

public sealed class CaptureHealthRecoveryPolicyTests
{
    [Fact]
    public void ThreeFatalTwoSecondWindows_RequestRecovery()
    {
        var policy = new CaptureHealthRecoveryPolicy();
        var fatal = ReplayCaptureHealth.Unknown("Worker") with
        {
            State = ReplayCaptureState.Degraded,
            DegradeReason = ReplayDegradeReason.EncoderOverload,
            OutputFrameRate = 0,
            QueueDepth = 12,
            EncodeQueueCapacity = 12,
            SaveInProgress = false
        };

        Assert.False(policy.Observe(fatal));
        Assert.False(policy.Observe(fatal));
        Assert.True(policy.Observe(fatal));
    }

    [Fact]
    public void SaveOrQueueRecovery_ClearsFatalSequence()
    {
        var policy = new CaptureHealthRecoveryPolicy();
        var fatal = ReplayCaptureHealth.Unknown("Worker") with
        {
            State = ReplayCaptureState.Degraded,
            DegradeReason = ReplayDegradeReason.EncoderOverload,
            OutputFrameRate = 0,
            QueueDepth = 9,
            EncodeQueueCapacity = 12
        };

        Assert.False(policy.Observe(fatal));
        Assert.False(policy.Observe(fatal with { SaveInProgress = true }));
        Assert.False(policy.Observe(fatal));
    }
}
