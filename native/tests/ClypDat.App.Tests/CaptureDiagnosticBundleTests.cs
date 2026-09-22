using System.IO.Compression;
using ClypDat.App.Services;
using Xunit;

namespace ClypDat.App.Tests;

public sealed class CaptureDiagnosticBundleTests
{
    [Fact]
    public void Create_WhenOneLogIsExclusivelyLocked_ExportsReadableLogsAndRecordsWarning()
    {
        var root = Path.Combine(Path.GetTempPath(), "ClypDat-CaptureDiagnosticBundleTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var readableLog = Path.Combine(root, "clypdat-readable.log");
        var lockedLog = Path.Combine(root, "clypdat-locked.log");
        File.WriteAllText(readableLog, "readable log");
        File.WriteAllText(lockedLog, "locked log");

        try
        {
            using (var lockHandle = new FileStream(lockedLog, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            {
                var bundle = CaptureDiagnosticBundle.Create(null, null, root, new DateTime(2026, 9, 3, 12, 0, 0));

                using var archive = ZipFile.OpenRead(bundle);
                Assert.Equal("readable log", ReadEntry(archive, "logs/clypdat-readable.log"));
                Assert.Contains("clypdat-locked.log", ReadEntry(archive, "bundle-warnings.txt"), StringComparison.Ordinal);
            }
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Create_UsesUniqueFinalPathsAndLeavesNoPartialArchives()
    {
        var root = Path.Combine(Path.GetTempPath(), "ClypDat-CaptureDiagnosticBundleTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);

        try
        {
            var now = new DateTime(2026, 9, 3, 12, 0, 0);
            var first = CaptureDiagnosticBundle.Create(null, null, root, now);
            var second = CaptureDiagnosticBundle.Create(null, null, root, now);

            Assert.NotEqual(first, second);
            Assert.True(File.Exists(first));
            Assert.True(File.Exists(second));
            Assert.Empty(Directory.EnumerateFiles(root, "*.partial"));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static string ReadEntry(ZipArchive archive, string name)
    {
        var entry = archive.GetEntry(name);
        Assert.NotNull(entry);
        using var reader = new StreamReader(entry!.Open());
        return reader.ReadToEnd();
    }
}
