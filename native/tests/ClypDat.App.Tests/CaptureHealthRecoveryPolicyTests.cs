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
            State = ReplayCaptureState.Degraded, CaptureMode = "Windows Graphics Capture (recovery)",
            OutputFrameRate = 0, TargetFrameRate = 90,
            QueueDepth = 12,
            EncodeQueueCapacity = 12,
            EncoderSubmissionStalled = true,
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
            State = ReplayCaptureState.Degraded, CaptureMode = "WGC",
            OutputFrameRate = 0, TargetFrameRate = 90,
            QueueDepth = 9,
            EncodeQueueCapacity = 12, EncoderSubmissionStalled = true
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
            State = ReplayCaptureState.Degraded, TargetFrameRate = 90, InputFrameRate = 0, UniqueFrameRate = 0,
            OutputFrameRate = 0, QueueDepth = 0, EncodeQueueCapacity = 12
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
        var starving = ReplayCaptureHealth.Unknown() with { OutputFrameRate = 0, QueueDepth = 0, EncodeQueueCapacity = 12 };
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

    [Fact]
    public void DxgiCongestion_SwitchesToWgcAfterTwoWindows_RegardlessOfEncoder()
    {
        var policy = new CaptureSourceRecoveryPolicy();
        var sample = ReplayCaptureHealth.Unknown("Native") with
        {
            TargetFrameRate = 90, OutputFrameRate = 8, QueueDepth = 12, EncodeQueueCapacity = 12,
            DroppedFrames = 1, Encoder = "AMF"
        };
        var now = DateTime.UtcNow;
        Assert.Equal(CaptureSourceRecoveryAction.None, policy.Observe(sample, true, now));
        Assert.Equal(CaptureSourceRecoveryAction.SwitchToWgc, policy.Observe(sample, true, now));
    }
}
