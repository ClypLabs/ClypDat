using System.Text;
using ClypDat.App.Services;
using Xunit;

namespace ClypDat.App.Tests;

/// <summary>
/// The Spotify app on this PC (no sign-in) and the optional Spotify account,
/// and how the two combine into the one track a clip is stamped with.
/// </summary>
public sealed class SpotifySourceTests : IDisposable
{
    // Covers are cached under app data; keep the tests' ones out of the real folder.
    private readonly string _previousProductFolder = ClypDat.Core.Settings.AppDataPaths.ProductFolderName;

    public SpotifySourceTests() =>
        ClypDat.Core.Settings.AppDataPaths.ConfigureProductFolder("ClypDat-SpotifyTests-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        var root = ClypDat.Core.Settings.AppDataPaths.Root;
        ClypDat.Core.Settings.AppDataPaths.ConfigureProductFolder(_previousProductFolder);
        try { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); } catch { }
    }

    private static string LocalArt => Path.Combine(SpotifyLocalArt.Folder, new string('a', 64) + ".jpg");

    private static SpotifyNowPlaying Local(string track, string artist, bool playing = true, string? art = null) =>
        new(true, null, track, artist, "Album", TimeSpan.FromMinutes(3), TimeSpan.FromSeconds(30), playing, DateTimeOffset.UtcNow, null, LocalArtPath: art);

    private static SpotifyNowPlaying Web(string track, string artist, bool playing = true) =>
        new(true, null, track, artist, "Album", TimeSpan.FromMinutes(3), TimeSpan.FromSeconds(31), playing, DateTimeOffset.UtcNow, null,
            ArtUrl: "https://i.scdn.co/image/abc", TrackId: "track-id");

    [Fact]
    public void TurnedOffReadsAsDisconnectedWithItsNotice()
    {
        var merged = SpotifySnapshotMerge.Merge(false, Local("Song", "Artist"), null, null, "Disconnected from your ClypDat account page.");
        Assert.False(merged.IsConnected);
        Assert.Null(merged.Track);
        Assert.Equal("Disconnected from your ClypDat account page.", merged.Error);
    }

    [Fact]
    public void ThisPcAloneIsEnoughWithNoSignIn()
    {
        var merged = SpotifySnapshotMerge.Merge(true, Local("Song", "Artist", art: LocalArt), null, null, null);
        Assert.True(merged.IsConnected);
        Assert.Equal("Song", merged.Track);
        Assert.Equal(LocalArt, merged.LocalArtPath);
        Assert.Null(merged.ArtUrl);
    }

    [Fact]
    public void OnWithNothingPlayingIsStillConnected()
    {
        var merged = SpotifySnapshotMerge.Merge(true, null, null, null, null);
        Assert.True(merged.IsConnected);
        Assert.Null(merged.Track);
    }

    [Fact]
    public void TheAccountAddsItsCoverAndIdToTheSameSong()
    {
        var merged = SpotifySnapshotMerge.Merge(true, Local("Song", "Artist A, Artist B", art: LocalArt), Web("Song", "Artist A"), "listener", null);
        Assert.Equal("Song", merged.Track);
        Assert.Equal("https://i.scdn.co/image/abc", merged.ArtUrl);
        Assert.Equal("track-id", merged.TrackId);
        Assert.Equal("listener", merged.DisplayName);
        // Local position wins: it is the one that moved last.
        Assert.Equal(TimeSpan.FromSeconds(30), merged.Progress);
    }

    [Fact]
    public void ADifferentSongPlayingElsewhereWinsOverAPausedDesktop()
    {
        var merged = SpotifySnapshotMerge.Merge(true, Local("Old song", "Artist", playing: false), Web("Phone song", "Other"), null, null);
        Assert.Equal("Phone song", merged.Track);
        Assert.True(merged.IsPlaying);
    }

    [Fact]
    public void ThePlayingDesktopWinsOverAStaleAccountReading()
    {
        var merged = SpotifySnapshotMerge.Merge(true, Local("New song", "Artist"), Web("Previous song", "Artist", playing: false), null, null);
        Assert.Equal("New song", merged.Track);
        // Not enriched with the previous song's cover.
        Assert.Null(merged.ArtUrl);
    }

    [Theory]
    [InlineData("Song", "A, B", "song", "A", true)]
    [InlineData("Song", "B & A", "Song", "A", true)]
    [InlineData("Song", null, "Song", "A", true)]
    [InlineData("Song", "A", "Other", "A", false)]
    [InlineData("Song", "A", "Song", "Z", false)]
    public void SameTrackToleratesHowArtistsAreJoined(string track, string? artist, string otherTrack, string otherArtist, bool expected)
    {
        var a = Local(track, artist!) with { Artist = artist };
        Assert.Equal(expected, SpotifySnapshotMerge.SameTrack(a, Web(otherTrack, otherArtist)));
    }

    [Theory]
    [InlineData("Spotify.exe", true)]
    [InlineData("SpotifyAB.SpotifyMusic_zpnnbwrqh6hq!Spotify", true)]
    [InlineData("Microsoft.ZuneMusic_8wekyb3d8bbwe!Microsoft.ZuneMusic", false)]
    [InlineData("chrome.exe", false)]
    [InlineData(null, false)]
    public void OnlySpotifysMediaSessionIsRead(string? appId, bool expected) =>
        Assert.Equal(expected, SpotifyLocalSource.IsSpotifyApp(appId));

    [Theory]
    [InlineData("Advertisement", null, true)]
    [InlineData("Spotify Free", null, true)]
    [InlineData("Advertisement", "An Artist", false)]
    [InlineData("A Song", null, false)]
    public void AdvertsAreNotStampedAsSongs(string track, string? artist, bool expected) =>
        Assert.Equal(expected, SpotifyLocalSource.IsAdvert(track, artist));

    [Fact]
    public void OnlyFlatHashNamedCoversInTheCacheFolderAreTrusted()
    {
        var folder = Path.Combine(Path.GetTempPath(), "spotify-local-art-test");
        var hash = new string('b', 64);
        Assert.True(SpotifyLocalArt.IsLocalArtPath(folder, Path.Combine(folder, hash + ".jpg")));
        Assert.False(SpotifyLocalArt.IsLocalArtPath(folder, Path.Combine(folder, "sub", hash + ".jpg")));
        Assert.False(SpotifyLocalArt.IsLocalArtPath(folder, Path.Combine(folder, "..", hash + ".jpg")));
        Assert.False(SpotifyLocalArt.IsLocalArtPath(folder, Path.Combine(folder, hash + ".png")));
        Assert.False(SpotifyLocalArt.IsLocalArtPath(folder, Path.Combine(folder, "cover.jpg")));
        Assert.False(SpotifyLocalArt.IsLocalArtPath(folder, @"\\host\share\" + hash + ".jpg"));
        Assert.False(SpotifyLocalArt.IsLocalArtPath(folder, hash + ".jpg"));
        Assert.False(SpotifyLocalArt.IsLocalArtPath(folder, null));
    }

    [Fact]
    public void HistoryCarriesTheLocalCoverOnlyWhenTheAccountGaveNoUrl()
    {
        var folder = Path.Combine(Path.GetTempPath(), "spotify-history-local-" + Guid.NewGuid());
        Directory.CreateDirectory(folder);
        try
        {
            var history = new SpotifyPlaybackHistory(Path.Combine(folder, "history.json"));
            history.Add(100, Local("Song", "Artist", art: LocalArt));
            history.Add(110, Web("Song", "Artist") with { LocalArtPath = LocalArt });
            var samples = history.Materialize(99, 20).Samples.Where(sample => sample.Available).ToArray();
            Assert.Equal(LocalArt, samples[0].LocalArtPath);
            Assert.Null(samples[1].LocalArtPath);
            Assert.Equal("https://i.scdn.co/image/abc", samples[1].ArtUrl);
        }
        finally { Directory.Delete(folder, recursive: true); }
    }

    [Fact]
    public async Task LocalCoversAreImportedIntoTheArchiveAndUntrustedOnesIgnored()
    {
        var root = Path.Combine(Path.GetTempPath(), "spotify-local-import-" + Guid.NewGuid());
        Directory.CreateDirectory(root);
        var cached = SpotifyLocalArt.Save(Encoding.UTF8.GetBytes("local cover " + Guid.NewGuid()));
        Assert.NotNull(cached);
        try
        {
            var timeline = new SpotifyTimeline(SpotifyTimeline.CurrentVersion, new[]
            {
                new SpotifyTimelineSample(0, null, "Song", "Artist", null, null, null, true, null, true, LocalArtPath: cached),
                new SpotifyTimelineSample(5, null, "Other", "Artist", null, null, null, true, null, true, LocalArtPath: Path.Combine(root, "elsewhere.jpg")),
            });
            var imported = await SpotifyCoverArtStore.ImportLocalArtAsync(root, timeline, CancellationToken.None);
            var first = imported.Samples[0];
            Assert.Null(first.LocalArtPath);
            Assert.True(SpotifyCoverArtStore.IsArchivedArtPath(root, first.ArtPath));
            Assert.Equal(File.ReadAllBytes(cached!), File.ReadAllBytes(first.ArtPath!));
            // Not a cached cover: never read, never imported.
            Assert.Null(imported.Samples[1].ArtPath);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void AnAllowListRefusalIsReadFromSpotifysErrorBody()
    {
        Assert.Equal("Check settings on developer.spotify.com/dashboard, the user may not be registered.",
            SpotifyNowPlayingService.ApiError("{\"error\":{\"status\":403,\"message\":\"Check settings on developer.spotify.com/dashboard, the user may not be registered.\"}}"));
        Assert.Equal("invalid_grant", SpotifyNowPlayingService.ApiError("{\"error\":\"invalid_grant\"}"));
        Assert.Null(SpotifyNowPlayingService.ApiError(""));
    }

    [Fact]
    public void TheFailurePageEscapesItsMessage()
    {
        var page = Encoding.UTF8.GetString(BrowserCallbackPage.Failure(BrowserCallbackService.Spotify, "<script>alert(1)</script>"));
        Assert.Contains("&lt;script&gt;", page);
        Assert.DoesNotContain("<script>alert(1)", page);
        Assert.Contains("not connected", page);
        var success = Encoding.UTF8.GetString(BrowserCallbackPage.Success(BrowserCallbackService.Spotify));
        Assert.Contains("Spotify is <span class=\"accent\">connected</span>", success);
        foreach (var placeholder in new[] { "__NAME__", "__TITLE__", "__EYEBROW__", "__HEADING__", "__BADGE__", "__DETAIL__", "__LOGO__" })
            Assert.DoesNotContain(placeholder, success);
    }
}
