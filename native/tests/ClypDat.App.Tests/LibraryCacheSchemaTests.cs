using ClypDat.App.Services;
using Microsoft.Data.Sqlite;
using Xunit;

namespace ClypDat.App.Tests;

public sealed class LibraryCacheSchemaTests
{
    [Fact]
    public void VersionOneCacheIsDiscarded()
    {
        SQLitePCL.raw.SetProvider(new SQLitePCL.SQLite3Provider_winsqlite3());
        var folder = Path.Combine(Path.GetTempPath(), "clypdat-library-cache-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        var databasePath = Path.Combine(folder, "library-cache.db");
        try
        {
            using (var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = databasePath }.ToString()))
            {
                connection.Open();
                using var command = connection.CreateCommand();
                command.CommandText = """
                    CREATE TABLE library_snapshots (library_root TEXT PRIMARY KEY, updated_utc_ticks INTEGER NOT NULL);
                    CREATE TABLE library_entries (library_root TEXT NOT NULL, path TEXT NOT NULL, created_utc_ticks INTEGER NOT NULL, payload_json TEXT NOT NULL);
                    PRAGMA user_version = 1;
                    """;
                command.ExecuteNonQuery();
                command.CommandText = "INSERT INTO library_entries VALUES ($root, 'stale.mp4', 1, '{}');";
                command.Parameters.AddWithValue("$root", Path.GetFullPath(folder).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
                command.ExecuteNonQuery();
            }

            var store = new LibraryCacheStore(databasePath);
            Assert.Empty(store.Load(folder));

            using (var verify = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = databasePath }.ToString()))
            {
                verify.Open();
                using var versionCommand = verify.CreateCommand();
                versionCommand.CommandText = "PRAGMA user_version;";
                Assert.Equal(2L, (long)versionCommand.ExecuteScalar()!);
                versionCommand.CommandText = "SELECT COUNT(*) FROM library_entries;";
                Assert.Equal(0L, (long)versionCommand.ExecuteScalar()!);
            }
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(folder, recursive: true);
        }
    }
}
