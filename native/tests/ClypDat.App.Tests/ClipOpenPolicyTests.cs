using ClypDat.App.Services;
using ClypDat.App.ViewModels;
using Xunit;

namespace ClypDat.App.Tests;

public sealed class ClipOpenPolicyTests
{
    [Fact]
    public void MedalImportWithSeveralAudioTracks_DropsThePreMix()
    {
        var tracks = new[]
        {
            new MediaTrackInfo(0, "video", "h264", "Video"),
            new MediaTrackInfo(1, "audio", "aac", "All Audio"),
            new MediaTrackInfo(2, "audio", "aac", "All PC Audio"),
            new MediaTrackInfo(3, "audio", "aac", "Microphone"),
        };

        Assert.Equal([2, 3], MainWindowViewModel.PlayableAudioStreamIndexes(tracks, isMedalImport: true));
        Assert.Equal([1, 2, 3], MainWindowViewModel.PlayableAudioStreamIndexes(tracks, isMedalImport: false));
    }

    [Fact]
    public void MedalImportWithOneAudioTrack_KeepsIt()
    {
        var tracks = new[]
        {
            new MediaTrackInfo(0, "video", "h264", "Video"),
            new MediaTrackInfo(1, "audio", "aac", "All Audio"),
        };

        Assert.Equal([1], MainWindowViewModel.PlayableAudioStreamIndexes(tracks, isMedalImport: true));
    }

    [Fact]
    public void ReplayMixIsExcludedFromEditorAndHoverAudio()
    {
        var tracks = new[]
        {
            new MediaTrackInfo(0, "video", "h264", "Video"),
            new MediaTrackInfo(1, "audio", "aac", "All Tracks"),
            new MediaTrackInfo(2, "audio", "aac", "Game Audio"),
            new MediaTrackInfo(3, "audio", "aac", "Discord"),
            new MediaTrackInfo(4, "audio", "aac", "Microphone"),
        };

        Assert.Equal([2, 3, 4], MainWindowViewModel.PlayableAudioStreamIndexes(tracks, isMedalImport: false));
    }

    [Fact]
    public void OnlyFirstNamedMixWithOtherAudioIsExcluded()
    {
        var oneStream = new[] { new MediaTrackInfo(1, "audio", "aac", "All Tracks") };
        var laterMix = new[]
        {
            new MediaTrackInfo(1, "audio", "aac", "Game Audio"),
            new MediaTrackInfo(2, "audio", "aac", "All Tracks"),
        };

        Assert.Equal([1], MainWindowViewModel.PlayableAudioStreamIndexes(oneStream, isMedalImport: false));
        Assert.Equal([1, 2], MainWindowViewModel.PlayableAudioStreamIndexes(laterMix, isMedalImport: false));
    }

    [Theory]
    [InlineData(false, false, true, false)]
    [InlineData(false, true, true, true)]
    [InlineData(true, false, false, true)]
    [InlineData(true, true, false, true)]
    public void OpenEditorKeepsCachesAndNeverBlocks(bool editorOpen, bool gameRecording, bool clearsCaches, bool deferred)
    {
        var mode = MemoryTrimmer.ResolveTrimMode(editorOpen, recording: gameRecording, gameRunning: gameRecording);

        Assert.Equal(clearsCaches, mode.ClearCaches);
        Assert.Equal(deferred, mode.Deferred);
    }
}
