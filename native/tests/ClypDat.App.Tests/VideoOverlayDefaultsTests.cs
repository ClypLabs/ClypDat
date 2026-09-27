using System.Text.Json;
using ClypDat.App.Services;
using ClypDat.App.ViewModels;
using ClypDat.Core.Settings;
using Xunit;

namespace ClypDat.App.Tests;

public sealed class VideoOverlayDefaultsTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("{}")]
    [InlineData("{\"VideoOverlays\":{}}")]
    [InlineData("{\"VideoOverlays\":null}")]
    public void FreshOrMissingOverlaySettingsLeaveAllSlotsEmpty(string? json)
    {
        using var files = new SettingsFiles();
        if (json is not null) File.WriteAllText(AppSettingsStore.SettingsPath, json);
        var settings = AppSettingsStore.Load();
        Assert.Equal("None", settings.VideoOverlays.KeyboardLayout);
        Assert.Null(settings.VideoOverlays.Camera);
        Assert.Equal("None", CustomGameSettingsResolver.ResolveOverlays(settings, "game.exe").KeyboardLayout);
        AvaloniaTestThread.Run(() =>
        {
            using var model = new VideoOverlayViewModel(settings.VideoOverlays, () => { }, null,
                new NoCameraPreview(), refreshCameras: false);
            Assert.Equal(4, model.Slots.Count);
            Assert.All(model.Slots, slot => Assert.True(slot.IsEmpty));
            Assert.False(model.HasKeyboard);
        }, TimeSpan.FromSeconds(30), "Fresh overlay settings timed out.");
        Assert.True(AppSettingsStore.Save(settings), AppSettingsStore.LastSaveError);
        Assert.Equal("None", AppSettingsStore.Load().VideoOverlays.KeyboardLayout);
    }

    [Theory]
    [InlineData("None")]
    [InlineData("QWERTY Compact")]
    [InlineData("Arrows")]
    [InlineData("QWERTY Full")]
    [InlineData("AZERTY Compact")]
    public void ExplicitSelectionsAndGamePlacementSurviveMigrationAndSave(string layout)
    {
        using var files = new SettingsFiles();
        var global = new VideoOverlaySettings
        {
            KeyboardLayout = layout, KeyboardAnchor = "Top Left",
            KeyboardTransform = new(.12, .18, .2),
            Camera = new("saved-camera", "Saved camera"), CameraAnchor = "Bottom Right",
            CameraTransform = new(.64, .58, .2)
        };
        var game = global.Copy();
        game.KeyboardLayout = "QWERTY Compact";
        game.KeyboardAnchor = "Bottom Right";
        game.KeyboardTransform = new(.6, .65, .25);
        var settings = new AppSettings { SettingsSchemaVersion = 5, VideoOverlays = global };
        settings.CustomGameSettings["game.exe"] = new CustomGameProfile
        {
            Groups = [CustomGameSettingsResolver.OverlaysGroup], VideoOverlays = game
        };
        File.WriteAllText(AppSettingsStore.SettingsPath, JsonSerializer.Serialize(settings));
        var migrated = AppSettingsStore.Load();
        Assert.Equal(AppSettingsMigrations.CurrentSchemaVersion, migrated.SettingsSchemaVersion);
        AssertPlacement(global, migrated.VideoOverlays);
        AssertPlacement(game, CustomGameSettingsResolver.ResolveOverlays(migrated, "game.exe"));
        Assert.True(AppSettingsStore.Save(migrated), AppSettingsStore.LastSaveError);
        var reloaded = AppSettingsStore.Load();
        AssertPlacement(global, CustomGameSettingsResolver.ResolveOverlays(reloaded, "other.exe"));
        AssertPlacement(game, CustomGameSettingsResolver.ResolveOverlays(reloaded, "game.exe"));
    }

    private static void AssertPlacement(VideoOverlaySettings expected, VideoOverlaySettings actual)
    {
        Assert.Equal(expected.KeyboardLayout, actual.KeyboardLayout);
        Assert.Equal(expected.KeyboardAnchor, actual.KeyboardAnchor);
        Assert.Equal(expected.KeyboardTransform, actual.KeyboardTransform);
        Assert.Equal(expected.Camera, actual.Camera);
        Assert.Equal(expected.CameraAnchor, actual.CameraAnchor);
        Assert.Equal(expected.CameraTransform, actual.CameraTransform);
    }

    private sealed class SettingsFiles : IDisposable
    {
        private readonly string _previous = AppDataPaths.ProductFolderName;
        private readonly string _root;
        public SettingsFiles()
        {
            AppDataPaths.ConfigureProductFolder("ClypDat-OverlayTests-" + Guid.NewGuid().ToString("N"));
            _root = AppDataPaths.Root;
            Directory.CreateDirectory(_root);
        }
        public void Dispose()
        {
            AppDataPaths.ConfigureProductFolder(_previous);
            AppSettingsStore.Load();
            Directory.Delete(_root, recursive: true);
        }
    }

    private sealed class NoCameraPreview : ICameraPreviewService
    {
        public event Action<CameraPreviewFrame>? FrameReady { add { } remove { } }
        public event Action<CameraPreviewFailure>? Failed { add { } remove { } }
        public bool IsRunning => false;
        public int Session => 0;
        public void Start(string deviceMoniker) => throw new InvalidOperationException("Fresh overlays must not start a camera.");
        public void Stop() { }
        public void Dispose() { }
    }
}
