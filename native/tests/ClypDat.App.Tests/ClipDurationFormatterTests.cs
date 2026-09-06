using ClypDat.App.Services;
using Xunit;

namespace ClypDat.App.Tests;

public sealed class ClipDurationFormatterTests
{
    [Theory]
    [InlineData(0, 0, 0, "0:00")]
    [InlineData(0, 0, 7, "0:07")]
    [InlineData(0, 14, 6, "14:06")]
    [InlineData(0, 59, 59, "59:59")]
    [InlineData(1, 0, 0, "1:00:00")]
    // The bug, exactly: a three-hour session recording rendered as "14:06"
    // because "m\:ss" prints the minutes component and discards the hours.
    [InlineData(3, 14, 6, "3:14:06")]
    public void LengthsReadAsHoursMinutesSeconds(int hours, int minutes, int seconds, string expected)
    {
        Assert.Equal(expected, ClipDurationFormatter.Format(new TimeSpan(hours, minutes, seconds)));
    }

    // Built from TotalHours, not the "h" specifier, which is the hours component
    // and wraps to zero on the second day.
    [Fact]
    public void PastADayTheHoursKeepCounting()
    {
        Assert.Equal("26:01:02", ClipDurationFormatter.Format(new TimeSpan(1, 2, 1, 2)));
    }

    [Fact]
    public void NothingToShowReadsAsZero()
    {
        Assert.Equal("0:00", ClipDurationFormatter.Format(TimeSpan.Zero));
        Assert.Equal("0:00", ClipDurationFormatter.Format(TimeSpan.FromSeconds(-5)));
    }
}
