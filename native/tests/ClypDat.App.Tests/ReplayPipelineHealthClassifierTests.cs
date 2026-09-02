using ClypDat.Capture.Abstractions;
using ClypDat.App.Services;
using Xunit;

namespace ClypDat.App.Tests;

public sealed class ReplayPipelineHealthClassifierTests
{
    [Fact]
    public void BlockingSubmissionWinsOverQueuePressure()
    {
        var stage = ReplayPipelineHealthClassifier.Classify(90, 90, .4, 11.1, 0, 12, 12, 197, 0, true);
        Assert.Equal(ReplayPipelineStage.EncoderSubmission, stage);
    }

    [Fact]
    public void EmptyQueueLowOutputIsSourceNotEncoder()
    {
        var stage = ReplayPipelineHealthClassifier.Classify(0, 0, .4, 16.7, 0, 0, 12, .4, 0, true);
        Assert.Equal(ReplayPipelineStage.SourceAcquisition, stage);
    }

    [Fact]
    public void HistogramReportsP95MaximumAndResets()
    {
        var histogram = new ReplayLatencyHistogram();
        for (var i = 0; i < 19; i++) histogram.Record(TimeSpan.FromMilliseconds(1));
        histogram.Record(TimeSpan.FromMilliseconds(20));
        var snapshot = histogram.SnapshotAndReset();
        Assert.Equal(1, snapshot.P95Milliseconds);
        Assert.Equal(20, snapshot.MaximumMilliseconds);
        Assert.Equal((0, 0), histogram.SnapshotAndReset());
    }

    [Fact]
    public void LatestPacingSkipsMissedTicksWithoutBursting()
    {
        var scheduled = TimeSpan.Zero;
        var interval = TimeSpan.FromMilliseconds(10);
        Assert.Equal(1, ReplayPacingPolicy.TakeLatestIntervals(TimeSpan.FromMilliseconds(10), interval, ref scheduled));
        Assert.Equal(3, ReplayPacingPolicy.TakeLatestIntervals(TimeSpan.FromMilliseconds(40), interval, ref scheduled));
        Assert.Equal(TimeSpan.FromMilliseconds(40), scheduled);
    }

    [Fact]
    public void LatestPacingAcceptsNormalSubMillisecondEarlyWake()
    {
        var scheduled = TimeSpan.Zero;
        var interval = TimeSpan.FromSeconds(1.0 / 120);

        Assert.Equal(1, ReplayPacingPolicy.TakeLatestIntervals(
            interval - TimeSpan.FromMilliseconds(.75), interval, ref scheduled));
        Assert.Equal(interval, scheduled);
    }

    [Fact]
    public void EarlyWakeJitterStillProducesEvery120FpsDeadline()
    {
        var scheduled = TimeSpan.Zero;
        var interval = TimeSpan.FromSeconds(1.0 / 120);
        long encoded = 0;

        for (var tick = 1; tick <= 120; tick++)
        {
            var wake = TimeSpan.FromTicks(interval.Ticks * tick) - TimeSpan.FromMilliseconds(.75);
            if (ReplayPacingPolicy.TakeLatestIntervals(wake, interval, ref scheduled) > 0) encoded++;
        }

        Assert.Equal(120, encoded);
        Assert.Equal(TimeSpan.FromTicks(interval.Ticks * 120), scheduled);
    }

    [Fact]
    public void PeriodicMaintenanceStillRunsWhenLatestPacingSkipsAFrame()
    {
        var lastMaintenance = TimeSpan.Zero;
        var interval = TimeSpan.FromSeconds(1);

        Assert.False(ReplayPacingPolicy.IsMaintenanceDue(TimeSpan.FromMilliseconds(999), interval, ref lastMaintenance));
        Assert.True(ReplayPacingPolicy.IsMaintenanceDue(TimeSpan.FromSeconds(1), interval, ref lastMaintenance));
        Assert.Equal(TimeSpan.FromSeconds(1), lastMaintenance);
        Assert.False(ReplayPacingPolicy.IsMaintenanceDue(TimeSpan.FromSeconds(1.5), interval, ref lastMaintenance));
        Assert.True(ReplayPacingPolicy.IsMaintenanceDue(TimeSpan.FromSeconds(2), interval, ref lastMaintenance));
    }
}
