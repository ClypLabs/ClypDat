using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using ClypDat.App.Services;
using ClypDat.App.ViewModels;
using Microsoft.Data.Sqlite;
using Xunit;

namespace ClypDat.App.Tests;

// This branch and the release (master) share %LOCALAPPDATA%\ClypDat. Their
// probe cache (media-cache\*-probe.json) and library cache (library-cache.db)
// must be one format, or every switch between the two builds throws the other's
// entries away and the whole library shows 0:00 until it is probed again.
public sealed class CacheCompatibilityTests
{
    // What the release writes today (master 0762f754): probe entries at schema
    // 3, the library database at user_version 2. This entry is byte for byte
    // what 1.6.1 wrote for a 1 s 160x90 clip, apart from size and mtime.
    private const string ReleaseProbeEntry =
        """{"Duration":"00:00:01","SizeBytes":{0},"LastWriteTimeUtcTicks":{1},"Width":160,"Height":90,"Fps":30,"CaptureBackend":"","Tracks":[{"Index":0,"Type":"video","Codec":"h264","Label":"Video 0 (und)","VolumePercent":100},{"Index":1,"Type":"audio","Codec":"aac","Label":"Audio 1 (und)","VolumePercent":100}],"HasVideo":true,"SchemaVersion":3,"SpotifyOverlayBurned":false}""";

    private sealed class Sandbox : IDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), $"clypdat-cache-compat-{Guid.NewGuid():N}");
        public string Library => Path.Combine(Root, "Videos");
        public string MediaCache => Path.Combine(Root, "media-cache");
        public string Database => Path.Combine(Root, "library-cache.db");

        public Sandbox()
        {
            Directory.CreateDirectory(Library);
            Directory.CreateDirectory(MediaCache);
            SQLitePCL.raw.SetProvider(new SQLitePCL.SQLite3Provider_winsqlite3());
        }

        public async Task<string> ClipAsync(string name)
        {
            var path = Path.Combine(Library, name);
            await SpotifyOverlayBurnerTests.Run("-f", "lavfi", "-i", "color=black:s=160x90:r=30:d=1", "-f", "lavfi", "-i", "anullsrc=r=48000:cl=stereo",
                "-t", "1", "-c:v", "libx264", "-c:a", "aac", path);
            return path;
        }

        public string ProbeEntryPath(string clip) =>
            Path.Combine(MediaCache, Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(clip)))[..24].ToLowerInvariant() + "-probe.json");

        public void Dispose()
        {
            SqliteConnection.ClearAllPools();
            try { Directory.Delete(Root, recursive: true); } catch (IOException) { }
        }
    }

    private static T InCulture<T>(string name, Func<T> action)
    {
        var (culture, ui) = (CultureInfo.CurrentCulture, CultureInfo.CurrentUICulture);
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo(name);
            return action();
        }
        finally
        {
            (CultureInfo.CurrentCulture, CultureInfo.CurrentUICulture) = (culture, ui);
        }
    }

    private static long UserVersion(string database)
    {
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = database }.ToString());
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA user_version;";
        return (long)command.ExecuteScalar()!;
    }

    [Theory]
    [InlineData("de-DE")]
    [InlineData("fi-FI")]
    [InlineData("en-AU")]
    public async Task ProbedMetadataSurvivesRestart_InAnyCulture(string culture)
    {
        FfmpegPathResolver.EnsureBundledFfmpeg();
        using var sandbox = new Sandbox();
        var clip = await sandbox.ClipAsync("Game - Sep-26-2026 - 14-35-07.mp4");

        // First run probes (a comma-decimal culture must still read "1.000000" and "30/1").
        var probed = await InCulture(culture, () => new MediaProbeService(sandbox.MediaCache).ProbeMetadataDetailedAsync(clip));
        Assert.False(probed.FromCache);
        Assert.Null(probed.Error);
        Assert.InRange(probed.Media.Duration.TotalSeconds, 0.9, 1.1);
        Assert.Equal(30, probed.Media.Fps);
        Assert.Contains("\"SchemaVersion\":3", File.ReadAllText(sandbox.ProbeEntryPath(clip)));

        // A restarted app, in another culture again, reads it without ffprobe.
        var restarted = new MediaProbeService(sandbox.MediaCache);
        var stub = InCulture("de-DE", () => restarted.CreateLibraryStub(clip));
        Assert.Equal(probed.Media.Duration, stub.Duration);
        Assert.Equal(probed.Media.Tracks.Count, stub.Tracks.Count);
        Assert.Equal(30, stub.Fps);
        Assert.True((await restarted.ProbeMetadataDetailedAsync(clip)).FromCache);
    }

    [Fact]
    public async Task ReleaseWrittenProbeCache_IsServedWithoutReprobing()
    {
        FfmpegPathResolver.EnsureBundledFfmpeg();
        using var sandbox = new Sandbox();
        var clip = await sandbox.ClipAsync("Release - Sep-26-2026 - 14-35-07.mp4");
        var info = new FileInfo(clip);
        File.WriteAllText(sandbox.ProbeEntryPath(clip), ReleaseProbeEntry
            .Replace("{0}", info.Length.ToString(CultureInfo.InvariantCulture))
            .Replace("{1}", info.LastWriteTimeUtc.Ticks.ToString(CultureInfo.InvariantCulture)));

        var service = new MediaProbeService(sandbox.MediaCache);
        var stub = service.CreateLibraryStub(clip);
        Assert.Equal(TimeSpan.FromSeconds(1), stub.Duration);
        Assert.Equal(2, stub.Tracks.Count);
        var probe = await service.ProbeMetadataDetailedAsync(clip);
        Assert.True(probe.FromCache); // No ffprobe: the release's entry is this build's entry.
    }

    [Fact]
    public void LibraryCacheSurvivesRestartAndBothBuildsKeepIt()
    {
        using var sandbox = new Sandbox();
        var clips = Enumerable.Range(0, 3).Select(i => Path.Combine(sandbox.Library, $"Clip {i}.mp4")).ToArray();
        var hydrated = clips.Select(path => new CachedClipState(new MediaFileInfo(Path.GetFileNameWithoutExtension(path), path, DateTimeOffset.UtcNow.AddMinutes(-clips.Length),
            TimeSpan.FromSeconds(30), 1000, "", [new MediaTrackInfo(0, "video", "h264", "Video"), new MediaTrackInfo(1, "audio", "aac", "Game")], 2560, 1440, 90), null, null)).ToArray();

        // "Release" writes it (same store code, same user_version: 2)...
        new LibraryCacheStore(sandbox.Database).Save(sandbox.Library, hydrated);
        Assert.Equal(2, UserVersion(sandbox.Database));

        // ...this build restarts on it: nothing dropped, nothing at 0:00...
        var restored = new LibraryCacheStore(sandbox.Database).Load(sandbox.Library);
        Assert.Equal(3, restored.Count);
        var cards = restored.Select(state => new ClipCardViewModel(state, sandbox.Library)).ToArray();
        Assert.All(cards, card => Assert.True(card.IsHydrated));

        // ...writes it back, and the release (and a further restart) still keep it.
        new LibraryCacheStore(sandbox.Database).Save(sandbox.Library, cards.Select(card => card.ToCachedState()).ToArray());
        Assert.Equal(2, UserVersion(sandbox.Database));
        var again = new LibraryCacheStore(sandbox.Database).Load(sandbox.Library);
        Assert.Equal(3, again.Count);
        Assert.All(again, state => Assert.Equal(TimeSpan.FromSeconds(30), state.Media.Duration));
    }
}
