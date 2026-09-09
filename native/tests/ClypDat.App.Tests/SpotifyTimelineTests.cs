using ClypDat.App.Services;
using Xunit;

namespace ClypDat.App.Tests;

public sealed class SpotifyTimelineTests
{
    private static SpotifyNowPlaying State(string id = "one", int progress = 10000, bool playing = true) =>
        new(true, null, id, "artist-" + id, "album-" + id, TimeSpan.FromMinutes(3), TimeSpan.FromMilliseconds(progress),
            playing, DateTimeOffset.UtcNow, null, null, id);

    [Fact]
    public void SourceWindowSurvivesDelayedCompletionAndRecovery()
    {
        WithHistory((history, path) =>
        {
            history.Add(100, State());
            history.Add(102, State(progress: 12000));
            history.Add(104, State("two", 300));
            history.Add(106, State("two", 2300, false));
            history.Add(108, State("two", 80000, false));
            history.Add(110, State("two", 80000));
            history.Add(112, State("one", 2000));
            var timeline = new SpotifyPlaybackHistory(path).Materialize(101, 20);
            Assert.Equal(11000, SpotifyTimelineSidecar.At(timeline, 0)!.ProgressMs);
            Assert.Equal("album-two", SpotifyTimelineSidecar.At(timeline, 3)!.Album);
            Assert.Equal(2300, SpotifyTimelineSidecar.At(timeline, 6)!.ProgressMs);
            Assert.Equal(80000, SpotifyTimelineSidecar.At(timeline, 7)!.ProgressMs);
            Assert.Equal(81000, SpotifyTimelineSidecar.At(timeline, 10)!.ProgressMs);
            Assert.Equal("one", SpotifyTimelineSidecar.At(timeline, 11)!.TrackId);
            Assert.Null(SpotifyTimelineSidecar.At(timeline, 17));
            Assert.Equal(12000, SpotifyTimelineSidecar.At(timeline, 1)!.ProgressMs);
        });
    }

    [Fact]
    public void ReconnectAndUnknownStartingStateNeverInventProgress()
    {
        WithHistory((history, _) =>
        {
            history.Add(10, State());
            history.Add(12, SpotifyNowPlaying.Disconnected);
            history.Add(20, State("two", 4000));
            var timeline = history.Materialize(8, 20);
            Assert.Null(SpotifyTimelineSidecar.At(timeline, 0));
            Assert.NotNull(SpotifyTimelineSidecar.At(timeline, 2));
            Assert.Null(SpotifyTimelineSidecar.At(timeline, 4));
            Assert.Equal(5000, SpotifyTimelineSidecar.At(timeline, 13)!.ProgressMs);
            Assert.Null(SpotifyTimelineSidecar.At(timeline, 18));
        });
    }

    [Fact]
    public void TrimAndSpeedRebaseProgressAndTransitionsTogether()
    {
        var timeline = new SpotifyTimeline(1, new[] {
            new SpotifyTimelineSample(0, "a", "A", "artist", "album", 100000, 5000, true, null, true),
            new SpotifyTimelineSample(10, "b", "B", "artist-b", "album-b", 200000, 1000, false, null, true),
            new SpotifyTimelineSample(14, "b", "B", "artist-b", "album-b", 200000, 1000, true, null, true) });
        var trim = SpotifyTimelineSidecar.Rebase(timeline, 4, 20, 2);
        Assert.Equal(9000, SpotifyTimelineSidecar.At(trim, 0)!.ProgressMs);
        Assert.Equal(13000, SpotifyTimelineSidecar.At(trim, 2)!.ProgressMs);
        Assert.Equal("b", SpotifyTimelineSidecar.At(trim, 3)!.TrackId);
        Assert.Equal(1000, SpotifyTimelineSidecar.At(trim, 4)!.ProgressMs);
        Assert.Equal(3000, SpotifyTimelineSidecar.At(trim, 6)!.ProgressMs);
        var twice = SpotifyTimelineSidecar.Rebase(trim, 1, 7, .5);
        Assert.Equal(13000, SpotifyTimelineSidecar.At(twice, 2)!.ProgressMs);
        Assert.Equal(1, twice.Samples[0].ProgressRate);
        Assert.Equal(1, new SpotifyTimelineSample(0, null, "legacy", null, null, null, null, true, null, true).ProgressRate);
    }

    [Fact]
    public void RetentionKeepsPrecedingSampleAndPinsPendingSaves()
    {
        WithHistory((history, path) =>
        {
            var now = MonotonicClock.SharedSeconds;
            history.Add(now, State());
            history.Pin("pending");
            history.Add(now + 1600, State("later"));
            var recovered = new SpotifyPlaybackHistory(path);
            Assert.Equal("one", SpotifyTimelineSidecar.At(recovered.Materialize(now, 1), 0)!.TrackId);
            recovered.Release("pending");
            recovered.Add(now + 1602, State("last"));
            Assert.Null(SpotifyTimelineSidecar.At(recovered.Materialize(now + 100, 1), 0));
        });
    }

