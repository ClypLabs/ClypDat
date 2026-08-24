using ClypDat.App.Services;
using Xunit;

namespace ClypDat.GameDetection.Tests;

public sealed class PresentSamplingBudgetTests
{
    [Fact]
    public void KeepsMatching60HzSourceFreshAcrossPhaseBoundary()
    {
        var budget = new PresentSamplingBudget(60);
        var interval = TimeSpan.FromSeconds(1.0 / 60.0);
        var accepted = 0;
        var pending = false;

        for (var i = 0; i < 60; i++)
        {
            var now = TimeSpan.FromTicks(interval.Ticks * i);
            if (budget.TryConsume(now, pending))
            {
                accepted++;
                pending = true;
            }

            // The pacing tick consumes the latest pending present once per
            // output interval. The next source frame may race this boundary.
            pending = false;
        }

        Assert.Equal(60, accepted);
    }

    [Fact]
    public void PreservesCreditWhenPresentArrivesJustBeforeTick()
    {
        var budget = new PresentSamplingBudget(60);

        Assert.True(budget.TryConsume(TimeSpan.Zero, pendingSample: false));
        Assert.False(budget.TryConsume(TimeSpan.FromMilliseconds(16), pendingSample: true));
        Assert.True(budget.TryConsume(TimeSpan.FromMilliseconds(17), pendingSample: true));
    }

    [Fact]
    public void Bounds240HzInputNearConfigured60HzOutput()
    {
        var budget = new PresentSamplingBudget(60);
        var sourceInterval = TimeSpan.FromSeconds(1.0 / 240.0);
        var outputInterval = TimeSpan.FromSeconds(1.0 / 60.0);
        var accepted = 0;
        var pending = false;
        var nextOutput = TimeSpan.Zero;

        for (var i = 0; i < 240; i++)
        {
            var now = TimeSpan.FromTicks(sourceInterval.Ticks * i);
            while (now >= nextOutput)
            {
                pending = false;
                nextOutput += outputInterval;
            }

            if (budget.TryConsume(now, pending))
            {
                accepted++;
                pending = true;
            }
        }

        Assert.InRange(accepted, 58, 63);
    }

    [Theory]
    [InlineData(30, 30, 30, 30)]
    [InlineData(60, 60, 60, 60)]
    [InlineData(90, 90, 90, 90)]
    [InlineData(120, 90, 89, 91)]
    [InlineData(109, 90, 89, 91)]
    public void ProducerSamplingKeepsSlowSourcesAndCapsFastSources(
        int sourceFps, int selectedFps, int minimumAccepted, int maximumAccepted)
    {
        var budget = new PresentSamplingBudget(selectedFps);
        var interval = TimeSpan.FromSeconds(1.0 / sourceFps);
        var accepted = 0;

        // The producer has no downstream pending surface: it decides whether
        // to transport each acquired present before the cross-device copy.
        for (var i = 0; i < sourceFps; i++)
            if (budget.TryConsume(TimeSpan.FromTicks(interval.Ticks * i), pendingSample: true)) accepted++;

        Assert.InRange(accepted, minimumAccepted, maximumAccepted);
    }
}
