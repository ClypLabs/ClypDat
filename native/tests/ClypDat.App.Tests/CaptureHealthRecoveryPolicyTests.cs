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

    [Fact]
    public void SourceStarvation_RecreatesThenFallsBackToWgc()
    {
        var policy = new CaptureSourceRecoveryPolicy();
        var sample = ReplayCaptureHealth.Unknown("Native") with
        {
            State = ReplayCaptureState.Degraded, InputFrameRate = 0, UniqueFrameRate = 0,
            OutputFrameRate = 0, QueueDepth = 9, EncodeQueueCapacity = 12
        };
        var now = DateTime.UtcNow;
        Assert.Equal(CaptureSourceRecoveryAction.None, policy.Observe(sample, true, now));
        Assert.Equal(CaptureSourceRecoveryAction.RecreateDxgi, policy.Observe(sample, true, now));
        Assert.Equal(CaptureSourceRecoveryAction.None, policy.Observe(sample, true, now));
        Assert.Equal(CaptureSourceRecoveryAction.SwitchToWgc, policy.Observe(sample, true, now));
    }

    [Fact]
    public void SourceRecovery_IgnoresPausedOrBackground_AndHealthyResets()
    {
        var policy = new CaptureSourceRecoveryPolicy();
        var starving = ReplayCaptureHealth.Unknown() with { OutputFrameRate = 0, QueueDepth = 12, EncodeQueueCapacity = 12 };
        Assert.Equal(CaptureSourceRecoveryAction.None, policy.Observe(starving with { CapturePaused = true }, true, DateTime.UtcNow));
        Assert.Equal(CaptureSourceRecoveryAction.None, policy.Observe(starving, false, DateTime.UtcNow));
        Assert.Equal(CaptureSourceRecoveryAction.None, policy.Observe(starving, true, DateTime.UtcNow));
        Assert.Equal(CaptureSourceRecoveryAction.None, policy.Observe(starving with { InputFrameRate = 90, UniqueFrameRate = 90 }, true, DateTime.UtcNow));
        Assert.Equal(CaptureSourceRecoveryAction.None, policy.Observe(starving, true, DateTime.UtcNow));
    }

    [Fact]
    public void StartupQualification_RejectsCfrDuplicatesAndInactiveWindows()
    {
        Assert.False(ReplayStartupQualificationPolicy.Passes(true, false, true, 90, 90, 0, 0, 0, 12));
        Assert.False(ReplayStartupQualificationPolicy.Passes(false, false, true, 90, 90, 90, 0, 0, 12));
        Assert.False(ReplayStartupQualificationPolicy.Passes(true, true, true, 90, 90, 90, 0, 0, 12));
        Assert.True(ReplayStartupQualificationPolicy.Passes(true, false, true, 90, 90, 90, 0, 0, 12));
    }
}
