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
        Assert.False(policy.Observe(fatal with { UpdatedUtc = fatal.UpdatedUtc.AddSeconds(2) }));
        Assert.True(policy.Observe(fatal with { UpdatedUtc = fatal.UpdatedUtc.AddSeconds(4) }));
    }

    [Theory]
    [InlineData(ReplayCaptureState.Starting)]
    [InlineData(ReplayCaptureState.Stopped)]
    [InlineData(ReplayCaptureState.Stopping)]
    [InlineData(ReplayCaptureState.Recovering)]
    [InlineData(ReplayCaptureState.Unknown)]
    public void NonRecordingHealth_DoesNotConsumeRecovery(ReplayCaptureState state)
    {
        var policy = new CaptureHealthRecoveryPolicy();
        var sample = CongestedNativeHealth() with { State = state };

        // Startup publishes every 25 ms while NVENC fills its initial surfaces.
        // Replay the reported full-queue/zero-output sample until frames arrive.
        for (var i = 0; i < 240; i++)
            Assert.False(policy.Observe(sample with { UpdatedUtc = sample.UpdatedUtc.AddMilliseconds(i * 25) }));
        Assert.False(policy.Observe(sample with
        {
            State = ReplayCaptureState.Healthy, OutputFrameRate = 90, QueueDepth = 0,
            UpdatedUtc = sample.UpdatedUtc.AddSeconds(6)
        }));
    }

    [Fact]
    public void RapidNativeHealth_RequiresSpacedWindows()
    {
        var policy = new CaptureHealthRecoveryPolicy();
        var sample = CongestedNativeHealth();

        for (var i = 0; i < 160; i++)
            Assert.False(policy.Observe(sample with { UpdatedUtc = sample.UpdatedUtc.AddMilliseconds(i * 25) }));
        Assert.True(policy.Observe(sample with { UpdatedUtc = sample.UpdatedUtc.AddSeconds(4) }));
    }

    [Fact]
    public void DuplicateOrOlderHealth_DoesNotAdvanceWindows()
    {
        var policy = new CaptureHealthRecoveryPolicy();
        var sample = CongestedNativeHealth();

        Assert.False(policy.Observe(sample));
        for (var i = 0; i < 10; i++)
        {
            Assert.False(policy.Observe(sample));
            Assert.False(policy.Observe(sample with { UpdatedUtc = sample.UpdatedUtc.AddSeconds(-1) }));
        }
        Assert.False(policy.Observe(sample with { UpdatedUtc = sample.UpdatedUtc.AddSeconds(2) }));
        Assert.True(policy.Observe(sample with { UpdatedUtc = sample.UpdatedUtc.AddSeconds(4) }));
    }

    [Fact]
    public void Reset_StartsNewWindowSequence()
    {
        var policy = new CaptureHealthRecoveryPolicy();
        var sample = CongestedNativeHealth();
        Assert.False(policy.Observe(sample));
        Assert.False(policy.Observe(sample with { UpdatedUtc = sample.UpdatedUtc.AddSeconds(2) }));
        policy.Reset();
        Assert.False(policy.Observe(sample));
        Assert.False(policy.Observe(sample with { UpdatedUtc = sample.UpdatedUtc.AddSeconds(2) }));
        Assert.True(policy.Observe(sample with { UpdatedUtc = sample.UpdatedUtc.AddSeconds(4) }));
    }

    private static ReplayCaptureHealth CongestedNativeHealth() => ReplayCaptureHealth.Unknown("Native C++") with
    {
        State = ReplayCaptureState.Healthy, CaptureMode = "Windows Graphics Capture",
        Encoder = "h264_nvenc", TargetFrameRate = 90, OutputFrameRate = 0,
        QueueDepth = 12, EncodeQueueCapacity = 12, DroppedFrames = 3,
        UpdatedUtc = new DateTime(2026, 9, 23, 16, 46, 15, DateTimeKind.Utc)
    };

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
    public void FailedWorkerRestartRequest_IsImmediateForEveryCaptureSource()
    {
        var health = ReplayCaptureHealth.Unknown("Worker") with
        {
            State = ReplayCaptureState.Failed,
            CaptureMode = "DXGI Desktop Duplication",
            PipelineRecoveryAction = ReplayPipelineRecoveryAction.RestartWorker,
            LastFailure = "Capture worker shutdown timed out; preserving live native resources."
        };

        Assert.True(new CaptureHealthRecoveryPolicy().Observe(health));
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
    public void SharedSurfaceExhaustionHiddenByCfr_SwitchesToWgcAfterTwoWindows()
    {
        var policy = new CaptureSourceRecoveryPolicy();
        var sample = ReplayCaptureHealth.Unknown("Native") with
        {
            TargetFrameRate = 60,
            InputFrameRate = 25.28,
            UniqueFrameRate = 25.28,
            OutputFrameRate = 60,
            QueueDepth = 0,
            EncodeQueueCapacity = 8,
            SurfacesInUse = 6,
            SurfaceCapacity = 6,
            TransportBusySlotSkips = 330,
            TransportAllBusyDrops = 55,
            TransportReleaseLagFrames = 6
        };
        var now = DateTime.UtcNow;
        Assert.Equal(CaptureSourceRecoveryAction.None, policy.Observe(sample, true, now));
        Assert.Equal(CaptureSourceRecoveryAction.SwitchToWgc, policy.Observe(sample, true, now));
    }

    [Fact]
    public void EncoderCongestion_IsNotMisclassifiedAsSourceFailure()
    {
        var policy = new CaptureSourceRecoveryPolicy();
        var sample = ReplayCaptureHealth.Unknown("Native") with
        {
            TargetFrameRate = 60,
            InputFrameRate = 60,
            UniqueFrameRate = 60,
            OutputFrameRate = 20,
            QueueDepth = 8,
            EncodeQueueCapacity = 8,
            DroppedFrames = 40,
            EncoderSubmissionStalled = true
        };

        Assert.Equal(CaptureSourceRecoveryAction.None, policy.Observe(sample, true, DateTime.UtcNow));
        Assert.Equal(CaptureSourceRecoveryAction.None, policy.Observe(sample, true, DateTime.UtcNow));
    }
}
