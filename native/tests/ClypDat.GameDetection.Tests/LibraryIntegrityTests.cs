using Avalonia.Controls.Presenters;
using ClypDat.App.Controls;
using ClypDat.App.Services;
using ClypDat.App.ViewModels;
using Xunit;

namespace ClypDat.GameDetection.Tests;

public sealed class LibraryIntegrityTests
{
    [Fact]
    public void CardPanel_ResolvesClipFromItemsControlContentPresenter()
    {
        var clip = new ClipCardViewModel(CreateMedia(Path.Combine(Path.GetTempPath(), "clip.mp4")), Path.GetTempPath());
        var presenter = new ContentPresenter { Content = clip };

        Assert.Same(clip, LibraryCardPanel.ResolveClip(presenter));
    }

    [Fact]
    public void LibraryCache_CollapsesDuplicatePathsCaseInsensitively()
    {
        var databasePath = Path.Combine(Path.GetTempPath(), "clypdat-cache-test-" + Guid.NewGuid().ToString("N") + ".db");
        var libraryRoot = Path.Combine(Path.GetTempPath(), "clypdat-library-test-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(libraryRoot);
            SQLitePCL.raw.SetProvider(new SQLitePCL.SQLite3Provider_winsqlite3());
            var store = new LibraryCacheStore(databasePath);
            var entries = new[]
            {
                new CachedClipState(CreateMedia(Path.Combine(libraryRoot, "Clip.mp4")), null, null),
                new CachedClipState(CreateMedia(Path.Combine(libraryRoot, "clip.mp4")), null, null)
            };

            store.Save(libraryRoot, entries);

            Assert.Single(store.Load(libraryRoot));
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            foreach (var path in new[] { databasePath, databasePath + "-wal", databasePath + "-shm" })
            {
                if (File.Exists(path)) File.Delete(path);
            }
            if (Directory.Exists(libraryRoot)) Directory.Delete(libraryRoot, recursive: true);
        }
    }

    private static MediaFileInfo CreateMedia(string path) => new(
        Path.GetFileName(path),
        path,
        DateTimeOffset.UtcNow,
        TimeSpan.FromSeconds(10),
        1,
        string.Empty,
        Array.Empty<MediaTrackInfo>(),
        1920,
        1080,
        60);
}