    [Fact]
    public void MovingAndTrimmingCopiesArtworkBeforeDeletingOriginal()
    {
        var root = Path.Combine(Path.GetTempPath(), "spotify-files-" + Guid.NewGuid());
        try
        {
            var source = Path.Combine(root, "old.mp4"); var destination = Path.Combine(root, "new.mp4");
            var art = LibraryLayout.SidecarPath(root, source, ".spotify-art/track.jpg");
            Directory.CreateDirectory(Path.GetDirectoryName(art)!); File.WriteAllBytes(art, new byte[] { 1, 2, 3 });
            ClipInfoSidecar.Save(root, source, new ClipInfo("game", null, SpotifyArtPath: art));
            SpotifyTimelineSidecar.Save(root, source, new[] { new SpotifyTimelineSample(0, "a", "A", null, null, 100000, 1000, true, art, true) });
            LibraryLayout.MoveSidecars(root, source, destination);
            var timeline = SpotifyTimelineSidecar.Load(root, destination)!;
            Assert.True(File.Exists(timeline.Samples[0].ArtPath));
            Assert.False(File.Exists(art));
            Assert.Equal(timeline.Samples[0].ArtPath, ClipInfoSidecar.Load(root, destination)!.SpotifyArtPath);
            SpotifyTimelineSidecar.Copy(root, destination, destination, 2, 5, 2);
            Assert.Equal(3000, SpotifyTimelineSidecar.Load(root, destination)!.Samples[0].ProgressMs);
            ClipInfoSidecar.Delete(root, destination);
            Assert.False(File.Exists(timeline.Samples[0].ArtPath));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [Fact]
    public async Task CoverArchiveDeduplicatesAndMigratesLegacyReferences()
    {
        var root = Path.Combine(Path.GetTempPath(), "spotify-cover-archive-" + Guid.NewGuid());
        try
        {
            var first = await SpotifyCoverArtStore.ImportBytesAsync(root, new byte[] { 1, 2, 3 });
            var second = await SpotifyCoverArtStore.ImportBytesAsync(root, new byte[] { 1, 2, 3 });
            Assert.Equal(first, second);
            Assert.Single(Directory.EnumerateFiles(SpotifyCoverArtStore.ArchiveRoot(root), "*.jpg"));

            var clip = Path.Combine(root, "Clips", "Game", "clip.mp4");
            var legacy = SpotifyCoverArtStore.PathFor(root, clip);
            Directory.CreateDirectory(Path.GetDirectoryName(legacy)!);
            File.WriteAllBytes(legacy, new byte[] { 4, 5, 6 });
            ClipInfoSidecar.Save(root, clip, new ClipInfo("game", null, SpotifyArtPath: legacy));
            SpotifyTimelineSidecar.Save(root, clip, new[] { new SpotifyTimelineSample(0, "a", "A", null, null, null, null, true, legacy, true) });

            SpotifyCoverArtStore.MigrateLibrary(root);
            var migrated = ClipInfoSidecar.Load(root, clip)!;
            Assert.NotEqual(legacy, migrated.SpotifyArtPath);
            Assert.True(File.Exists(migrated.SpotifyArtPath));
            Assert.Equal(migrated.SpotifyArtPath, SpotifyTimelineSidecar.Load(root, clip)!.Samples[0].ArtPath);
            Assert.True(File.Exists(legacy));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [Theory]
    [InlineData(0, 100, 0)]
    [InlineData(48, 0, 0)]
    [InlineData(48, 1, 0)]
    [InlineData(48, 2, 24)]
    [InlineData(48, 3, 48)]
    [InlineData(48, 4, 48)]
    [InlineData(48, 5, 24)]
    [InlineData(48, 6, 0)]
    public void TitleOscillatesAt24PixelsWithEndPauses(double overflow, double seconds, double expected) =>
        Assert.Equal(expected, SpotifyOverlayCardRenderer.TitleOffset(overflow, seconds), 6);

    private static void WithHistory(Action<SpotifyPlaybackHistory, string> test)
    {
        var folder = Path.Combine(Path.GetTempPath(), "spotify-history-" + Guid.NewGuid());
        var path = Path.Combine(folder, "history.json");
        try { test(new SpotifyPlaybackHistory(path), path); }
        finally { if (Directory.Exists(folder)) Directory.Delete(folder, true); }
    }
}
