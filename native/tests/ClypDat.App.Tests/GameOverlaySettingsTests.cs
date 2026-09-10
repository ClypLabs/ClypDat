using System.Text.Json;
using ClypDat.App.Services;
using ClypDat.App.ViewModels;
using ClypDat.Core.Settings;
using Xunit;

namespace ClypDat.App.Tests;

public sealed class GameOverlaySettingsTests
{
    [Fact]
    public void ReaddingOverlayGroupRestoresSavedOverrideWhileFirstAddSeedsGlobal()
    {
        var settings = new AppSettings();
        settings.VideoOverlays.KeyboardLayout = "QWERTY Compact";

        var saved = new CustomGameProfile { VideoOverlays = new() { KeyboardLayout = "None" } };
        var savedTab = new CustomGameTabViewModel("saved.exe", saved, settings, () => { });
        savedTab.HasOverlays = true;
        Assert.Equal("None", saved.VideoOverlays!.KeyboardLayout);

        savedTab.HasOverlays = false;
        settings.VideoOverlays.KeyboardLayout = "Arrow Keys + Mouse";
        savedTab.HasOverlays = true;
        Assert.Equal("None", saved.VideoOverlays.KeyboardLayout);

        var fresh = new CustomGameProfile();
        var freshTab = new CustomGameTabViewModel("fresh.exe", fresh, settings, () => { });
        freshTab.HasOverlays = true;
        Assert.NotSame(settings.VideoOverlays, fresh.VideoOverlays);
        Assert.Equal("Arrow Keys + Mouse", fresh.VideoOverlays!.KeyboardLayout);
    }

    [Fact]
    public void TallCustomBoardFitsOutputAndKeepsItsPackedAspect()
    {
        var board = CustomKeyboardBoard.Pack(["KeyW", "KeyA", "KeyZ", "Space"], false);
        var aspect = CustomKeyboardBoard.AspectRatio(board);
        var bounds = ClipOverlayBurnLayout.Resolve(new(.9, .9, .7), aspect, 1920, 1080);
        Assert.InRange(bounds.X + bounds.Width, 1, 1920);
        Assert.InRange(bounds.Y + bounds.Height, 1, 1080);
        Assert.InRange(Math.Abs((double)bounds.Width / bounds.Height - aspect), 0, .01);
    }
    [Fact]
    public void OverrideIsSeededIndependentlyAndCameraOffDoesNotFallBack()
    {
        var settings = new AppSettings();
        settings.VideoOverlays.Camera = new("device", "Camera");
        settings.VideoOverlays.CameraAnchor = "Bottom Right";
        settings.VideoOverlays.IncludeVirtualCameras = true;
        var profile = new CustomGameProfile { Groups = ["Overlays"] };
        settings.CustomGameSettings["game.exe"] = profile;
        CustomGameSettingsResolver.SeedGroupFromGlobal(settings, profile, "Overlays");
        Assert.NotSame(settings.VideoOverlays, profile.VideoOverlays);
        Assert.Equal(settings.VideoOverlays.CameraTransform, profile.VideoOverlays!.CameraTransform);
        Assert.Equal("Bottom Right", profile.VideoOverlays.CameraAnchor);
        profile.VideoOverlays.Camera = null;
        profile.VideoOverlays.KeyboardLayout = "None";
        profile.VideoOverlays.IncludeVirtualCameras = false;
        var resolved = CustomGameSettingsResolver.ResolveOverlays(settings, "game.exe");
        Assert.Null(resolved.Camera);
        Assert.Equal("None", resolved.KeyboardLayout);
        Assert.True(resolved.IncludeVirtualCameras);
        Assert.NotNull(settings.VideoOverlays.Camera);
        profile.Groups.Clear();
        Assert.Equal(settings.VideoOverlays.Camera, CustomGameSettingsResolver.ResolveOverlays(settings, "game.exe").Camera);
        Assert.Equal(settings.VideoOverlays.Camera, CustomGameSettingsResolver.ResolveOverlays(settings, "unknown").Camera);
    }

