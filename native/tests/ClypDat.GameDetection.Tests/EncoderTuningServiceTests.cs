using ClypDat.App.Services;
using ClypDat.Capture.Abstractions;
using Xunit;

namespace ClypDat.GameDetection.Tests;

public sealed class EncoderTuningServiceTests
{
    // Health arrives roughly every 2s; the service ignores the first 30s as
    // warm-up and needs 8 severe windows out of a 15-sample window, with a 60s
    // cooldown between decisions.
    private static readonly TimeSpan SampleInterval = TimeSpan.FromSeconds(2);
    private static readonly DateTime SessionStart = new(2026, 8, 4, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void LowersFrameRateOnceTheresNoFasterPresetLeft()
    {
        var service = new EncoderTuningService();
        var changes = new List<EncoderFrameRateChange>();
        service.FrameRateChangeRequested += (_, change) => changes.Add(change);
        service.BeginSession("P1", 60);

        Feed(service, severeSamples: 30, frameRate: 60);

        var change = Assert.Single(changes);
        Assert.Equal(60, change.PreviousFrameRate);
        Assert.Equal(30, change.FrameRate);
    }

    [Fact]
    public void OnlyLowersTheFrameRateOncePerSession()
    {
        var service = new EncoderTuningService();
        var changes = new List<EncoderFrameRateChange>();
        service.FrameRateChangeRequested += (_, change) => changes.Add(change);
        service.BeginSession("P1", 60);

        // Keep drowning long after the step down. There is nowhere left to go,
        // so it must not ratchet toward an unwatchable frame rate.
        Feed(service, severeSamples: 400, frameRate: 60);

        Assert.Single(changes);
    }

    [Fact]
    public void LeavesTheFrameRateAloneWhenAFasterPresetIsStillAvailable()
    {
        var service = new EncoderTuningService();
        var changes = new List<EncoderFrameRateChange>();
        service.FrameRateChangeRequested += (_, change) => changes.Add(change);
        // P4 has P3/P2/P1 below it - spend those (as observe-only proposals)
        // before touching something the user can see.
        service.BeginSession("P4", 60);

        Feed(service, severeSamples: 60, frameRate: 60);

        Assert.Empty(changes);
    }

    [Fact]
    public void LeavesAHealthySessionAtItsConfiguredFrameRate()
    {
        var service = new EncoderTuningService();
        var changes = new List<EncoderFrameRateChange>();
        service.FrameRateChangeRequested += (_, change) => changes.Add(change);
        service.BeginSession("P1", 60);

        Feed(service, severeSamples: 0, frameRate: 60, healthySamples: 400);

        Assert.Empty(changes);
    }

    [Fact]
    public void DoesNotHalveAFrameRateThatIsAlreadyLow()
    {
        var service = new EncoderTuningService();
        var changes = new List<EncoderFrameRateChange>();
        service.FrameRateChangeRequested += (_, change) => changes.Add(change);
        service.BeginSession("P1", 30);

        Feed(service, severeSamples: 200, frameRate: 30);

        Assert.Empty(changes);
    }

    [Fact]
    public void IgnoresACaptureStallWhichNoEncoderSettingCanFix()
    {
        var service = new EncoderTuningService();
        var changes = new List<EncoderFrameRateChange>();
        service.FrameRateChangeRequested += (_, change) => changes.Add(change);
        service.BeginSession("P1", 60);

        // The display is not handing over frames at all. The encoder is idle and
        // blameless; lowering its target would fix nothing.
        Feed(service, severeSamples: 200, frameRate: 60, reason: ReplayDegradeReason.CaptureStall);

        Assert.Empty(changes);
    }

    private static void Feed(
        EncoderTuningService service,
        int severeSamples,
        int frameRate,
        int healthySamples = 0,
        ReplayDegradeReason reason = ReplayDegradeReason.EncoderOverload)
    {
        var clock = SessionStart;
        for (var i = 0; i < severeSamples + healthySamples; i++)
        {
            // Past the 30s warm-up before anything counts.
            clock += SampleInterval;
            var severe = i < severeSamples;
            service.OnHealth(Health(
                clock,
                frameRate,
                // Severe means output has collapsed below 70% of target, which
                // is what separates a real failure from a bursty loading screen.
                outputFrameRate: severe ? frameRate * 0.3 : frameRate,
                reason: severe ? reason : ReplayDegradeReason.None));
        }
    }

    private static ReplayCaptureHealth Health(DateTime updatedUtc, int targetFrameRate, double outputFrameRate, ReplayDegradeReason reason)
    {
        var overloaded = reason != ReplayDegradeReason.None;
        return new ReplayCaptureHealth(
            "Hybrid",
            "Desktop Duplication",
            overloaded ? ReplayCaptureState.Degraded : ReplayCaptureState.Healthy,
            targetFrameRate,
            InputFrameRate: targetFrameRate,
            UniqueFrameRate: targetFrameRate,
            outputFrameRate,
            DuplicateFrames: 0,
            DroppedFrames: overloaded ? 90 : 0,
            QueueDepth: overloaded ? 30 : 0,
            "h264_qsv",
            "Default adapter",
            string.Empty,
            updatedUtc)
        {
            // Both of these have to be populated or the service treats the
            // record as coming from a backend that cannot report what it needs.
            EncodeQueueCapacity = 30,
            EncoderPreset = "P1",
            DegradeReason = reason,
            AdapterDescription = "Intel(R) Iris(R) Xe Graphics"
        };
    }
}
