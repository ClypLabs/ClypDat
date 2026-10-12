using ClypDat.App.Services;
using Xunit;

namespace ClypDat.App.Tests;

public sealed class ClipCaptureWindowTests
{
    private static readonly DateTime Start = new(2026, 10, 10, 8, 0, 0, DateTimeKind.Utc);
    private static ClipInfo PausedClip => new("Fortnite", null, CaptureStartUtc: Start, CaptureEndUtc: Start.AddSeconds(30),
        CaptureSegments: [new(Start, Start.AddSeconds(10), 0, 10), new(Start.AddSeconds(20), Start.AddSeconds(30), 10, 20)],
        CaptureMediaDurationSeconds: 20, CaptureTimingVerified: true);

    private static MatchStats Match => new()
    {
        Game = "fortnite", StartedUtc = Start, EndedUtc = Start.AddMinutes(1), DemoDurationSeconds = 60,
        Events = [new() { Kind = "kill", DemoSeconds = 5 }, new() { Kind = "kill", DemoSeconds = 15 }, new() { Kind = "kill", DemoSeconds = 25 }]
    };

    [Fact]
    public void PauseGapIsExcludedAndLaterEventUsesRetainedMediaTime()
    {
        var timeline = MatchClipMapping.Describe(PausedClip, default, TimeSpan.FromSeconds(20), false, null);
        var window = MatchClipMapping.ReplayWindow(Match, timeline)!;
        Assert.False(window.Contains(15));
        Assert.Equal([5.0, 15.0], MatchClipMapping.Markers(Match, timeline).Select(marker => marker.OffsetSeconds));
        Assert.Equal(15, window.ToClipSeconds(25), 6);
    }

    [Fact]
    public void TrimAndSpedUpExportKeepTheTwoCapturedIntervals()
    {
        var trimmed = ClipCaptureWindow.Rebase(PausedClip, 5, 15, 20);
        Assert.Equal(Start.AddSeconds(5), trimmed.CaptureStartUtc);
        Assert.Equal(Start.AddSeconds(25), trimmed.CaptureEndUtc);
        Assert.Equal(10, trimmed.CaptureMediaDurationSeconds);
        Assert.Equal(2, trimmed.CaptureSegments!.Count);
        var timeline = MatchClipMapping.Describe(trimmed, default, TimeSpan.FromSeconds(5), false, null);
        var window = MatchClipMapping.ReplayWindow(Match, timeline)!;
        Assert.False(window.Contains(15));
        Assert.Equal(1, window.ToClipSeconds(7), 6);
        Assert.Equal(4, window.ToClipSeconds(23), 6);

        // Trim the already sped-up file: its media seconds remain authoritative.
        var retrimmed = ClipCaptureWindow.Rebase(trimmed, 2, 4, 5);
        Assert.Equal(Start.AddSeconds(9), retrimmed.CaptureStartUtc);
        Assert.Equal(Start.AddSeconds(23), retrimmed.CaptureEndUtc);
        Assert.Equal(2, retrimmed.CaptureMediaDurationSeconds);
    }

    [Fact]
    public void NativeSourcePreservesGapAndRejectsAnotherBoot()
    {
        var now = MonotonicClock.SharedSeconds;
        var source = new SpotifySourceWindow(now, 20, MonotonicClock.BootId,
            VideoMappings: [new(now, 10, 0), new(now + 20, 10, 10)]);
        var info = ClipCaptureWindow.FromSource(new("Fortnite", null), source);
        Assert.Equal(2, info.CaptureSegments!.Count);
        Assert.Equal(30, (info.CaptureEndUtc!.Value - info.CaptureStartUtc!.Value).TotalSeconds, 5);
        Assert.Equal(20, info.CaptureMediaDurationSeconds);
        Assert.True(info.CaptureTimingVerified);
        Assert.Same(info, ClipCaptureWindow.FromSource(info, source with { BootId = "different-boot" }));
    }

    [Fact]
    public void InvalidStoredSegmentCannotProducePreciseMarkers()
    {
        var info = PausedClip with { CaptureSegments = [new(Start, Start.AddSeconds(10), double.NaN, 10)] };
        var timeline = MatchClipMapping.Describe(info, default, TimeSpan.FromSeconds(20), false, null);
        Assert.Null(MatchClipMapping.ReplayWindow(Match, timeline));
        Assert.Empty(MatchClipMapping.Markers(Match, timeline));
    }

    [Fact]
    public void RetimedAudioOrEstimatedFileTimesCannotPretendToBeVideoProvenance()
    {
        var now = MonotonicClock.SharedSeconds;
        var info = new ClipInfo("Fortnite", null);
        var audioOnly = new SpotifySourceWindow(now, 20, MonotonicClock.BootId, [new(now, 20, 0)]);
        Assert.Same(info, ClipCaptureWindow.FromSource(info, audioOnly));
        var timeline = MatchClipMapping.Describe(info, Start.AddSeconds(20), TimeSpan.FromSeconds(20), false, audioOnly);
        Assert.False(timeline.Exact);
        Assert.Null(MatchClipMapping.ReplayWindow(Match, timeline));
    }

    [Fact]
    public void SavedReplayIdentityNeverFallsBackToAnotherMatch()
    {
        var info = PausedClip with { MatchSessionId = "original.replay" };
        var timeline = MatchClipMapping.Describe(info, default, TimeSpan.FromSeconds(20), false, null);
        Assert.Null(MatchClipMapping.FindMatch([Match with { Id = "fortnite-other" }], timeline, "fortnite"));
    }
}
