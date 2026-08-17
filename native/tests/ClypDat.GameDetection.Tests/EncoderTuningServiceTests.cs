using ClypDat.App.Services;
using ClypDat.Capture.Abstractions;
using Xunit;

namespace ClypDat.GameDetection.Tests;

public sealed class EncoderTuningServiceTests
{
    private static readonly TimeSpan SampleInterval = TimeSpan.FromSeconds(2);

    [Fact]
    public void Lowers120FpsToMeasured60FpsCapacity()
    {
        var service = new EncoderTuningService();
        var changes = Changes(service);
        service.BeginSession("P1", 120, 1440);

        Feed(service, 30, targetFrameRate: 120, outputFrameRate: 61, queueDepth: 80, queueCapacity: 120);

        var change = Assert.Single(changes);
        Assert.Equal(new EncoderFrameRateChange(120, 60), change);
    }

    [Fact]
    public void CanReduceAgainAfterCooldownWhenOverloadContinues()
    {
        var service = new EncoderTuningService();
        var changes = Changes(service);
        service.BeginSession("P4", 120, 1080);

        Feed(service, 30, 120, 61, 80, 120);
        Feed(service, 60, 120, 35, 80, 120);

        Assert.Equal(new[]
        {
            new EncoderFrameRateChange(120, 60),
            new EncoderFrameRateChange(60, 30)
        }, changes);
    }

    [Fact]
    public void RestoresConfiguredRateAfterCleanPeriod()
    {
        var service = new EncoderTuningService();
        var changes = Changes(service);
        service.BeginSession("P1", 120, 1440);

        Feed(service, 30, 120, 61, 80, 120);
        Feed(service, 310, 120, 120, 0, 120, ReplayDegradeReason.None, ReplayCaptureState.Healthy, startUtc: DateTime.UtcNow.AddMinutes(2));

        Assert.Equal(new[]
        {
            new EncoderFrameRateChange(120, 60),
            new EncoderFrameRateChange(60, 120)
        }, changes);
    }

    [Fact]
    public void DoesNotGoBelow30Fps()
    {
        var service = new EncoderTuningService();
        var changes = Changes(service);
        service.BeginSession("P1", 30, 1080);

        Feed(service, 300, 30, 10, 30, 30);

        Assert.Empty(changes);
    }

    [Fact]
    public void IgnoresCaptureStallAndSaveOverload()
    {
        var service = new EncoderTuningService();
        var changes = Changes(service);
        service.BeginSession("P1", 120, 1080);

        Feed(service, 30, 120, 10, 80, 120, ReplayDegradeReason.CaptureStall);
        Feed(service, 30, 120, 10, 80, 120, ReplayDegradeReason.EncoderOverload, ReplayCaptureState.Degraded, saveInProgress: true);

        Assert.Empty(changes);
    }

    [Fact]
    public void NeverRequestsResolutionChange()
    {
        var service = new EncoderTuningService();
        var changes = new List<EncoderResolutionChange>();
        service.ResolutionChangeRequested += (_, change) => changes.Add(change);
        service.BeginSession("P1", 120, 1080);

        Feed(service, 30, 120, 61, 80, 120);

        Assert.Empty(changes);
    }

    [Fact]
    public void DisabledAdaptiveFpsDoesNotLowerTarget()
    {
        var service = new EncoderTuningService();
        var changes = Changes(service);
        service.BeginSession("P1", 120, 1080, enabled: false);

        Feed(service, 30, 120, 61, 80, 120);

        Assert.Empty(changes);
    }

    [Fact]
    public void DisablingAfterReductionRestoresConfiguredTarget()
    {
        var service = new EncoderTuningService();
        var changes = Changes(service);
        service.BeginSession("P1", 120, 1080);

        Feed(service, 30, 120, 61, 80, 120);
        service.SetEnabled(false);

        Assert.Equal(new[]
        {
            new EncoderFrameRateChange(120, 60),
            new EncoderFrameRateChange(60, 120)
        }, changes);
    }

    private static List<EncoderFrameRateChange> Changes(EncoderTuningService service)
    {
        var changes = new List<EncoderFrameRateChange>();
        service.FrameRateChangeRequested += (_, change) => changes.Add(change);
        return changes;
    }

    private static void Feed(
        EncoderTuningService service,
        int samples,
        int targetFrameRate,
        double outputFrameRate,
        int queueDepth,
        int queueCapacity,
        ReplayDegradeReason reason = ReplayDegradeReason.EncoderOverload,
        ReplayCaptureState state = ReplayCaptureState.Degraded,
        bool saveInProgress = false,
        DateTime? startUtc = null)
    {
        var clock = startUtc ?? DateTime.UtcNow;
        for (var i = 0; i < samples; i++)
        {
            clock += SampleInterval;
            service.OnHealth(Health(clock, targetFrameRate, outputFrameRate, reason, state, queueDepth, queueCapacity, saveInProgress));
        }
    }

    private static ReplayCaptureHealth Health(
        DateTime updatedUtc,
        int targetFrameRate,
        double outputFrameRate,
        ReplayDegradeReason reason,
        ReplayCaptureState state,
        int queueDepth,
        int queueCapacity,
        bool saveInProgress)
    {
        return new ReplayCaptureHealth(
            "Hybrid",
            "Desktop Duplication",
            state,
            targetFrameRate,
            InputFrameRate: targetFrameRate,
            UniqueFrameRate: targetFrameRate,
            outputFrameRate,
            DuplicateFrames: 0,
            DroppedFrames: reason == ReplayDegradeReason.EncoderOverload ? 90 : 0,
            queueDepth,
            "h264_qsv",
            "Default adapter",
            string.Empty,
            updatedUtc)
        {
            EncodeQueueCapacity = queueCapacity,
            EncoderPreset = "P1",
            DegradeReason = reason,
            AdapterDescription = "Intel(R) Iris(R) Xe Graphics",
            SaveInProgress = saveInProgress
        };
    }
}
