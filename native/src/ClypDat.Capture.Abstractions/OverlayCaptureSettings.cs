namespace ClypDat.Capture.Abstractions;

/// <summary>
/// Capture-only overlay selection. This is IPC data, deliberately independent
/// from persisted UI settings and legacy presentation switches.
/// </summary>
public sealed record OverlayCaptureSettings(
    OverlayCameraSelection? Camera,
    string KeyboardLayout,
    OverlayTransform CameraTransform,
    OverlayTransform KeyboardTransform)
{
    public static OverlayCaptureSettings None { get; } = new(null, "None", new(.70, .05, .25), new(.05, .70, .35));
}

public sealed record OverlayCameraSelection(string DeviceMoniker, string FriendlyName);
public sealed record OverlayTransform(double X, double Y, double Width);
