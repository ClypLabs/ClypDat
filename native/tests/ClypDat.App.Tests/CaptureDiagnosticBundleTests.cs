using System.IO.Compression;
using ClypDat.App.Services;
using Xunit;

namespace ClypDat.App.Tests;

public sealed class CaptureDiagnosticBundleTests
{
    [Fact]
    public void WorkerLogComesFromAppDataRootRatherThanLogFolder()
    {
        var previous = ClypDat.Core.Settings.AppDataPaths.ProductFolderName;
        ClypDat.Core.Settings.AppDataPaths.ConfigureProductFolder("ClypDat-DiagnosticsTest-" + Guid.NewGuid().ToString("N"));
        var root = ClypDat.Core.Settings.AppDataPaths.Root;
        try
        {
            Directory.CreateDirectory(root);
            File.WriteAllText(Path.Combine(root, "capture-worker.log"), "worker evidence");
            var bundle = CaptureDiagnosticBundle.Create(null, null, Path.Combine(root, "logs"), DateTime.Now);
            using var archive = ZipFile.OpenRead(bundle);
            Assert.Equal("worker evidence", ReadEntry(archive, "logs/capture-worker.log"));
        }
        finally
        {
            ClypDat.Core.Settings.AppDataPaths.ConfigureProductFolder(previous);
            Directory.Delete(root, true);
        }
    }

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

    [Fact]
    public void Create_FullExportKeepsHistoryAndRedactsUtf8ContentPastReaderBufferBoundary()
    {
        var root = Path.Combine(Path.GetTempPath(), "ClypDat-CaptureDiagnosticBundleTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var log = Path.Combine(root, "clypdat-history.log");
        var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var content = string.Join(Environment.NewLine, Enumerable.Repeat("history line", 70_000)) +
            Environment.NewLine + new string('x', 4090) + userProfile + "-秘密-token";
        File.WriteAllText(log, content);

        try
        {
            var bundle = CaptureDiagnosticBundle.Create(null, null, root, DateTime.Now);
            using var archive = ZipFile.OpenRead(bundle);
            var exported = ReadEntry(archive, "logs/clypdat-history.log");
            Assert.StartsWith("history line" + Environment.NewLine, exported, StringComparison.Ordinal);
            Assert.Contains(new string('x', 4090) + "%USERPROFILE%-秘密-token", exported, StringComparison.Ordinal);
            Assert.Contains("%USERPROFILE%-秘密-token", exported, StringComparison.Ordinal);
            Assert.DoesNotContain(userProfile, exported, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void CreateForUpload_RetainsRecentByteLimitAndTruncationNotice()
    {
        var root = Path.Combine(Path.GetTempPath(), "ClypDat-CaptureDiagnosticBundleTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var log = Path.Combine(root, "clypdat-upload.log");
        File.WriteAllText(log, new string('a', 600_000) + Environment.NewLine + "recent evidence");

        try
        {
            var bundle = CaptureDiagnosticBundle.Create(null, null, root, DateTime.Now, recentOnly: true);
            using var archive = ZipFile.OpenRead(bundle);
            var exported = ReadEntry(archive, "logs/clypdat-upload.log");
            Assert.Contains("[Earlier log content omitted from upload]", exported, StringComparison.Ordinal);
            Assert.EndsWith("recent evidence", exported, StringComparison.Ordinal);
            Assert.DoesNotContain(new string('a', 100), exported, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void ClypDatCrashEvents_KeepsOnlyClypDatRecords()
    {
        var text = string.Join("\r\n",
            "Event[0]:", "  Source: Application Error", "Description:", "Faulting application name: ClypDat.exe", "Faulting module name: libvlccore.dll",
            "Event[1]:", "  Source: Application Error", "Description:", "Faulting application name: other.exe",
            "Event[2]:", "  Source: .NET Runtime", "Description:", "Application: ClypDatRecorder.exe", "Exception Info: System.IO.IOException");

        var events = CaptureDiagnosticBundle.ClypDatCrashEvents(text);

        Assert.Equal(2, events.Count);
        Assert.Contains("libvlccore.dll", events[0], StringComparison.Ordinal);
        Assert.Contains("ClypDatRecorder.exe", events[1], StringComparison.Ordinal);
        Assert.DoesNotContain(events, record => record.Contains("other.exe", StringComparison.Ordinal));
    }

    // A tester's bundle: another program's crash whose stack ran through a
    // folder named ClypDat, with wevtutil's NUL-terminated fields.
    [Fact]
    public void ClypDatCrashEvents_IgnoresOtherProgramsMentioningClypDatAndDropsNuls()
    {
        var text = string.Join("\r\n",
            "Event[201]", "  Source: .NET Runtime", "  Task: None\0", "Description: ", "Application: dotnet.exe",
            @"   at Program.<Main>$(String[] args) in D:\Development\ClypDat\clypdat-app\.local\audit\Program.cs:line 32", "\0",
            "Event[202]", "  Source: Application Hang", "  Task: None\0", "Description: ",
            "The program ClypDat.exe version 2.2.0.0 stopped interacting with Windows and was closed.");

        var events = CaptureDiagnosticBundle.ClypDatCrashEvents(text);

        var hang = Assert.Single(events);
        Assert.Contains("stopped interacting", hang, StringComparison.Ordinal);
        Assert.DoesNotContain('\0', hang);
    }

    [Fact]
    public void Create_IncludesCrashEvents()
    {
        var root = Path.Combine(Path.GetTempPath(), "ClypDat-CaptureDiagnosticBundleTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var bundle = CaptureDiagnosticBundle.Create(null, null, root, DateTime.Now);
            using var archive = ZipFile.OpenRead(bundle);
            Assert.False(string.IsNullOrWhiteSpace(ReadEntry(archive, "crash-events.txt")));
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
