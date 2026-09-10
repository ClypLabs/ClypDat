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
    /// <param name="keyLayouts">The user's custom key sets, so a custom selection
    /// can be resolved to finished caps here. Optional: a caller with no settings
    /// in scope still gets correct behaviour for the built-in layouts.</param>
    public static OverlayCaptureSettings ToCaptureSettings(this ClypDat.Core.Settings.VideoOverlaySettings? settings,
        IReadOnlyList<ClypDat.Core.Settings.CustomKeyboardLayout>? keyLayouts = null)
    {
        settings ??= new();
        var custom = ClypDat.Core.Settings.CustomKeyboardLibrary.Find(keyLayouts, settings.KeyboardLayout);
        // A custom selection whose set is gone is not a layout this app can draw.
        var layout = ClypDat.Core.Settings.KeyboardOverlayCatalog.IsKnown(settings.KeyboardLayout) &&
            (!ClypDat.Core.Settings.CustomKeyboardLibrary.IsCustomSelection(settings.KeyboardLayout) || custom is not null)
            ? settings.KeyboardLayout : "None";
        return new OverlayCaptureSettings(
            settings.Camera is { } camera ? new(camera.DeviceMoniker, camera.FriendlyName) : null,
            layout,
            new(settings.CameraTransform.X, settings.CameraTransform.Y, settings.CameraTransform.Width),
            new(settings.KeyboardTransform.X, settings.KeyboardTransform.Y, settings.KeyboardTransform.Width),
            custom is null ? null : PackSnapshot(custom),
            custom?.Name,
            custom?.IncludeMouse ?? true,
            ClypDat.Core.Settings.VideoOverlayRecordingMode.Normalize(settings.RecordingMode));
    }

    private static IReadOnlyList<OverlayKeyCapSnapshot> PackSnapshot(ClypDat.Core.Settings.CustomKeyboardLayout layout)
    {
        var board = ClypDat.Core.Settings.CustomKeyboardBoard.Pack(layout.Keys, layout.IncludeMouse);
        return board.Rows.SelectMany((row, index) => row.Select(cap => new OverlayKeyCapSnapshot(cap.Code, cap.Label, index, cap.Units)))
            .Concat(board.Cluster.SelectMany((row, index) => row.Select(cap => new OverlayKeyCapSnapshot(cap.Code, cap.Label, ClusterRow - index, cap.Units))))
            .ToArray();
    }

    /// <summary>Cluster rows are stored as negative row indices, so the arrow
    /// cluster survives the round trip without a second collection on the wire.</summary>
    public const int ClusterRow = -1;

    public static ClypDat.Core.Settings.VideoOverlayTransform ToPresentationTransform(this OverlayTransform transform)
        => new(transform.X, transform.Y, transform.Width);
}
