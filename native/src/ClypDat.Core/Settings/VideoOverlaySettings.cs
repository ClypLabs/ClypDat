namespace ClypDat.Core.Settings;

// Coordinates are fractions of final encoded frame. This keeps layouts stable
// across capture resolutions and makes preview and recorder use same contract.
public sealed class VideoOverlaySettings
{
    public VideoOverlaySettings Copy() => new()
    {
        Enabled = Enabled, RecordingMode = RecordingMode, IncludeVirtualCameras = IncludeVirtualCameras,
        Camera = Camera is null ? null : Camera with { }, KeyboardLayout = KeyboardLayout,
        CameraTransform = CameraTransform with { }, KeyboardTransform = KeyboardTransform with { },
        CameraAnchor = CameraAnchor, KeyboardAnchor = KeyboardAnchor
    };
    // Compatibility-only. Older settings persisted this burn-in switch. It is
    // deliberately ignored: selecting a source now captures an editable layer.
    public bool Enabled { get; set; }
    /// <summary>How camera and peripheral overlays are stored for new captures.
    /// Missing values deserialize as editable for settings written before this
    /// choice existed.</summary>
    public string RecordingMode { get; set; } = VideoOverlayRecordingMode.EditableLayers;
    public bool IncludeVirtualCameras { get; set; }
    public VideoOverlayCameraSelection? Camera { get; set; }
    public string KeyboardLayout { get; set; } = "QWERTY Compact";
    public VideoOverlayTransform CameraTransform { get; set; } = new(.70, .05, .25);
    public VideoOverlayTransform KeyboardTransform { get; set; } = new(.05, .70, .35);
    // Source placement is separate from its transform.  A source can keep its
    // assigned picker corner after being freely moved around the frame.
    public string? CameraAnchor { get; set; } = "Top Right";
    public string? KeyboardAnchor { get; set; } = "Bottom Left";
}

public static class VideoOverlayRecordingMode
{
    public const string EditableLayers = "EditableLayers";
    public const string BurnIntoVideo = "BurnIntoVideo";

    public static string Normalize(string? value) => string.Equals(value, BurnIntoVideo, StringComparison.OrdinalIgnoreCase)
        ? BurnIntoVideo : EditableLayers;
    public static bool IsBurned(string? value) => Normalize(value) == BurnIntoVideo;
}

public sealed record VideoOverlayCameraSelection(string DeviceMoniker, string FriendlyName);

public sealed record VideoOverlayTransform(double X, double Y, double Width);

public static class VideoOverlayLayout
{
    public const double MinimumWidth = .05;
    public const double CameraAspectRatio = 16d / 9d;

    /// <summary>The corners a source can be assigned to, in reading order.</summary>
    public static readonly IReadOnlyList<string> Corners = ["Top Left", "Top Right", "Bottom Left", "Bottom Right"];

    public static VideoOverlayTransform Normalize(VideoOverlayTransform? transform, double aspectRatio)
    {
        var width = double.IsFinite(transform?.Width ?? double.NaN)
            ? Math.Clamp(transform!.Width, MinimumWidth, 1)
            : .25;
        var height = width / Math.Max(.01, aspectRatio);
        if (height > 1)
        {
            height = 1;
            width = Math.Max(MinimumWidth, height * aspectRatio);
        }

        var x = double.IsFinite(transform?.X ?? double.NaN) ? transform!.X : 0;
        var y = double.IsFinite(transform?.Y ?? double.NaN) ? transform!.Y : 0;
        return new(Math.Clamp(x, 0, 1 - width), Math.Clamp(y, 0, 1 - height), width);
    }

    public static VideoOverlayTransform Corner(string corner, double width, double aspectRatio)
    {
        var normalized = Normalize(new(0, 0, width), aspectRatio);
        return corner switch
        {
            "Top Right" => normalized with { X = 1 - normalized.Width },
            "Bottom Left" => normalized with { Y = 1 - normalized.Width / aspectRatio },
            "Bottom Right" => normalized with { X = 1 - normalized.Width, Y = 1 - normalized.Width / aspectRatio },
            _ => normalized
        };
    }
}

public enum VideoOverlayManipulationMode { Move, TopLeft, TopRight, BottomLeft, BottomRight }

public static class VideoOverlayManipulation
{
    // Pointer coordinates and returned transform are fractions of encoded frame.
    // Width remains source-relative; height is derived from output/source aspect.
    public static VideoOverlayTransform Apply(VideoOverlayTransform start, VideoOverlayManipulationMode mode,
        double deltaX, double deltaY, double outputAspect, double sourceAspect)
    {
        var aspect = Math.Max(.01, sourceAspect / Math.Max(.01, outputAspect));
        start = VideoOverlayLayout.Normalize(start, aspect);
        if (mode == VideoOverlayManipulationMode.Move)
            return VideoOverlayLayout.Normalize(new(start.X + deltaX, start.Y + deltaY, start.Width), aspect);

        var leftHandle = mode is VideoOverlayManipulationMode.TopLeft or VideoOverlayManipulationMode.BottomLeft;
        var topHandle = mode is VideoOverlayManipulationMode.TopLeft or VideoOverlayManipulationMode.TopRight;
        var fixedX = leftHandle ? start.X + start.Width : start.X;
        var fixedY = topHandle ? start.Y + start.Width / aspect : start.Y;
        // Horizontal pointer distance is authoritative.  It keeps source
        // aspect fixed and anchors the opposite resize corner.
        var width = leftHandle ? fixedX - (start.X + deltaX) : start.Width + deltaX;
        var maximumWidth = Math.Min(leftHandle ? fixedX : 1 - fixedX,
            (topHandle ? fixedY : 1 - fixedY) * aspect);
        width = Math.Clamp(width, VideoOverlayLayout.MinimumWidth, Math.Max(VideoOverlayLayout.MinimumWidth, maximumWidth));
        var height = width / aspect;
        var x = leftHandle ? fixedX - width : fixedX;
        var y = topHandle ? fixedY - height : fixedY;
        return VideoOverlayLayout.Normalize(new(x, y, width), aspect);
    }

}
