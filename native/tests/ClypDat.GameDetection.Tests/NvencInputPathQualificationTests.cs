using ClypDat.App.Services;
using Xunit;

namespace ClypDat.GameDetection.Tests;

public sealed class NvencInputPathQualificationTests
{
    [Fact]
    public void Select_UsesFasterSystemMemoryPath() =>
        Assert.Equal(NvencInputPath.SystemMemory, NvencInputPathQualification.Select(120, Result(110), Result(118)));

    [Fact]
    public void Select_PrefersD3D11WhenRatesAreWithinThreePercent() =>
        Assert.Equal(NvencInputPath.D3D11, NvencInputPathQualification.Select(120, Result(114), Result(117)));

    [Fact]
    public void Select_ExcludesUnavailablePaths() =>
        Assert.Equal(NvencInputPath.SystemMemory, NvencInputPathQualification.Select(120, new(false, 0), Result(100)));

    [Fact]
    public void Select_ChoosesBestPathEvenWhenNeitherReachesTarget() =>
        Assert.Equal(NvencInputPath.SystemMemory, NvencInputPathQualification.Select(120, Result(70), Result(80)));

    [Fact]
    public void Select_ExcludesTimedOutPath() =>
        Assert.Equal(NvencInputPath.SystemMemory, NvencInputPathQualification.Select(120, new(true, 200, TimedOut: true), Result(100)));

    [Fact]
    public void Result_ReportsTargetThreshold()
    {
        Assert.True(Result(114).ReachedTarget(120));
        Assert.False(Result(113.9).ReachedTarget(120));
    }

    [Fact]
    public void Select_ReturnsNullWhenBothPathsAreUnavailable() =>
        Assert.Null(NvencInputPathQualification.Select(120, new(false, 0), new(false, 0)));

    private static NvencInputPathQualification.Result Result(double framesPerSecond) => new(true, framesPerSecond);
}
