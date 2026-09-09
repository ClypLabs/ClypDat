using ClypDat.App.Services;
using Xunit;

namespace ClypDat.App.Tests;

public sealed class ClipOverlayManifestTests
{
    [Fact]
    public void RelativeAsset_ResolvesInsideLibrary()
    {
        var root = Path.Combine(Path.GetTempPath(), "clypdat-overlay-test", Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(Path.Combine(root, ".clipinfo"));
            var asset = Path.Combine(root, ".clipinfo", "camera.mp4");
            File.WriteAllBytes(asset, []);

            Assert.Equal(asset, ClipOverlayManifest.ResolveAssetPath(root, ".clipinfo/camera.mp4"));
            Assert.True(ClipOverlayManifest.IsUsable(root, new ClipOverlayLayer("Camera", true, AssetPath: ".clipinfo/camera.mp4")));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [Theory]
    [InlineData("../outside.mp4")]
    [InlineData("C:\\outside.mp4")]
    public void EscapedAsset_IsRejected(string asset) =>
        Assert.Null(ClipOverlayManifest.ResolveAssetPath(Path.GetTempPath(), asset));

    [Fact]
    public void Delete_AlsoDeletesOwnedOverlayAsset()
    {
        var root = Path.Combine(Path.GetTempPath(), "clypdat-overlay-test", Guid.NewGuid().ToString("N"));
        var clip = Path.Combine(root, "Clips", "Game", "clip.mp4");
        var asset = Path.Combine(root, ".clipinfo", "overlays", "camera.mp4");
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(asset)!);
            File.WriteAllBytes(asset, []);
            ClipInfoSidecar.Save(root, clip, new ClipInfo(null, null, OverlayManifest: new ClipOverlayManifest(
                ClipOverlayManifest.CurrentVersion, new ClipOverlayLayer("Camera", true, AssetPath: ".clipinfo/overlays/camera.mp4"))));

            ClipInfoSidecar.Delete(root, clip);

            Assert.False(File.Exists(asset));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [Fact]
    public void TimestampedAssets_MustAllExistAndAreDeleted()
    {
        var root = Path.Combine(Path.GetTempPath(), "clypdat-overlay-test", Guid.NewGuid().ToString("N"));
        var clip = Path.Combine(root, "Clips", "Game", "clip.mp4");
        var first = Path.Combine(root, ".clipinfo", "overlays", "0.mp4");
        var second = Path.Combine(root, ".clipinfo", "overlays", "1.mp4");
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(first)!);
            File.WriteAllBytes(first, []); File.WriteAllBytes(second, []);
            var layer = new ClipOverlayLayer("Camera", true, Assets: [
                new ClipOverlayAsset(".clipinfo/overlays/0.mp4", 0, 2),
                new ClipOverlayAsset(".clipinfo/overlays/1.mp4", 2, 4)]);
            Assert.True(ClipOverlayManifest.IsUsable(root, layer));
            File.Delete(second);
            Assert.False(ClipOverlayManifest.IsUsable(root, layer));
            File.WriteAllBytes(second, []);
            ClipInfoSidecar.Save(root, clip, new ClipInfo(null, null, OverlayManifest: new ClipOverlayManifest(ClipOverlayManifest.CurrentVersion, layer)));
            ClipInfoSidecar.Delete(root, clip);
            Assert.False(File.Exists(first)); Assert.False(File.Exists(second));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
}