    [Fact]
    public void EditorPlacementCreatesGameOverrideWithoutChangingGlobalOrOtherLayer()
    {
        var settings = new AppSettings();
        settings.GameCaptureOverrides.Add(new() { ExecutableName = "game.exe", DisplayName = "Game" });
        var global = settings.VideoOverlays.Copy();
        var moved = new VideoOverlayTransform(.1, .2, .3);
        Assert.Equal("game.exe", CustomGameSettingsResolver.UpdateOverlayPlacement(settings, "Game", "Camera", moved));
        var resolved = CustomGameSettingsResolver.ResolveOverlays(settings, "game.exe");
        Assert.Equal(moved, resolved.CameraTransform);
        Assert.Equal(global.KeyboardTransform, resolved.KeyboardTransform);
        Assert.Equal(global.CameraTransform, settings.VideoOverlays.CameraTransform);
        Assert.Null(CustomGameSettingsResolver.UpdateOverlayPlacement(settings, null, "Camera", moved));
        Assert.Null(CustomGameSettingsResolver.UpdateOverlayPlacement(settings, "Unknown game", "Camera", moved));
        Assert.Single(settings.CustomGameSettings);
    }

    [Fact]
    public void DisplayNameChoosesExistingProfileAndAliasesResolveIt()
    {
        var settings = new AppSettings();
        settings.GameCaptureOverrides.Add(new() { ExecutableName = "game.exe", DisplayName = "Game" });
        settings.GameCaptureOverrides.Add(new() { ExecutableName = "steam-123", DisplayName = "Game" });
        settings.CustomGameSettings["steam-123"] = new() { DisplayName = "Game", Groups = ["Overlays"], VideoOverlays = new() { KeyboardLayout = "None" } };
        Assert.Equal("steam-123", CustomGameSettingsResolver.DetectionKeyForDisplayName(settings, "GAME"));
        Assert.Equal("None", CustomGameSettingsResolver.ResolveOverlays(settings, "game.exe").KeyboardLayout);
        var reloaded = JsonSerializer.Deserialize<AppSettings>(JsonSerializer.Serialize(settings))!;
        Assert.Equal("None", CustomGameSettingsResolver.ResolveOverlays(reloaded, "game.exe").KeyboardLayout);
    }

    [Fact]
    public void CapturePayloadFreezesCustomKeysAcrossLibraryEdits()
    {
        var layout = new CustomKeyboardLayout { Name = "Movement", Keys = ["KeyW", "KeyA", "KeyS", "KeyD", "ShiftLeft", "Space"], IncludeMouse = false };
        var settings = new VideoOverlaySettings { KeyboardLayout = CustomKeyboardLibrary.Selection(layout) };
        var payload = settings.ToCaptureSettings([layout]);
        layout.Keys.Clear(); layout.IncludeMouse = true;
        var json = JsonSerializer.Serialize(payload);
        Assert.Contains("ShiftLeft", json);
        Assert.Contains("KeyW", json);
        Assert.DoesNotContain("MouseLeft", json);
        Assert.Equal("None", settings.ToCaptureSettings([]).KeyboardLayout);
    }

    [Fact]
    public void MigrationPrunesMalformedSetsAndDanglingGameSelections()
    {
        var valid = new CustomKeyboardLayout { Keys = ["KeyW", "unknown", "KeyW"] };
        var settings = new AppSettings { SettingsSchemaVersion = 10, CustomKeyboardLayouts = [new() { Id = "bad" }, valid] };
        settings.VideoOverlays.KeyboardLayout = CustomKeyboardLibrary.Selection(valid);
        settings.CustomGameSettings["game.exe"] = new() { Groups = ["Overlays"], VideoOverlays = new() { KeyboardLayout = "custom:" + Guid.NewGuid().ToString("N") } };
        Assert.True(AppSettingsMigrations.Apply(settings));
        Assert.Single(settings.CustomKeyboardLayouts);
        Assert.Equal(["KeyW"], valid.Keys);
        Assert.Equal(CustomKeyboardLibrary.Selection(valid), settings.VideoOverlays.KeyboardLayout);
        Assert.Equal("None", settings.CustomGameSettings["game.exe"].VideoOverlays!.KeyboardLayout);
        Assert.False(KeyboardOverlayCatalog.IsKnown("custom:bad"));
    }
}
