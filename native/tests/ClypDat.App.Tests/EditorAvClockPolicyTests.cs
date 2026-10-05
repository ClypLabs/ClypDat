using ClypDat.App.Services;
using Xunit;

namespace ClypDat.App.Tests;

public sealed class EditorAvClockPolicyTests
{
    // Measured after resumes: audio matched wall time, libvlc's video time read
    // 160-480ms ahead on its 250ms grid. None of these is real drift.
    [Theory]
    [InlineData(32.126, 32.602)]
    [InlineData(49.371, 49.606)]
    [InlineData(45.187, 45.352)]
    public void LibVlcTimeAheadAfterResumeIsNotCorrected(double audible, double video)
    {
        var policy = new EditorAvClockPolicy();
        policy.Begin(1);
        Assert.False(Sample(policy, 0.5, audible, video));
        Assert.False(Sample(policy, 0.75, audible + 0.25, video + 0.25));
    }

    [Fact]
    public void VideoFarAheadIsCorrectedAfterTwoSamples()
    {
        var policy = new EditorAvClockPolicy();
        policy.Begin(1);
        Assert.False(Sample(policy, 0.5, 10, 11));
        Assert.True(policy.TryGetCorrection(1, TimeSpan.FromSeconds(0.75), TimeSpan.FromSeconds(10.25), TimeSpan.FromSeconds(11.25), out var correction));
        Assert.Equal(TimeSpan.FromSeconds(11.25), correction);
    }

    [Fact]
    public void VideoStalledBehindAudioIsStillCorrected()
    {
        var policy = new EditorAvClockPolicy();
        policy.Begin(1);
        Assert.False(Sample(policy, 0.5, 10.3, 10.1));
        Assert.True(Sample(policy, 0.75, 10.55, 10.1));
    }

    [Fact]
    public void CorrectsAtMostOncePerGeneration()
    {
        var policy = new EditorAvClockPolicy();
        policy.Begin(1);
        Sample(policy, 0.5, 10.3, 10.1);
        Assert.True(Sample(policy, 0.75, 10.55, 10.1));
        Assert.False(Sample(policy, 1.0, 10.8, 10.1));
        Assert.False(policy.TryGetCorrection(2, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(10.8), TimeSpan.FromSeconds(10.1), out _));
    }

    private static bool Sample(EditorAvClockPolicy policy, double elapsed, double audible, double video) =>
        policy.TryGetCorrection(1, TimeSpan.FromSeconds(elapsed), TimeSpan.FromSeconds(audible), TimeSpan.FromSeconds(video), out _);
}
