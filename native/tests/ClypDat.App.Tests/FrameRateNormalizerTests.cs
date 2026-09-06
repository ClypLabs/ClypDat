using ClypDat.App.Services;
using Xunit;

namespace ClypDat.App.Tests;

public sealed class FrameRateNormalizerTests
{
    // A CFR capture does not measure exactly on its target, because the frame
    // durations do not divide the timebase evenly. 30 has to read as 30.
    [Theory]
    [InlineData(119.983, 120)]
    [InlineData(120.0, 120)]
    [InlineData(90.0, 90)]
    [InlineData(59.94, 60)]
    [InlineData(60.0, 60)]
    [InlineData(29.97, 30)]
    [InlineData(30.0, 30)]
    public void ASteadyCaptureReportsItsRate(double measured, double expected)
    {
        Assert.Equal(expected, FrameRateNormalizer.Normalize(measured));
    }

    // The case this exists for: a clip that held 90fps but dropped frames in
    // places averages 79 over its duration. It was never a 79fps clip, and
    // rounding to the nearest 15 would call it 75.
    [Theory]
    [InlineData(79, 90)]
    [InlineData(77, 90)]
    [InlineData(52, 60)]
    [InlineData(26, 30)]
    // Up to the next multiple of 15, not up to the nearest rate the app can
    // record at: 103 is 105, not 120.
    [InlineData(103, 105)]
    [InlineData(115, 120)]
    public void FramesDroppedInPlacesStillReportsTheRateItRanAt(double measured, double expected)
    {
        Assert.Equal(expected, FrameRateNormalizer.Normalize(measured));
    }

    // Past that the drop is too big to keep claiming the target, so it rounds to
    // the closest 15 instead.
    [Theory]
    [InlineData(46, 45)]
    [InlineData(31, 30)]
    [InlineData(63, 60)]
    [InlineData(76, 75)]
    public void ABigDropRoundsToTheClosestRate(double measured, double expected)
    {
        Assert.Equal(expected, FrameRateNormalizer.Normalize(measured));
    }

    // Imported clips are not always multiples of 15, and must not be dragged
    // onto one - a 24fps import is not a 30fps clip.
    [Theory]
    [InlineData(24.0, 24)]
    [InlineData(25.0, 25)]
    [InlineData(50.0, 50)]
    public void FilmAndBroadcastRatesSurvive(double measured, double expected)
    {
        Assert.Equal(expected, FrameRateNormalizer.Normalize(measured));
    }

    // NTSC rates are the same rate by another name - 60000/1001 is what every
    // player calls 60 - so they read as the whole number, not as 59.94.
    [Theory]
    [InlineData(23.976, 24)]
    [InlineData(29.97, 30)]
    [InlineData(59.94, 60)]
    public void NtscRatesReadAsTheirWholeNumber(double measured, double expected)
    {
        Assert.Equal(expected, FrameRateNormalizer.Normalize(measured));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(double.NaN)]
    public void AnUnreadableRateStaysUnknown(double measured)
    {
        Assert.Equal(0, FrameRateNormalizer.Normalize(measured));
    }

    // Nothing useful to say about a rate past the ceiling, so it passes through
    // rather than being invented into something else.
    [Fact]
    public void AnImplausiblyHighRateIsLeftAlone()
    {
        Assert.Equal(1000, FrameRateNormalizer.Normalize(1000));
    }
}
