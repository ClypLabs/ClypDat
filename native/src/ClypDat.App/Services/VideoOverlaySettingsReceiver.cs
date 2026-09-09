using ClypDat.Capture.Abstractions;

namespace ClypDat.App.Services;

/// <summary>Worker-only settings hand-off. Kept off IReplayBuffer so old backends stay valid.</summary>
internal interface IVideoOverlaySettingsReceiver
{
    void SetVideoOverlaySettings(OverlayCaptureSettings settings);
}

internal static class VideoOverlaySettingsMapper
{
    // Map persisted Core settings exactly at the UI/worker boundary. Enabled is
    // legacy burn-in state and intentionally never affects capture selection.
    public static OverlayCaptureSettings ToCaptureSettings(this ClypDat.Core.Settings.VideoOverlaySettings? settings)
    {
        settings ??= new();
        var layout = ClypDat.Core.Settings.KeyboardOverlayCatalog.IsKnown(settings.KeyboardLayout)
            ? settings.KeyboardLayout : "None";
        return new OverlayCaptureSettings(
            settings.Camera is { } camera ? new(camera.DeviceMoniker, camera.FriendlyName) : null,
            layout,
            new(settings.CameraTransform.X, settings.CameraTransform.Y, settings.CameraTransform.Width),
            new(settings.KeyboardTransform.X, settings.KeyboardTransform.Y, settings.KeyboardTransform.Width));
    }

    public static ClypDat.Core.Settings.VideoOverlayTransform ToPresentationTransform(this OverlayTransform transform)
        => new(transform.X, transform.Y, transform.Width);
}
