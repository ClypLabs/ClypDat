using ClypDat.App.Services;
using Xunit;

namespace ClypDat.App.Tests;

public sealed class ClipFileNamingTests
{
    [Fact]
    public void BatchNaming_UsesConfiguredStemAndCollisionSuffix()
    {
        var timestamp = new DateTime(2026, 9, 1, 15, 20, 33);
        var fileName = ClipFileNaming.BuildFileName("Round", timestamp, "mp4", ClipFileNaming.StandardScheme, string.Empty, "Counter-Strike 2");
        var root = Path.Combine(Path.GetTempPath(), "ClypDat-ClipFileNamingTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            File.WriteAllText(Path.Combine(root, fileName), string.Empty);
            var resolved = ClipFileNaming.BuildUniquePath(root, fileName);

            Assert.EndsWith(" (2).mp4", resolved, StringComparison.Ordinal);
            Assert.Equal("Round - Sep-01-2026 - 15-20-33 (2)", Path.GetFileNameWithoutExtension(resolved));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
