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
        Assert.True(output.Generation > pendingGeneration);
        output.EndSeek(TimeSpan.FromSeconds(2.001));
        Assert.Equal(pendingGeneration + 1, output.Generation);
        output.Submit([], [], TimeSpan.FromSeconds(2.001), .5);
        Assert.Equal(0u, output.ReadStatus().Failed);
    }

    [Fact]
    public void InvalidEffectFailsInsteadOfSilentlyDroppingBlur()
    {
        global::LibVLCSharp.Shared.Core.Initialize();
        using var vlc = new LibVLC("--quiet");
        using var output = new NativeVideoOutput();
        Assert.Throws<InvalidOperationException>(() => output.Submit([
            new EditorVideoModels.Blur { Sigma = float.NaN, Start = 0, End = 1 }
        ], [], TimeSpan.Zero, 0));
    }
}
