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
}
