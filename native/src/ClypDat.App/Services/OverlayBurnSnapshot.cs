using ClypDat.Capture.Abstractions;

namespace ClypDat.App.Services;

internal sealed record OverlayBurnSnapshot(OverlayCaptureSettings Settings, byte[]? CameraBgra,
    IReadOnlySet<string> PressedKeys, string? CameraError);
