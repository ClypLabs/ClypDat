using System.Text.Json;
using ClypDat.App.Services;
using Microsoft.Data.Sqlite;
using Xunit;

namespace ClypDat.App.Tests;

public sealed class RecorderStagingLibraryTests
{
    [Theory]
    [InlineData("clip.mp4.partial.mp4")]
    [InlineData("clip.mkv.partial.mkv")]
    [InlineData("clip.MP4.PARTIAL.mp4")]
    [InlineData("clip.mKv.pArTiAl.MKV")]
    public void RecorderStagingFilesAreNotVideoFiles(string name)
    {
        Assert.False(MediaProbeService.IsVideoFile(Path.Combine(@"D:\Videos", name)));
    }

    [Theory]
    [InlineData("partial.mp4")]
    [InlineData("partial-footage.mkv")]
    [InlineData("clip.mp4.partial.mov")]
    [InlineData("clip.partial.mp4")]
    public void OrdinaryNamesContainingPartialRemainVideoFiles(string name)
    {
        Assert.True(MediaProbeService.IsVideoFile(Path.Combine(@"D:\Videos", name)));
    }

    [Fact]
    public void DiscoveryHidesStagingFileAndFindsCompletedRename()
    {
        var folder = CreateTemporaryDirectory();
        try
        {
            var staging = Path.Combine(folder, "capture.mp4.partial.mp4");
            var completed = Path.Combine(folder, "capture.mp4");
            File.WriteAllBytes(staging, [1]);

            Assert.Empty(new MediaProbeService().EnumerateVideos(folder));

            File.Move(staging, completed);

            Assert.Equal([completed], new MediaProbeService().EnumerateVideos(folder).Select(file => file.FullName));
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    [Fact]
    public void CacheSaveAndRestoreExcludeRecorderStagingEntries()
    {
        SQLitePCL.raw.SetProvider(new SQLitePCL.SQLite3Provider_winsqlite3());
        var folder = CreateTemporaryDirectory();
        try
        {
            var libraryRoot = Path.Combine(folder, "library");
            var database = Path.Combine(folder, "cache.db");
            Directory.CreateDirectory(libraryRoot);
            var cache = new LibraryCacheStore(database);
            var staged = CreateEntry(Path.Combine(libraryRoot, "capture.mp4.partial.mp4"));
            var completed = CreateEntry(Path.Combine(libraryRoot, "capture.mp4"));

            cache.Save(libraryRoot, [staged, completed]);
            Assert.Equal(1, ReadCachedEntryCount(database));
            Assert.Equal([completed.Media.Path], cache.Load(libraryRoot).Select(entry => entry.Media.Path));

            InsertLegacyCacheEntry(database, libraryRoot, staged);

            Assert.Equal([completed.Media.Path], cache.Load(libraryRoot).Select(entry => entry.Media.Path));
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(folder, recursive: true);
        }
    }

    private static CachedClipState CreateEntry(string path) => new(
        new MediaFileInfo(Path.GetFileNameWithoutExtension(path), path, DateTimeOffset.UtcNow,
            TimeSpan.Zero, 1, string.Empty, [], 0, 0, 0),
        null,
        null);

    private static void InsertLegacyCacheEntry(string database, string libraryRoot, CachedClipState entry)
    {
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = database }.ToString());
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO library_entries (library_root, path, created_utc_ticks, payload_json)
            VALUES ($root, $path, $created, $payload);
            """;
        command.Parameters.AddWithValue("$root", Path.GetFullPath(libraryRoot).TrimEnd(Path.DirectorySeparatorChar));
        command.Parameters.AddWithValue("$path", entry.Media.Path);
        command.Parameters.AddWithValue("$created", entry.Media.CreatedAt.UtcTicks);
        command.Parameters.AddWithValue("$payload", JsonSerializer.Serialize(entry));
        command.ExecuteNonQuery();
    }

    private static int ReadCachedEntryCount(string database)
    {
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = database }.ToString());
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM library_entries;";
        return Convert.ToInt32(command.ExecuteScalar());
    }

    private static string CreateTemporaryDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), $"ClypDat-staging-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        return path;
    }
}
