using ClypDat.App.Services;
using Xunit;

namespace ClypDat.App.Tests;

public sealed class ClipRenderFiltersTests
{
    [Theory]
    [InlineData(0.5, "atempo=0.5", 20)]
    public void SlowSpeedsKeepPitchAndExpandOutputDuration(double speed, string audioFilter, double duration)
    {
        Assert.Equal(audioFilter, ClipRenderFilters.BuildAudioSpeedFilter(speed));
        Assert.Equal(duration, ClipRenderFilters.AdjustDuration(10, speed));
    }
}
