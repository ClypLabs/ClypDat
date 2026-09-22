using System.Text.Json;
using ClypDat.App.Services;
using ClypDat.App.ViewModels;
using ClypDat.Core.Settings;
using Xunit;

namespace ClypDat.App.Tests;

public sealed class SpotifyOverlayLayerStateTests
{
    [Fact]
    public void LegacyEveEditSidecarMigratesToMp4Json()
    {
        var root = Path.Combine(Path.GetTempPath(), "spotify-sidecar-" + Guid.NewGuid());
        try
        {
            var clipPath = Path.Combine(root, "Clips", "Track.mp4");
            var legacyPath = LibraryLayout.SidecarPath(root, clipPath, ".eve.json");
            Directory.CreateDirectory(Path.GetDirectoryName(legacyPath)!);
            File.WriteAllText(legacyPath, "{\"SpotifyOverlayVisible\":false}");

            var edit = ClipEditSidecar.Load(root, clipPath)!;
            var migratedPath = ClipEditSidecar.SidecarPath(root, clipPath);
            Assert.EndsWith("Track.mp4.json", migratedPath, StringComparison.Ordinal);
            Assert.False(File.Exists(legacyPath));
            Assert.True(File.Exists(migratedPath));
            Assert.False(edit.SpotifyOverlayVisible);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    [Fact]
    public void NestedLegacySidecarMigratesIntoLibraryMetadata()
    {
        var root = Path.Combine(Path.GetTempPath(), "spotify-sidecar-" + Guid.NewGuid());
        try
        {
            var clipPath = Path.Combine(root, "Clips", "Game", "Track.mp4");
            var legacyPath = Path.Combine(Path.GetDirectoryName(clipPath)!, ".clipinfo", "Track.mp4.eve.json");
            Directory.CreateDirectory(Path.GetDirectoryName(legacyPath)!);
            File.WriteAllText(legacyPath, "{\"SpotifyOverlayVisible\":false}");

            ClipEditSidecar.MigrateLegacySidecars(root);
            Assert.False(File.Exists(legacyPath));
            Assert.True(File.Exists(ClipEditSidecar.SidecarPath(root, clipPath)));
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    [Fact]
    public void ExistingEditsKeepVisibleOverlayAtConfiguredPlacement()
    {
        var edit = JsonSerializer.Deserialize<ClipEditSettings>("{\"TrimStartSeconds\":3,\"SpeedMultiplier\":2}")!;
        Assert.True(edit.SpotifyOverlayVisible);
        Assert.Null(edit.SpotifyOverlayTransform);
        Assert.Equal(3, edit.TrimStartSeconds);
    }

    [Fact]
    public void LayerSurvivesRenameMoveAndSavedTrimWhileBakedEffectsReset()
    {
        var root = Path.Combine(Path.GetTempPath(), "spotify-layer-" + Guid.NewGuid());
        try
        {
            var oldPath = Path.Combine(root, "Clips", "Song's old \u65e5\u672c.mp4");
            var newPath = Path.Combine(root, "VODs", "renamed \u65e5\u672c.mp4");
            var transform = new SpotifyOverlayTransform(.21, .32, .43);
            ClipEditSidecar.Save(root, oldPath, new ClipEditSettings
            {
                TrimStartSeconds = 8,
                TrimEndSeconds = 20,
                SpeedMultiplier = 2,
                CropMode = "9:16",
                TrackVolumes = new() { [1] = 75 },
                MutedTrackIndexes = new() { 2 },
                Description = "Keep this description",
                SpotifyOverlayVisible = false,
                SpotifyOverlayTransform = transform
            });
            LibraryLayout.MoveSidecars(root, oldPath, newPath);
            Assert.Null(ClipEditSidecar.Load(root, oldPath));
            var moved = ClipEditSidecar.Load(root, newPath)!;
            Assert.Equal(transform, moved.SpotifyOverlayTransform);
            Assert.False(moved.SpotifyOverlayVisible);
            Assert.Equal(8, moved.TrimStartSeconds);

            ClipEditSidecar.ResetAfterSavedTrim(root, newPath);
            var trimmed = ClipEditSidecar.Load(root, newPath)!;
            Assert.Equal(transform, trimmed.SpotifyOverlayTransform);
            Assert.False(trimmed.SpotifyOverlayVisible);
            Assert.Equal("Keep this description", trimmed.Description);
            Assert.Equal(0, trimmed.TrimStartSeconds);
            Assert.Equal(0, trimmed.TrimEndSeconds);
            Assert.Equal(1, trimmed.SpeedMultiplier);
            Assert.Equal("None", trimmed.CropMode);
            Assert.Equal(75, trimmed.TrackVolumes[1]);
            Assert.Contains(2, trimmed.MutedTrackIndexes);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    internal static void OverlayLaneIsIndependentFromMediaStreams()
    {
        var lane = new TrackLaneViewModel(-1, "Spotify overlay", "overlay", "#38836B", false)
        {
            CanEditOverlay = true
        };
        Assert.True(lane.IsOverlay);
        Assert.False(lane.IsVideo);
        Assert.False(lane.IsAudio);
        Assert.False(lane.CanAdjustVolume);
        Assert.Equal(44, lane.LaneHeight);
        Assert.Equal(2, lane.LabelRowSpan);
    }

    [Fact]
    public void SpotifyOverlayRequiresSpotifyAudioLane()
    {
        var tracks = new[]
        {
            new TrackLaneViewModel(0, "Video", "video", "#05C7B7", false),
            new TrackLaneViewModel(1, "Game Audio", "audio", "#607080", true),
            new TrackLaneViewModel(2, "Spotify", "audio", "#1ED760", true)
        };

        Assert.True(MainWindowViewModel.HasSpotifyAudioTrack(tracks));
        Assert.False(MainWindowViewModel.HasSpotifyAudioTrack(tracks.Take(2)));
    }
}
