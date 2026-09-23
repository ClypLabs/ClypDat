using System.IO.MemoryMappedFiles;
using System.Text.Json;
using ClypDat.App.Services;
using ClypDat.Core.Settings;
using Xunit;

namespace ClypDat.App.Tests;

public sealed class SecurityRuntimeRegressionTests
{
    [Fact]
    public void DeletingClipCannotDeleteAnotherClipOrItsOverlay()
    {
        using var fixture = new Files();
        var clip = Path.Combine(fixture.Root, "Clips", "a.mp4");
        var other = fixture.Write("Clips/b.mp4", "other video");
        var otherCamera = LibraryLayout.SidecarPath(fixture.Root, other, ".camera");
        Directory.CreateDirectory(otherCamera);
        var segment = Path.Combine(otherCamera, "0.mp4");
        File.WriteAllText(segment, "other camera");
        ClipInfoSidecar.Save(fixture.Root, clip, new ClipInfo(null, null, OverlayManifest: new(
            ClipOverlayManifest.CurrentVersion,
            new ClipOverlayLayer("Camera", true, AssetPath: Path.GetRelativePath(fixture.Root, other),
                Assets: [new(Path.GetRelativePath(fixture.Root, segment), 0, 1)]))));

        ClipInfoSidecar.Delete(fixture.Root, clip);

        Assert.Equal("other video", File.ReadAllText(other));
        Assert.Equal("other camera", File.ReadAllText(segment));
    }

    [Fact]
    public void RenameReplacesStaleMetadataWithLiveMetadata()
    {
        using var fixture = new Files();
        var source = Path.Combine(fixture.Root, "Clips", "source.mp4");
        var destination = Path.Combine(fixture.Root, "Clips", "destination.mp4");
        var sourceInfo = LibraryLayout.SidecarPath(fixture.Root, source, ".info.json");
        var destinationInfo = LibraryLayout.SidecarPath(fixture.Root, destination, ".info.json");
        Directory.CreateDirectory(Path.GetDirectoryName(sourceInfo)!);
        File.WriteAllText(sourceInfo, "{\"FileTitle\":\"Live title\"}");
        File.WriteAllText(destinationInfo, "{\"FileTitle\":\"Stale title\"}");
        var sourceEdit = LibraryLayout.SidecarPath(fixture.Root, source, ".json");
        var destinationEdit = LibraryLayout.SidecarPath(fixture.Root, destination, ".json");
        File.WriteAllText(sourceEdit, "live trim");
        File.WriteAllText(destinationEdit, "stale trim");

        LibraryLayout.MoveSidecars(fixture.Root, source, destination);

        Assert.Equal("Live title", ClipInfoSidecar.Load(fixture.Root, destination)!.FileTitle);
        Assert.Equal("live trim", File.ReadAllText(destinationEdit));
        Assert.False(File.Exists(sourceInfo));
    }

    [Theory]
    [InlineData("https://i.scdn.co/image/cover", true)]
    [InlineData("http://i.scdn.co/image/cover", false)]
    [InlineData("https://i.scdn.co.attacker.invalid/cover", false)]
    [InlineData("https://user@i.scdn.co/image/cover", false)]
    [InlineData("https://i.scdn.co:444/image/cover", false)]
    public void ArtworkOnlyAcceptsSpotifyHttps(string url, bool trusted) =>
        Assert.Equal(trusted, SpotifyCoverArtStore.IsTrustedArtUrl(url));

    [Fact]
    public void RenameWithoutOptionalMetadataDoesNotAdoptStaleEditsOrMusic()
    {
        using var fixture = new Files();
        var source = Path.Combine(fixture.Root, "Clips", "source.mp4");
        var destination = Path.Combine(fixture.Root, "Clips", "destination.mp4");
        ClipInfoSidecar.Save(fixture.Root, source, new ClipInfo(null, null, FileTitle: "Live"));
        var edits = LibraryLayout.SidecarPath(fixture.Root, destination, ".json");
        File.WriteAllText(edits, "stale edits");
        var music = SpotifyTimelineSidecar.PathFor(fixture.Root, destination);
        File.WriteAllText(music, "stale music");

        LibraryLayout.MoveSidecars(fixture.Root, source, destination);

        Assert.False(File.Exists(edits));
        Assert.False(File.Exists(music));
        Assert.Equal("Live", ClipInfoSidecar.Load(fixture.Root, destination)!.FileTitle);
    }

    [Fact]
    public void ArtworkRejectsOutsideAndTraversalPaths()
    {
        using var fixture = new Files();
        var clip = Path.Combine(fixture.Root, "Clips", "clip.mp4");
        Assert.Null(SpotifyCoverArtStore.TrustedArtPath(fixture.Root, clip, @"\\attacker.invalid\share\cover.jpg"));
        Assert.Null(SpotifyCoverArtStore.TrustedArtPath(fixture.Root, clip, Path.Combine(fixture.Root, "secret.txt")));
        Assert.False(SpotifyCoverArtStore.IsArchivedArtPath(fixture.Root,
            Path.Combine(SpotifyCoverArtStore.ArchiveRoot(fixture.Root), "..", "secret.jpg")));
    }

