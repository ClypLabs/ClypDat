using ClypDat.App.Services;
using Xunit;

namespace ClypDat.GameDetection.Tests;

public sealed class StorageJanitorTests
{
    [Fact]
    public void DeleteFilesOlderThan_PreservesCurrentProcessFiles()
    {
        var root = Path.Combine(Path.GetTempPath(), $"clypdat-janitor-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        var oldFile = Path.Combine(root, "old.mp4");
        var currentFile = Path.Combine(root, "current.mp4");

        try
        {
            File.WriteAllText(oldFile, "old");
            File.WriteAllText(currentFile, "current");
            var cutoff = DateTime.UtcNow;
            File.SetLastWriteTimeUtc(oldFile, cutoff.AddSeconds(-2));
            File.SetLastWriteTimeUtc(currentFile, cutoff.AddSeconds(2));

            var removed = StorageJanitor.DeleteFilesOlderThan(root, cutoff);

            Assert.Equal(1, removed);
            Assert.False(File.Exists(oldFile));
            Assert.True(File.Exists(currentFile));
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }
}
