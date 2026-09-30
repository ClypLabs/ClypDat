using ClypDat.App.Services;
using ClypDat.App.Views;
using Xunit;

namespace ClypDat.App.Tests;

public sealed class ReplayTargetReconciliationTests
{
    private static ReplayBufferConfig Desktop => new(60, 1080, 60, 0, 0, 1920, 1080,
        "", "", [], [], "", [], "", "", "", "", CaptureSource: "Desktop", CaptureMonitorDeviceName: "DISPLAY1");

    public static IEnumerable<object[]> TargetChanges()
    {
        var desktop = Desktop;
        var game = desktop with { CaptureSource = "Game", GameExecutableName = "game-a.exe", GameWindowHandle = 101 };
        yield return [desktop, game]; // Start on desktop, detect a game later.
        yield return [game, game with { GameExecutableName = "game-b.exe", GameWindowHandle = 202 }];
        yield return [game, game with { GameWindowHandle = 303 }]; // Same game recreates its HWND.
        yield return [game, desktop];
        yield return [desktop, desktop with { CaptureMonitorDeviceName = "DISPLAY2", CaptureX = 1920 }];
        yield return [game, game with { CaptureMonitorDeviceName = "DISPLAY2" }];
    }

    [Theory]
    [MemberData(nameof(TargetChanges))]
    public void TargetChangesInvalidateBothUiRestartAndWorkerConfigurationIdentity(ReplayBufferConfig before, ReplayBufferConfig after)
    {
        Assert.NotEqual(MainWindow.ReplayTargetIdentity(before), MainWindow.ReplayTargetIdentity(after));
        Assert.NotEqual(ReplayBufferConfigIdentity.Serialize(before), ReplayBufferConfigIdentity.Serialize(after));
    }

    [Fact]
    public void UnchangedTargetDoesNotKeepRestartingForStatusOrDisplayNameChanges()
    {
        var target = Desktop with { CaptureSource = "Game", GameExecutableName = "game.exe", GameWindowHandle = 101 };
        Assert.Equal(MainWindow.ReplayTargetIdentity(target), MainWindow.ReplayTargetIdentity(target with { }));
        Assert.Equal(MainWindow.ReplayTargetIdentity(target), MainWindow.ReplayTargetIdentity(target with { GameDisplayName = "Friendly name" }));
    }
}
