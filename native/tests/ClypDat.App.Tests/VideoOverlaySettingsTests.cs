using System.Text.Json;
using ClypDat.App.ViewModels;
using ClypDat.Core.Settings;
using Xunit;

namespace ClypDat.App.Tests;

public sealed class VideoOverlaySettingsTests
{
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
