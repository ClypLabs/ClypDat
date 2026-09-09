using ClypDat.App.Services;
using Xunit;

namespace ClypDat.App.Tests;

public sealed class ClipRenderFiltersTests
{
    [Fact]
    public void EditorOffersSlowAndFastSpeedPresets()
    {
        Assert.Equal(new[] { 0.25, 0.5, 1.0, 1.5, 2.0, 4.0 }, ClipRenderFilters.SpeedPresets);
    }

    [Theory]
    [InlineData(0.5, "atempo=0.5", 20)]
    [InlineData(0.25, "atempo=0.5,atempo=0.5", 40)]
    public void SlowSpeedsKeepPitchAndExpandOutputDuration(double speed, string audioFilter, double duration)
    {
        Assert.Equal(audioFilter, ClipRenderFilters.BuildAudioSpeedFilter(speed));
        Assert.Equal(duration, ClipRenderFilters.AdjustDuration(10, speed));
    }
}
