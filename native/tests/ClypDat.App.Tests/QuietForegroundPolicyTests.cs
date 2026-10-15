using ClypDat.App.Services;
using Xunit;

namespace ClypDat.App.Tests;

public sealed class QuietForegroundPolicyTests
{
    [Theory]
    [InlineData("Progman")]
    [InlineData("WorkerW")]
    [InlineData("Shell_TrayWnd")]
    [InlineData("Shell_SecondaryTrayWnd")]
    public void DesktopAndTaskbarCanBeTakenOver(string className) =>
        Assert.True(QuietForegroundPolicy.Allows(true, false, className));

    [Fact]
    public void NoForegroundWindowCanBeTakenOver() =>
        Assert.True(QuietForegroundPolicy.Allows(false, false, null));

    [Fact]
    public void ClypDatAlreadyInFrontCanBeRefocused() =>
        Assert.True(QuietForegroundPolicy.Allows(true, true, "Avalonia-1234"));

    [Theory]
    [InlineData("Chrome_WidgetWin_1")]
    [InlineData("CabinetWClass")]
    [InlineData(null)]
    public void AnotherAppInFrontIsLeftAlone(string? className) =>
        Assert.False(QuietForegroundPolicy.Allows(true, false, className));
}
