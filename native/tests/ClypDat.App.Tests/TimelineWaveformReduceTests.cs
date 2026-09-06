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
}
