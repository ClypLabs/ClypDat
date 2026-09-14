using ClypDat.App.Services;
using ClypDat.Capture.Abstractions;
using ClypDat.Core.Settings;
using Xunit;

namespace ClypDat.App.Tests;

public sealed class LinuxDetectionTests
{
    private const string Uuid = "aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee";
    private static KdeWindowMetadata Window(bool focused = true, bool minimized = false) =>
        new(1, "added", Uuid, "fixture", "Fixture", 1234, focused, minimized, 0, 0, 1280, 720);
    private static ForegroundGameDetector Detector()
    {
        var detector = new ForegroundGameDetector(new SteamGameLibrary(() => null));
        detector.ApplyCustomGameNames([new GameCaptureOverride { ExecutableName = "fixture.exe", DisplayName = "Fixture" }]);
        return detector;
    }
    [Fact]
    public void CustomGameUsesCompositorIdentityAndSurvivesMinimize()
    {
        var detector = Detector();
        var process = new LinuxProcessIdentity(1234, 42, "/games/fixture.exe", null);
        var foreground = detector.MatchLinuxWindow(Window(), process);
        Assert.True(foreground.IsDetected);
        Assert.Equal(0, foreground.WindowHandle);
        Assert.Equal(new LinuxCaptureTarget(LinuxCaptureTargetKind.KdeWindow, Uuid), foreground.LinuxTarget);
        Assert.Equal(foreground, detector.SelectLinuxGame([foreground]));
        var minimized = detector.MatchLinuxWindow(Window(false, true), process);
        Assert.True(minimized.IsDetected);
        Assert.False(detector.SelectLinuxGame([minimized]).IsForeground);
        Assert.False(detector.SelectLinuxGame([]).IsDetected);
    }
    [Fact]
    public void PidReuseCannotInheritTheRememberedWindow()
    {
        var detector = Detector();
        var old = detector.MatchLinuxWindow(Window(), new(1234, 42, "/games/fixture.exe", null));
        detector.SelectLinuxGame([old]);
        var reused = old with { LinuxProcessStartTime = 43, IsForeground = false };
        var other = old with { ProcessId = 5678, LinuxTarget = new(LinuxCaptureTargetKind.KdeWindow, Guid.NewGuid().ToString()), IsForeground = false };
        Assert.Equal(other, detector.SelectLinuxGame([other, reused]));
        Assert.False(detector.MatchLinuxWindow(Window(), null).IsDetected);
        Assert.False(detector.MatchLinuxWindow(Window(), new(1235, 42, "/games/fixture.exe", null)).IsDetected);
    }
    [Fact]
    public void IgnoredCustomGameIsNotSelected()
    {
        var detector = Detector(); detector.ApplyUserIgnoredExecutables(["fixture.exe"]);
        Assert.False(detector.MatchLinuxWindow(Window(), new(1234, 42, "/games/fixture.exe", null)).IsDetected);
    }
    [Fact]
    public void ProcStartTimeAllowsSpacesAndClosingParenthesesInProcessName()
    {
        var stat = "1234 (name with ) parentheses) S " + string.Join(' ', Enumerable.Range(4, 19));
        Assert.Equal(22UL, LinuxProcessIdentity.ReadStartTime(stat));
        Assert.Null(LinuxProcessIdentity.ReadStartTime("invalid"));
    }
    [Fact]
    public void MissingMonitorDoesNotFallBackToAnotherOutput()
    {
        var monitors = LinuxDesktopOutputs.Parse("{\"name\":\"DP-2\",\"x\":0,\"y\":0,\"width\":2560,\"height\":1440}");
        Assert.Equal("DP-2", DesktopMonitorService.Resolve(null, monitors).DeviceName);
        var absent = DesktopMonitorService.Resolve("DP-1", monitors);
        Assert.Equal("DP-1", absent.DeviceName);
        Assert.Equal(0, absent.Width);
    }
    [Theory]
    [InlineData("DP-1|monitor:DP-2")]
    [InlineData("DP-1;width=123")]
    public void RejectsRecorderSourceSyntaxInjection(string name) =>
        Assert.Throws<ArgumentException>(() => new LinuxCaptureTarget(LinuxCaptureTargetKind.KdeOutput, name).ToRecorderSource());
}
