using ClypDat.App.Services;
using Xunit;

namespace ClypDat.App.Tests;

public sealed class TimelineWaveformReduceTests
{
    // Zoomed out, the lane holds far fewer pixels than there are peaks, so the
    // geometry is bounded by the lane rather than by the stored resolution.
    [Fact]
    public void MorePeaksThanPixelsReduceToOnePerColumn()
    {
        var peaks = new double[16000];
        for (var i = 0; i < peaks.Length; i++) peaks[i] = i / (double)peaks.Length;

        Assert.Equal(800, WaveformPeakReducer.Reduce(peaks, 800).Length);
    }

    // Zoomed in far enough that the stored peaks run out, every one of them is
    // drawn - this is what makes zooming reveal detail instead of stretching it.
    [Fact]
    public void FewerPeaksThanPixelsAreAllKept()
    {
        var peaks = new double[300];

        Assert.Equal(300, WaveformPeakReducer.Reduce(peaks, 1200).Length);
    }

    // A transient inside a column has to survive it. Averaging would flatten a
    // single loud frame into nothing and draw a waveform that never happened.
    [Fact]
    public void TheLoudestPeakInAColumnSurvives()
    {
        var peaks = new double[1000];
        peaks[503] = 1.0;

        var reduced = WaveformPeakReducer.Reduce(peaks, 100);

        Assert.Equal(100, reduced.Length);
        Assert.Equal(1.0, reduced[50]);
        Assert.Equal(1.0, reduced.Max());
        Assert.Equal(1, reduced.Count(value => value > 0));
    }

    // Every column is covered, so no stretch of audio is silently skipped.
    [Fact]
    public void NoPeakIsSkippedBetweenColumns()
    {
        var peaks = new double[1000];
        for (var i = 0; i < peaks.Length; i++) peaks[i] = 0.5;

        Assert.All(WaveformPeakReducer.Reduce(peaks, 333), value => Assert.Equal(0.5, value));
    }

    [Fact]
    public void OutOfRangeValuesAreClamped()
    {
        var reduced = WaveformPeakReducer.Reduce([2.5, -1.0, 0.4], 3);

        Assert.Equal(1.0, reduced[0]);
        Assert.Equal(0.0, reduced[1]);
        Assert.Equal(0.4, reduced[2]);
    }

    // A music lane at -30 dBFS is 0.03 linear. Drawn linearly it is a flat line
    // in a 40px lane; on the decibel scale it reaches half of it.
    [Fact]
    public void QuietLaneReachesHalfHeight()
    {
        var peaks = new double[1000];
        for (var i = 0; i < peaks.Length; i++) peaks[i] = 0.03;

        Assert.All(WaveformLaneScale.Shape(peaks, 200), value => Assert.InRange(value, 0.45, 0.55));
    }

    // Below the floor is silence, and silence draws as nothing. Dither drawn at
    // any height would say a muted app was making noise.
    [Fact]
    public void SilentLaneStaysFlat()
    {
        var peaks = new double[1000];
        for (var i = 0; i < peaks.Length; i++) peaks[i] = 0.0005;

        Assert.All(WaveformLaneScale.Shape(peaks, 200), value => Assert.Equal(0, value));
    }

    // Full scale is the top of the lane, and the mapping never exceeds it.
    [Fact]
    public void FullScaleFillsTheLane()
    {
        Assert.Equal(1, WaveformLaneScale.Height(1));
        Assert.Equal(1, WaveformLaneScale.Height(2));
    }

    // A steady source draws steady, at a height that says what it was. This is
    // what per-lane normalization got wrong: it drew every constant level as a
    // solid full-height block whatever that level was.
    [Fact]
    public void SteadyQuietLaneIsNotDrawnAsFullHeight()
    {
        var peaks = new double[1000];
        for (var i = 0; i < peaks.Length; i++) peaks[i] = 0.03;

        Assert.All(WaveformLaneScale.Shape(peaks, 200), value => Assert.True(value < 0.7));
    }

    // Louder still draws taller, across the whole range.
    [Fact]
    public void LouderColumnsStayTaller()
    {
        var shaped = WaveformLaneScale.Shape([0.01, 0.1, 1.0], 3);

        Assert.True(shaped[0] < shaped[1]);
        Assert.True(shaped[1] < shaped[2]);
    }
}
