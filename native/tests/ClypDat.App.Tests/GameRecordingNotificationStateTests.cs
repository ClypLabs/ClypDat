using ClypDat.App.Services;
using Xunit;

namespace ClypDat.App.Tests;

public sealed class GameRecordingNotificationStateTests
{
    private static GameDetection Game => new("Example Game", "game.exe", "Game", "Game", 123, 42, true,
        IsForeground: true, DetectionKey: "example-game");

    [Fact]
    public void BackgroundStartWaitsForForegroundAndDoesNotRepeatOnAltTabOrQualityRestart()
    {
        var state = new GameRecordingNotificationState();
        Assert.False(state.TryAnnounce(Game, recordingReady: false, enabled: true));
        Assert.False(state.TryAnnounce(Game with { IsForeground = false }, recordingReady: true, enabled: true));
        Assert.True(state.TryAnnounce(Game, recordingReady: true, enabled: true));
        Assert.False(state.TryAnnounce(Game with { IsForeground = false }, recordingReady: true, enabled: true));
        Assert.False(state.TryAnnounce(Game, recordingReady: true, enabled: true));
        Assert.False(state.TryAnnounce(Game, recordingReady: false, enabled: true));
        Assert.False(state.TryAnnounce(Game, recordingReady: true, enabled: true));
    }

    [Fact]
    public void DisabledOrStoppedCaptureCannotAnnounceAndNewSessionsCan()
    {
        var state = new GameRecordingNotificationState();
        Assert.False(state.TryAnnounce(GameDetection.None, recordingReady: true, enabled: true));
        Assert.False(state.TryAnnounce(Game, recordingReady: true, enabled: false));
        Assert.False(state.TryAnnounce(Game, recordingReady: false, enabled: true));
        Assert.True(state.TryAnnounce(Game, recordingReady: true, enabled: true));
        Assert.True(state.TryAnnounce(Game with { ProcessId = 43 }, recordingReady: true, enabled: true));
        Assert.False(state.TryAnnounce(GameDetection.None, recordingReady: false, enabled: true));
        Assert.True(state.TryAnnounce(Game, recordingReady: true, enabled: true));
    }
}