    [Fact]
    public void SharedFramesRejectInProgressAndRecycledSlots()
    {
        using var map = MemoryMappedFile.CreateNew(null, DetectorFrameCodec.SlotBytes * 3L);
        using var view = map.CreateViewAccessor();
        var image = new GrayDetectorImage(2, 2, [1, 2, 3, 4]);
        var frame = new DetectorFrameSnapshot(DateTime.UtcNow, image, image, image);
        var first = DetectorFrameCodec.Write(view, 0, frame);
        Assert.Equal(frame.CapturedUtc, DetectorFrameCodec.Read(view, 0, first).CapturedUtc);
        var second = DetectorFrameCodec.Write(view, 0, frame);
        Assert.NotEqual(first, second);
        Assert.Throws<InvalidDataException>(() => DetectorFrameCodec.Read(view, 0, first));
        view.Write(0, second + 1); // Writer has acquired the slot, but not published it.
        Assert.Throws<InvalidDataException>(() => DetectorFrameCodec.Read(view, 0));
    }

    [Fact]
    public void ConcurrentSavesProduceCompleteSettingsAndBackup()
    {
        using var fixture = new SettingsFiles();
        Parallel.For(0, 24, index => Assert.True(AppSettingsStore.Save(new AppSettings
        {
            FontFamilyName = "snapshot-" + index,
            ReplayBitrateDefault15Applied = true,
            ReplayH264DefaultApplied = true
        }), AppSettingsStore.LastSaveError));
        using var settings = JsonDocument.Parse(File.ReadAllText(AppSettingsStore.SettingsPath));
        using var backup = JsonDocument.Parse(File.ReadAllText(AppSettingsStore.SettingsPath + ".backup.json"));
        Assert.StartsWith("snapshot-", settings.RootElement.GetProperty("FontFamilyName").GetString());
        Assert.StartsWith("snapshot-", backup.RootElement.GetProperty("FontFamilyName").GetString());
        Assert.Empty(Directory.EnumerateFiles(AppDataPaths.Root, "*.tmp"));
    }

    [Fact]
    public void RecoveredSettingsUseNormalMigrationAndValidation()
    {
        using var fixture = new SettingsFiles();
        Directory.CreateDirectory(AppDataPaths.Root);
        File.WriteAllText(AppSettingsStore.SettingsPath, "{broken");
        File.WriteAllText(AppSettingsStore.SettingsPath + ".backup.json",
            "{\"ReplayBitrateMbps\":999,\"ReplayBitrateDefault15Applied\":true,\"FontFamilyName\":\"\"}");
        var recovered = AppSettingsStore.Load();
        Assert.Equal(100, recovered.ReplayBitrateMbps);
        Assert.Equal("Inter", recovered.FontFamilyName);
        Assert.NotNull(AppSettingsStore.PreservedUnreadablePath);
    }

    [Fact]
    public void SharingViolationDoesNotOverwriteOrTreatSettingsAsCorrupt()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var fixture = new SettingsFiles();
        Assert.True(AppSettingsStore.Save(new AppSettings { FontFamilyName = "Keep me" }));
        using (var held = new FileStream(AppSettingsStore.SettingsPath, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            AppSettingsStore.Load();
            Assert.NotNull(AppSettingsStore.LastLoadError);
            Assert.Null(AppSettingsStore.PreservedUnreadablePath);
            Assert.False(AppSettingsStore.Save(new AppSettings { FontFamilyName = "Replacement" }));
        }
        Assert.Equal("Keep me", AppSettingsStore.Load().FontFamilyName);
    }

    private sealed class Files : IDisposable
    {
        public string Root { get; } = Path.Combine(AppContext.BaseDirectory, "SecurityRuntimeFixtures", Guid.NewGuid().ToString("N"));
        public string Write(string relative, string text)
        {
            var path = Path.Combine(Root, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, text);
            return path;
        }
        public void Dispose() { if (Directory.Exists(Root)) Directory.Delete(Root, true); }
    }

    private sealed class SettingsFiles : IDisposable
    {
        private readonly string _previous = AppDataPaths.ProductFolderName;
        private readonly string _root;
        public SettingsFiles()
        {
            AppDataPaths.ConfigureProductFolder("ClypDat-SecurityTests-" + Guid.NewGuid().ToString("N"));
            _root = AppDataPaths.Root;
            AppSettingsStore.Load();
        }
        public void Dispose()
        {
            AppDataPaths.ConfigureProductFolder(_previous);
            AppSettingsStore.Load();
            if (Directory.Exists(_root)) Directory.Delete(_root, true);
        }
    }
}
