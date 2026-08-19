using System.Text.Json;
using ClypDat.Core.Settings;
using Microsoft.Data.Sqlite;

namespace ClypDat.App.Services;

// Local, aggregate index of a library. MediaProbeService's per-file caches
// avoid ffprobe/ffmpeg work, but still leave startup reading every remote
// file and sidecar before a card can render. This cache is intentionally
// disposable: the library remains the authority and RefreshLibraryAsync
// reconciles it after every restore.
internal sealed class LibraryCacheStore
{
    private const int SchemaVersion = 1;
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = false };
    private readonly string _databasePath;

    public LibraryCacheStore()
        : this(null)
    {
    }

    internal LibraryCacheStore(string? databasePath)
    {
        _databasePath = databasePath ?? Path.Combine(ClypDat.Core.Settings.AppDataPaths.Root, "library-cache.db");
    }

    public IReadOnlyList<CachedClipState> Load(string libraryRoot)
    {
        if (string.IsNullOrWhiteSpace(libraryRoot)) return Array.Empty<CachedClipState>();

        try
        {
            using var connection = Open();
            EnsureSchema(connection);
            using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT payload_json
                FROM library_entries
                WHERE library_root = $root
                ORDER BY created_utc_ticks DESC, path COLLATE NOCASE ASC;
                """;
            command.Parameters.AddWithValue("$root", NormalizeRoot(libraryRoot));

            using var reader = command.ExecuteReader();
            var entries = new List<CachedClipState>();
            while (reader.Read())
            {
                try
                {
                    var entry = JsonSerializer.Deserialize<CachedClipState>(reader.GetString(0), JsonOptions);
                    if (entry is not null && !string.IsNullOrWhiteSpace(entry.Media.Path)) entries.Add(entry);
                }
                catch
                {
                    // One bad row must not make the whole library disappear.
                }
            }

            return entries;
        }
        catch (Exception error)
        {
            AppLog.Error("Library cache read failed; falling back to disk scan.", error);
            return Array.Empty<CachedClipState>();
        }
    }

    public void Save(string libraryRoot, IReadOnlyList<CachedClipState> entries)
    {
        if (string.IsNullOrWhiteSpace(libraryRoot)) return;

        try
        {
            var uniqueEntries = entries
                .GroupBy(entry => entry.Media.Path, StringComparer.OrdinalIgnoreCase)
                .Select(group => group.First())
                .ToArray();
            if (uniqueEntries.Length != entries.Count)
            {
                AppLog.Info($"Library cache: collapsed {entries.Count - uniqueEntries.Length} duplicate path(s) before save.");
            }

            using var connection = Open();
            EnsureSchema(connection);
            using var transaction = connection.BeginTransaction();
            var root = NormalizeRoot(libraryRoot);

            using (var delete = connection.CreateCommand())
            {
                delete.Transaction = transaction;
                delete.CommandText = "DELETE FROM library_entries WHERE library_root = $root;";
                delete.Parameters.AddWithValue("$root", root);
                delete.ExecuteNonQuery();
            }

            using (var insert = connection.CreateCommand())
            {
                insert.Transaction = transaction;
                insert.CommandText = """
                    INSERT INTO library_entries (library_root, path, created_utc_ticks, payload_json)
                    VALUES ($root, $path, $created, $payload);
                    """;
                var rootParameter = insert.Parameters.Add("$root", SqliteType.Text);
                var pathParameter = insert.Parameters.Add("$path", SqliteType.Text);
                var createdParameter = insert.Parameters.Add("$created", SqliteType.Integer);
                var payloadParameter = insert.Parameters.Add("$payload", SqliteType.Text);

                foreach (var entry in uniqueEntries)
                {
                    rootParameter.Value = root;
                    pathParameter.Value = entry.Media.Path;
                    createdParameter.Value = entry.Media.CreatedAt.UtcTicks;
                    payloadParameter.Value = JsonSerializer.Serialize(entry, JsonOptions);
                    insert.ExecuteNonQuery();
                }
            }

            using (var snapshot = connection.CreateCommand())
            {
                snapshot.Transaction = transaction;
                snapshot.CommandText = """
                    INSERT INTO library_snapshots (library_root, updated_utc_ticks)
                    VALUES ($root, $updated)
                    ON CONFLICT(library_root) DO UPDATE SET updated_utc_ticks = excluded.updated_utc_ticks;
                    """;
                snapshot.Parameters.AddWithValue("$root", root);
                snapshot.Parameters.AddWithValue("$updated", DateTime.UtcNow.Ticks);
                snapshot.ExecuteNonQuery();
            }

            transaction.Commit();
        }
        catch (Exception error)
        {
            AppLog.Error("Library cache write failed; existing snapshot was kept.", error);
        }
    }

    private SqliteConnection Open()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_databasePath)!);
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = _databasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Shared
        }.ToString());
        connection.Open();
        return connection;
    }

    private static void EnsureSchema(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA journal_mode=WAL; PRAGMA synchronous=NORMAL;";
        command.ExecuteNonQuery();

        command.CommandText = "PRAGMA user_version;";
        var currentVersion = Convert.ToInt32(command.ExecuteScalar());
        if (currentVersion != 0 && currentVersion != SchemaVersion)
        {
            command.CommandText = "DROP TABLE IF EXISTS library_entries; DROP TABLE IF EXISTS library_snapshots;";
            command.ExecuteNonQuery();
        }

        command.CommandText = """
            CREATE TABLE IF NOT EXISTS library_snapshots (
                library_root TEXT PRIMARY KEY COLLATE NOCASE,
                updated_utc_ticks INTEGER NOT NULL
            );
            CREATE TABLE IF NOT EXISTS library_entries (
                library_root TEXT NOT NULL COLLATE NOCASE,
                path TEXT NOT NULL COLLATE NOCASE,
                created_utc_ticks INTEGER NOT NULL,
                payload_json TEXT NOT NULL,
                PRIMARY KEY (library_root, path)
            );
            CREATE INDEX IF NOT EXISTS ix_library_entries_restore
                ON library_entries (library_root, created_utc_ticks DESC);
            PRAGMA user_version = 1;
            """;
        command.ExecuteNonQuery();
    }

    private static string NormalizeRoot(string root) =>
        Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
}

internal sealed record CachedClipState(MediaFileInfo Media, ClipInfo? ClipInfo, ClipEditSettings? ClipEdit);
