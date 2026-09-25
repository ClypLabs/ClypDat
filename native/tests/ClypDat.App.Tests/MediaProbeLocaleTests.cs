using System.Globalization;
using ClypDat.App.Services;
using Xunit;

namespace ClypDat.App.Tests;

public sealed class MediaProbeLocaleTests
{
    [Theory]
    [InlineData("fi-FI")]
    [InlineData("de-DE")]
    [InlineData("en-US")]
    public void ProbeNumbersUseInvariantCulture(string cultureName)
    {
        var previousCulture = CultureInfo.CurrentCulture;
        var previousUiCulture = CultureInfo.CurrentUICulture;
        try
        {
            var culture = CultureInfo.GetCultureInfo(cultureName);
            CultureInfo.CurrentCulture = culture;
            CultureInfo.CurrentUICulture = culture;

            Assert.True(MediaProbeService.TryParseDuration("60.123456", out var duration));
            Assert.Equal(TimeSpan.FromSeconds(60.123456), duration);
            Assert.Equal(29.97002997002997, MediaProbeService.ParseRate("30000/1001"), 10);
            Assert.Equal(0, MediaProbeService.ParseRate("29,97"));
        }
        finally
        {
            CultureInfo.CurrentCulture = previousCulture;
            CultureInfo.CurrentUICulture = previousUiCulture;
        }
    }

    [Theory]
    [InlineData("")]
    [InlineData("not-a-number")]
    [InlineData("NaN")]
    [InlineData("Infinity")]
    [InlineData("-1.25")]
    [InlineData("1e100")]
    [InlineData("60,123456")]
    public void ProbeDurationRejectsInvalidOrLocalizedValues(string value)
    {
        Assert.False(MediaProbeService.TryParseDuration(value, out var duration));
        Assert.Equal(TimeSpan.Zero, duration);
    }
}
