using ClypDat.App.Services;
using Xunit;

namespace ClypDat.App.Tests;

public sealed class FullSessionCaptureTests
{
    [Fact]
    public void RecordedSourceTimesSurvivePublicationWithHeldAndPausedIntervals()
    {
        using var library = new LibraryFixture();
        var config = TestReplayConfiguration.Create(library.Root, "MKV") with { GameDisplayName = "Fortnite" };
        var sourceStart = MonotonicClock.SharedSeconds + 100;
        var recordedStart = MonotonicClock.FromSharedSeconds(sourceStart);
        FullSessionPublication.Publish(config, library.Session);
        FullSessionPublication.KeepCaptureSource(config, library.Session, 9_000_000,
            [new(sourceStart, 2, 0), new(sourceStart + 30, 4, 5)], videoMappingComplete: true);
        var info = ClipInfoSidecar.Load(library.Root, library.Session)!;
        Assert.Equal(recordedStart, info.CaptureStartUtc);
        Assert.Equal(recordedStart.AddSeconds(34), info.CaptureEndUtc);
        Assert.Equal(9, info.CaptureMediaDurationSeconds);
        Assert.True(info.CaptureTimingVerified);
        Assert.Equal(2, info.CaptureSegments!.Count);
        Assert.Equal(5, info.CaptureSegments[1].MediaStartSeconds);
        var source = SpotifySourceWindow.Load(library.Root, library.Session)!;
        Assert.Equal(2, source.VideoMappings!.Count);

        var timeline = MatchClipMapping.Describe(info, DateTimeOffset.UtcNow, TimeSpan.FromSeconds(9), true, source);
        var match = new MatchStats
        {
            Id = "fortnite-test", Game = FortniteMatchStats.Game, StartedUtc = recordedStart,
            EndedUtc = recordedStart.AddSeconds(40), DemoDurationSeconds = 40,
            Events = [new() { Kind = "kill", DemoSeconds = 1 }, new() { Kind = "kill", DemoSeconds = 10 }, new() { Kind = "kill", DemoSeconds = 31 }]
        };
        var markers = MatchClipMapping.Markers(match, timeline);
        Assert.Equal([1d, 6d], markers.Select(marker => marker.OffsetSeconds));

        var trimmed = ClipCaptureWindow.Rebase(info, 4, 8, 9);
        var fast = MatchClipMapping.Describe(trimmed, DateTimeOffset.UtcNow, TimeSpan.FromSeconds(2), false, null);
        var fastWindow = MatchClipMapping.ReplayWindow(match, fast)!;
        Assert.False(fastWindow.Contains(10));
        Assert.Equal(1, fastWindow.ToClipSeconds(31), 6);
        // Completing publication cannot discard already stored source metadata.
        FullSessionPublication.Publish(config, library.Session);
        Assert.Equal(info.CaptureSegments.ToArray(), ClipInfoSidecar.Load(library.Root, library.Session)!.CaptureSegments!.ToArray());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void MissingOrOverflowedSourceDoesNotInventPreciseSessionTimes(bool complete)
    {
        using var library = new LibraryFixture();
        var config = TestReplayConfiguration.Create(library.Root, "MKV");
        FullSessionPublication.Publish(config, library.Session);
        FullSessionPublication.KeepCaptureSource(config, library.Session, 9_000_000, [], complete);
        var info = ClipInfoSidecar.Load(library.Root, library.Session)!;
        Assert.Null(info.CaptureStartUtc);
        Assert.Null(info.CaptureSegments);
        Assert.Null(SpotifySourceWindow.Load(library.Root, library.Session));
    }

    [Fact]
    public void GenuineSourceBoundsWithoutIntervalsPermitOnlyWholeMatchStats()
    {
        using var library = new LibraryFixture();
        var config = TestReplayConfiguration.Create(library.Root, "MKV");
        var start = MonotonicClock.SharedSeconds + 100;
        FullSessionPublication.Publish(config, library.Session);
        FullSessionPublication.KeepCaptureSource(config, library.Session, 9_000_000, [], false, start, start + 40);
        var info = ClipInfoSidecar.Load(library.Root, library.Session)!;
        Assert.Equal(MonotonicClock.FromSharedSeconds(start), info.CaptureStartUtc);
        Assert.Equal(MonotonicClock.FromSharedSeconds(start + 40), info.CaptureEndUtc);
        Assert.False(info.CaptureTimingVerified);
        var timeline = MatchClipMapping.Describe(info, DateTimeOffset.UtcNow, TimeSpan.FromSeconds(9), true, null);
        Assert.False(timeline.Exact);
        var match = new MatchStats { Game = FortniteMatchStats.Game, StartedUtc = info.CaptureStartUtc!.Value, EndedUtc = info.CaptureEndUtc!.Value, DemoDurationSeconds = 40 };
        Assert.Null(MatchClipMapping.ReplayWindow(match, timeline));
    }

    private sealed class LibraryFixture : IDisposable
    {
        public string Root { get; } = Path.Combine(AppContext.BaseDirectory, "full-session-capture-tests", Guid.NewGuid().ToString("N"));
        public string Session { get; }
        public LibraryFixture()
        {
            var folder = LibraryLayout.VodDirectory(Root, "Fortnite");
            Directory.CreateDirectory(folder);
            Session = Path.Combine(folder, "session.mkv");
            File.WriteAllBytes(Session, [0]);
        }
        public void Dispose() => Directory.Delete(Root, recursive: true);
    }
}
