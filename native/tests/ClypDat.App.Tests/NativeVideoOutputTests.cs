using ClypDat.App.Services;
using LibVLCSharp.Shared;
using Xunit;

namespace ClypDat.App.Tests;

public sealed class NativeVideoOutputTests
{
    [Fact]
    public void PublishedPluginSharesVersionedContextWithManagedBridge()
    {
        global::LibVLCSharp.Shared.Core.Initialize();
        using var vlc = new LibVLC("--quiet", "--no-plugins-cache");
        using var player = new MediaPlayer(vlc);
        using var output = new NativeVideoOutput();
        output.BindPlayer(player);
        output.EndSeek(TimeSpan.FromSeconds(1.001));
        output.Submit([], [], TimeSpan.FromSeconds(1.001), 1);
        var first = output.ReadStatus();
        Assert.Equal(NativeVideoOutput.Abi, first.Version);
        Assert.Equal(0u, first.Failed);
        var generation = output.Generation;
        output.BeginSeek(TimeSpan.FromSeconds(2.001));
        Assert.True(output.Generation > generation);
        var pendingGeneration = output.Generation;
        output.EndSeek(TimeSpan.FromSeconds(2.001));
        Assert.Equal(pendingGeneration, output.Generation);
        output.EndSeek(TimeSpan.FromSeconds(2.001));
        Assert.Equal(pendingGeneration, output.Generation);
        output.Submit([], [], TimeSpan.FromSeconds(2.001), .5);
        Assert.Equal(0u, output.ReadStatus().Failed);
    }

    // A hover warm-up and the editor publish to the same output when the warm-up
    // reloads the clip still open in the editor. The warm-up's empty scene drops
    // the editor's images; the editor's next scene, naming one it uploaded
    // earlier, used to be rejected and pause the preview.
    [Fact]
    public void SceneNamingAnImageAnEmptySceneDroppedIsStillAccepted()
    {
        global::LibVLCSharp.Shared.Core.Initialize();
        using var vlc = new LibVLC("--quiet", "--no-plugins-cache");
        using var output = new NativeVideoOutput();
        output.EndSeek(TimeSpan.Zero);
        AvaloniaTestThread.Run(() =>
        {
            using var bitmap = new Avalonia.Media.Imaging.WriteableBitmap(new Avalonia.PixelSize(8, 8), new Avalonia.Vector(96, 96));
            output.UpdateArtwork(7, bitmap);
        }, TimeSpan.FromSeconds(10), "Artwork upload timed out.");
        NativeVideoOutput.Artwork[] scene = [new() { Id = 7, Bounds = new(new Avalonia.Rect(0, 0, .5, .5)), Start = 0, End = 10 }];

        output.Submit([], scene, TimeSpan.Zero, 0);
        output.Submit([], [], TimeSpan.Zero, 0);
        output.Submit([], scene, TimeSpan.Zero, 0);

        Assert.Equal(0u, output.ReadStatus().Failed);
    }

    [Fact]
    public void InvalidEffectFailsInsteadOfSilentlyDroppingBlur()
    {
        global::LibVLCSharp.Shared.Core.Initialize();
        using var vlc = new LibVLC("--quiet");
        using var output = new NativeVideoOutput();
        Assert.Throws<InvalidOperationException>(() => output.Submit([
            new NativeVideoOutput.Blur { Sigma = float.NaN, Start = 0, End = 1 }
        ], [], TimeSpan.Zero, 0));
    }
}
