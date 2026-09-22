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
    public void LatestPacingSkipsMissedTicksWithoutBursting()
    {
        var scheduled = TimeSpan.Zero;
        var interval = TimeSpan.FromMilliseconds(10);
        Assert.Equal(1, ReplayPacingPolicy.TakeLatestIntervals(TimeSpan.FromMilliseconds(10), interval, ref scheduled));
        Assert.Equal(3, ReplayPacingPolicy.TakeLatestIntervals(TimeSpan.FromMilliseconds(40), interval, ref scheduled));
        Assert.Equal(TimeSpan.FromMilliseconds(40), scheduled);
    }
}
