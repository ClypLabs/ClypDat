using ClypDat.App.Services;
using Microsoft.Data.Sqlite;
using Xunit;

namespace ClypDat.App.Tests;

public sealed class LibraryCacheOverlayManifestTests
{
    [Fact]
    public void SavedRowsLeaveTheOverlayManifestInTheSidecar()
    {
        SQLitePCL.raw.SetProvider(new SQLitePCL.SQLite3Provider_winsqlite3());
        var folder = Path.Combine(Path.GetTempPath(), "clypdat-library-cache-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            var clip = Path.Combine(folder, "Clips", "Game", "clip.mp4");
            Directory.CreateDirectory(Path.GetDirectoryName(clip)!);
            File.WriteAllBytes(clip, new byte[16]);
            var media = new MediaProbeService().CreateLibraryStub(clip);
            var info = new ClipInfo("Game", "Enemy Team Wiped", "Enemy Team Wiped", OverlayManifest: ClipOverlayManifest.Empty);
            var store = new LibraryCacheStore(Path.Combine(folder, "library-cache.db"));

            store.Save(folder, [new CachedClipState(media, info, null, 123456)]);
            var restored = Assert.Single(store.Load(folder));

            Assert.Null(restored.ClipInfo!.OverlayManifest);
            Assert.Equal("Enemy Team Wiped", restored.ClipInfo.AutoClipEventType);
            // The card shows this total without the manifest to recount it from.
            Assert.Equal(123456, restored.TotalSizeBytes);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(folder, recursive: true);
        }
    }
}
