using System.Text.Json;
using ClypDat.App.ViewModels;
using ClypDat.App.Services;
using ClypDat.Core.Settings;
using Xunit;

namespace ClypDat.App.Tests;

public sealed class VideoOverlaySettingsTests
{
    [Theory]
    [InlineData("QWERTY Full", 1989, 540, "KeyE")]
    [InlineData("QWERTY Compact", 1124, 540, "KeyW")]
    [InlineData("Arrows", 679, 434, "ArrowUp")]
    [InlineData("AZERTY Compact", 1124, 540, "KeyW")]
    public void KeyboardCatalog_UsesMedalCanvasAndDeterministicSample(string layout, int width, int height, string sample)
    {
        var definition = KeyboardOverlayCatalog.Get(layout);

        Assert.Equal(width, definition.NativeWidth);
        Assert.Equal(height, definition.NativeHeight);
        Assert.Contains(sample, definition.SamplePressed);
        Assert.Contains("MouseLeft", definition.SamplePressed);
    }

    [Fact]
    public void MissingSettings_DefaultToDisabledWithExpectedLayout()
    {
        var settings = JsonSerializer.Deserialize<AppSettings>("{}")!;

        Assert.False(settings.VideoOverlays.Enabled);
        Assert.Equal("QWERTY Compact", settings.VideoOverlays.KeyboardLayout);
        Assert.Equal(new VideoOverlayTransform(.70, .05, .25), settings.VideoOverlays.CameraTransform);
        Assert.Equal(new VideoOverlayTransform(.05, .70, .35), settings.VideoOverlays.KeyboardTransform);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void WorkerSnapshot_PreservesSelectedCameraAndFullKeyboardAcrossWebJson(bool legacyEnabled)
    {
        var selected = new VideoOverlaySettings
        {
            Enabled = legacyEnabled,
            Camera = new VideoOverlayCameraSelection("@device_pnp_\\?\\usb#facecam", "Elgato Facecam 4K"),
            KeyboardLayout = KeyboardOverlayCatalog.QwertyFull
        };
        var json = JsonSerializer.Serialize(selected, new JsonSerializerOptions(JsonSerializerDefaults.Web));

        var workerSettings = JsonSerializer.Deserialize<VideoOverlaySettings>(json, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        var snapshot = workerSettings.ToCaptureSettings();

        Assert.Equal(new ClypDat.Capture.Abstractions.OverlayCameraSelection(selected.Camera!.DeviceMoniker, selected.Camera.FriendlyName), snapshot.Camera);
        Assert.Equal(KeyboardOverlayCatalog.QwertyFull, snapshot.KeyboardLayout);
    }

    [Fact]
    public void WorkerSnapshot_UsesNoneForUnknownKeyboardInsteadOfCompactDefault()
    {
        var snapshot = new VideoOverlaySettings { KeyboardLayout = "Unknown layout" }.ToCaptureSettings();

        Assert.Equal("None", snapshot.KeyboardLayout);
    }

    [Theory]
    [InlineData(1.0, 1.0, .80, 16d / 9d)]
    [InlineData(-1.0, -1.0, .01, 16d / 9d)]
    [InlineData(.9, .9, .5, 2.4)]
    public void Normalize_ClampsOverlayInsideFrame(double x, double y, double width, double aspect)
    {
        var result = VideoOverlayLayout.Normalize(new(x, y, width), aspect);

        Assert.InRange(result.Width, VideoOverlayLayout.MinimumWidth, 1d);
        Assert.InRange(result.X, 0d, 1d - result.Width);
        Assert.InRange(result.Y, 0d, 1d - result.Width / aspect);
    }

    [Fact]
    public void Corner_UsesExpectedBottomRightCoordinates()
    {
        var result = VideoOverlayLayout.Corner("Bottom Right", .25, VideoOverlayLayout.CameraAspectRatio);

        Assert.Equal(.75, result.X, 3);
        Assert.Equal(1 - .25 / VideoOverlayLayout.CameraAspectRatio, result.Y, 3);
    }

    [Fact]
    public void Resize_AnchorsOppositeCornerAndKeepsSourceAspect()
    {
        var start = VideoOverlayLayout.Corner("Bottom Right", .25, VideoOverlayLayout.CameraAspectRatio);
        var result = VideoOverlayManipulation.Apply(start, VideoOverlayManipulationMode.TopLeft,
            -.10, -.10, 16d / 9d, VideoOverlayLayout.CameraAspectRatio);

        Assert.Equal(1, result.X + result.Width, 3);
        Assert.Equal(1, result.Y + result.Width, 3);
        Assert.True(result.Width > start.Width);
    }

    [Fact]
    public void Migration_AddsAnchorsWithoutChangingLegacyTransform()
    {
        var settings = new AppSettings { SettingsSchemaVersion = 8 };
        var camera = new VideoOverlayTransform(.31, .42, .22);
        settings.VideoOverlays.CameraTransform = camera;

        Assert.True(AppSettingsMigrations.Apply(settings));
        Assert.Equal(camera, settings.VideoOverlays.CameraTransform);
        Assert.Equal("Top Right", settings.VideoOverlays.CameraAnchor);
        Assert.Equal("Bottom Left", settings.VideoOverlays.KeyboardAnchor);
    }

    [Fact]
    public void DirectShowParser_ReturnsOnlyExplicitVideoDevices()
    {
        const string output = """
            [dshow @ 000001] "Elgato 4K X" (video)
            [dshow @ 000001]   Alternative name "@device_pnp_\\?\\usb#elgato"
            [dshow @ 000001] "Elgato Virtual Camera" (video)
            [dshow @ 000001] "Voicemeeter Virtual Camera" (video)
            [dshow @ 000001] "Microphone (USB Audio Device)" (audio)
            [dshow @ 000001] "Voicemeeter Output" (audio)
            [dshow @ 000001] "Elgato 4K X" (video)
            [dshow @ 000001] "none" (none)
            [dshow @ 000001] Could not enumerate "diagnostic text"
            """;

        var cameras = DirectShowCameraParser.Parse(output, includeVirtual: false);

        Assert.Collection(cameras, camera => Assert.Equal("Elgato 4K X", camera.Name));
    }

    [Fact]
    public void DirectShowParser_RespectsVirtualCameraToggle()
    {
        const string output = """
            [dshow @ 000001] "Elgato 4K X" (video)
            [dshow @ 000001] "Elgato Virtual Camera" (video)
            """;

        var cameras = DirectShowCameraParser.Parse(output, includeVirtual: true);

        Assert.Equal(["Elgato 4K X", "Elgato Virtual Camera"], cameras.Select(camera => camera.Name));
    }

    [Fact]
    public void DirectShowParser_AliasesUtf8ElgatoVirtualCameraButKeepsCaptureName()
    {
        const string output = "[dshow @ 000001] \"EƖgato Virtual Camera\" (video)";

        var camera = Assert.Single(DirectShowCameraParser.Parse(output, includeVirtual: true));

        Assert.Equal("Elgato Virtual Camera", camera.Name);
        Assert.Equal("EƖgato Virtual Camera", camera.Moniker);
    }

    [Fact]
    public void SavedGarbledElgatoVirtualCamera_IsRepairedWithoutLayoutChanges()
    {
        var saved = new VideoOverlayCameraSelection("EÆ–gato Virtual Camera", "EÆ–gato Virtual Camera");
        var detected = new CameraOption("Elgato Virtual Camera", "EƖgato Virtual Camera", true);

        var repaired = DirectShowCameraParser.RepairSavedElgatoVirtualCamera(saved, [detected]);

        Assert.Equal(new VideoOverlayCameraSelection("EƖgato Virtual Camera", "Elgato Virtual Camera"), repaired);
    }

    [Fact]
    public void Sources_AreGroupedAndHeadingsCannotBeSelected()
    {
        var sources = OverlaySourceOptions.Create([]);

        Assert.Equal(["None", "Peripheral Overlays", "QWERTY Keyboard + Mouse", "QWERTY Keyboard + Mouse (Full)", "Arrow Keys + Mouse", "AZERTY Keyboard + Mouse"], sources.Select(source => source.Name));
        Assert.False(sources[1].IsSelectable);
        Assert.DoesNotContain(sources, source => source.Name.Contains("Controller", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Sources_PutDetectedCamerasBeforePeripheralOverlays()
    {
        var sources = OverlaySourceOptions.Create([new CameraOption("Elgato 4K X", "Elgato 4K X")]);

        Assert.Equal(["None", "Cameras", "Elgato 4K X", "Peripheral Overlays"], sources.Take(4).Select(source => source.Name));
    }

    [Theory]
    [InlineData("Elgato 4K X", "Elgato 4K X", true)]
    [InlineData("@device_pnp_\\?\\usb#elgato", "@device_pnp_\\?\\usb#elgato", false)]
    [InlineData("Microphone (USB Audio Device)", "Microphone (USB Audio Device)", false)]
    [InlineData("Voicemeeter Output", "Voicemeeter Output", false)]
    public void SavedCameraFallback_OnlyKeepsPotentialCameraSelections(string moniker, string name, bool expected)
    {
        Assert.Equal(expected, DirectShowCameraParser.IsSavedCameraSelection(new(moniker, name)));
    }
}
