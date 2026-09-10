namespace ClypDat.Capture.Abstractions;

/// <summary>
/// Capture-only overlay selection. This is IPC data, deliberately independent
/// from persisted UI settings and legacy presentation switches.
/// </summary>
public sealed record OverlayCaptureSettings(
    OverlayCameraSelection? Camera,
    string KeyboardLayout,
    OverlayTransform CameraTransform,
    OverlayTransform KeyboardTransform,
    // A custom key set is resolved to finished caps on the app side. The worker
    // is a separate process with no view of app settings, so the set's id alone
    // would arrive meaningless - it has to travel already packed.
    IReadOnlyList<OverlayKeyCapSnapshot>? KeyboardKeys = null,
    string? KeyboardName = null,
    bool KeyboardShowMouse = true,
    string RecordingMode = OverlayRecordingMode.EditableLayers)
{
    public static OverlayCaptureSettings None { get; } = new(null, "None", new(.70, .05, .25), new(.05, .70, .35));
}

public static class OverlayRecordingMode
{
    public const string EditableLayers = "EditableLayers";
    public const string BurnIntoVideo = "BurnIntoVideo";
    public static bool IsBurned(string? value) => string.Equals(value, BurnIntoVideo, StringComparison.OrdinalIgnoreCase);
}

public sealed record OverlayKeyCapSnapshot(string Code, string Label, int Row, double Units = 1);

public sealed record OverlayCameraSelection(string DeviceMoniker, string FriendlyName);
public sealed record OverlayTransform(double X, double Y, double Width);
