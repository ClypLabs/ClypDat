using ClypDat.App.Services;
using ClypDat.Capture.Abstractions;
using Xunit;

namespace ClypDat.GameDetection.Tests;

public sealed class EncoderTuningServiceTests
{
    private static readonly TimeSpan SampleInterval = TimeSpan.FromSeconds(2);

    [Fact]
    public void NeverLowersConfiguredFrameRateAtPresetFloor()
    {
        var service = new EncoderTuningService();
        var frameRateChanges = new List<EncoderFrameRateChange>();
        var resolutionChanges = new List<EncoderResolutionChange>();
        service.FrameRateChangeRequested += (_, change) => frameRateChanges.Add(change);
        service.ResolutionChangeRequested += (_, change) => resolutionChanges.Add(change);
        service.BeginSession("P1", 60, 1080);

        Feed(service, severeSamples: 300, frameRate: 60, queueDepth: 30);

        Assert.Empty(frameRateChanges);
        Assert.Empty(resolutionChanges);
    }

    [Fact]
    public void NeverRequestsResolutionChange()
    {
        var service = new EncoderTuningService();
        var resolutionChanges = new List<EncoderResolutionChange>();
        service.ResolutionChangeRequested += (_, change) => resolutionChanges.Add(change);
        service.BeginSession("P1", 60, 1080);

        Feed(service, severeSamples: 300, frameRate: 60, queueDepth: 30);

        Assert.Empty(resolutionChanges);
    }

    [Fact]
    public void LeavesFasterPresetAsAnObservationOnlyProposal()
    {
        var service = new EncoderTuningService();
        var changes = new List<EncoderFrameRateChange>();
        service.FrameRateChangeRequested += (_, change) => changes.Add(change);
        service.BeginSession("P4", 60, 1080);

        Feed(service, severeSamples: 60, frameRate: 60, queueDepth: 30);

        Assert.Empty(changes);
    }

    [Fact]
    public void LeavesHealthySessionAtItsConfiguredQuality()
    {
        var service = new EncoderTuningService();
        var frameRateChanges = new List<EncoderFrameRateChange>();
        var resolutionChanges = new List<EncoderResolutionChange>();
        service.FrameRateChangeRequested += (_, change) => frameRateChanges.Add(change);
        service.ResolutionChangeRequested += (_, change) => resolutionChanges.Add(change);
        service.BeginSession("P1", 60, 1080);

        Feed(service, severeSamples: 0, frameRate: 60, healthySamples: 400, queueDepth: 0);

        Assert.Empty(frameRateChanges);
        Assert.Empty(resolutionChanges);
    }

    [Fact]
    public void DoesNotHalveAlreadyLowConfiguredRate()
    {
        var service = new EncoderTuningService();
        var changes = new List<EncoderFrameRateChange>();
        service.FrameRateChangeRequested += (_, change) => changes.Add(change);
        service.BeginSession("P1", 30, 1080);

        Feed(service, severeSamples: 200, frameRate: 30, queueDepth: 30);

        Assert.Empty(changes);
    }

    [Fact]
    public void IgnoresCaptureStallForEncoderTuning()
    {
        var service = new EncoderTuningService();
        var frameRateChanges = new List<EncoderFrameRateChange>();
        var resolutionChanges = new List<EncoderResolutionChange>();
        service.FrameRateChangeRequested += (_, change) => frameRateChanges.Add(change);
        service.ResolutionChangeRequested += (_, change) => resolutionChanges.Add(change);
        service.BeginSession("P1", 60, 1080);

        Feed(service, severeSamples: 200, frameRate: 60, queueDepth: 0, reason: ReplayDegradeReason.CaptureStall);

        Assert.Empty(frameRateChanges);
        Assert.Empty(resolutionChanges);
    }

    private static void Feed(
        EncoderTuningService service,
        int severeSamples,
        int frameRate,
        int healthySamples = 0,
        int queueDepth = 0,
        ReplayDegradeReason reason = ReplayDegradeReason.EncoderOverload)
    {
        var clock = DateTime.UtcNow;
        for (var i = 0; i < severeSamples + healthySamples; i++)
        {
            clock += SampleInterval;
            var severe = i < severeSamples;
            service.OnHealth(Health(
                clock,
                frameRate,
                severe ? frameRate * 0.3 : frameRate,
                severe ? reason : ReplayDegradeReason.None,
                queueDepth));
        }
    }

    private static ReplayCaptureHealth Health(
        DateTime updatedUtc,
        int targetFrameRate,
        double outputFrameRate,
        ReplayDegradeReason reason,
        int queueDepth)
    {
        return new ReplayCaptureHealth(
            "Hybrid",
            "Desktop Duplication",
            reason == ReplayDegradeReason.None ? ReplayCaptureState.Healthy : ReplayCaptureState.Degraded,
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
            EncodeQueueCapacity = 30,
            EncoderPreset = "P1",
            DegradeReason = reason,
            AdapterDescription = "Intel(R) Iris(R) Xe Graphics"
        };
    }
}
