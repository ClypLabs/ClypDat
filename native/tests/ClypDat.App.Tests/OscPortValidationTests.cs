using ClypDat.App.ViewModels;
using Xunit;

namespace ClypDat.App.Tests;

public sealed class OscPortValidationTests
{
    [Theory]
    [InlineData("1")]
    [InlineData("9001")]
    [InlineData("65535")]
    public void PortsInRangeAreAccepted(string text)
    {
        Assert.Null(MainWindowViewModel.ValidateOscPortText(text, 9001));
    }

    [Theory]
    [InlineData("0", "Ports start at 1")]
    [InlineData("65536", "Ports go up to 65535")]
    [InlineData("99999", "Ports go up to 65535")]
    [InlineData("99999999999", "Ports go up to 65535")]
    [InlineData("", "Enter a port from 1 to 65535")]
    [InlineData("90a1", "Use numbers only")]
    [InlineData("-5", "Use numbers only")]
    public void PortsOutOfRangeAreRejectedAndNameTheWorkingPort(string text, string reason)
    {
        var error = MainWindowViewModel.ValidateOscPortText(text, 9001);
        Assert.NotNull(error);
        Assert.Contains(reason, error, StringComparison.Ordinal);
        Assert.Contains("OSC stays on port 9001.", error, StringComparison.Ordinal);
    }
}
