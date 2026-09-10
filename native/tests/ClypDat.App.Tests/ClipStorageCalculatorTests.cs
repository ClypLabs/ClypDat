using ClypDat.App.Services;
using Xunit;

namespace ClypDat.App.Tests;

public sealed class ClipStorageCalculatorTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"clypdat-storage-{Guid.NewGuid():N}");

    [Fact]
    public void IncludesUniqueCameraAndInputAssets_AndIgnoresMissingOrEscapingPaths()
    {
        Directory.CreateDirectory(Path.Combine(_root, ".clipinfo"));
        var video = Write("clip.mp4", 110);
        var camera = Write(".clipinfo/camera.mp4", 10);
        var input = Write(".clipinfo/input.json", 2);
        var manifest = new ClipOverlayManifest(ClipOverlayManifest.CurrentVersion,
            new ClipOverlayLayer("Camera", true, AssetPath: ".clipinfo/camera.mp4", Assets: [new ClipOverlayAsset(".clipinfo/camera.mp4", 0, 1)]),
            new ClipOverlayLayer("Keyboard", false, AssetPath: "../../outside", InputIndexPath: ".clipinfo/input.json"));

        var total = ClipStorageCalculator.Calculate(_root, video, new ClipInfo(null, null, OverlayManifest: manifest));

        Assert.Equal(122, total);
        Assert.True(File.Exists(camera));
        Assert.True(File.Exists(input));
    }

    private string Write(string relative, long bytes)
    {
        var path = Path.Combine(_root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var stream = File.Create(path);
        stream.SetLength(bytes);
        return path;
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, true);
    }
}
