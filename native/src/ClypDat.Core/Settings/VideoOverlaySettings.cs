namespace ClypDat.Core.Settings;

// Coordinates are fractions of final encoded frame. This keeps layouts stable
// across capture resolutions and makes preview and recorder use same contract.
public sealed class VideoOverlaySettings
{
    public bool Enabled { get; set; }
    public bool IncludeVirtualCameras { get; set; } = true;
    public VideoOverlayCameraSelection? Camera { get; set; }
    public string KeyboardLayout { get; set; } = "QWERTY Compact";
    public VideoOverlayTransform CameraTransform { get; set; } = new(.70, .05, .25);
    public VideoOverlayTransform KeyboardTransform { get; set; } = new(.05, .70, .35);
}

public sealed record VideoOverlayCameraSelection(string DeviceMoniker, string FriendlyName);

public sealed record VideoOverlayTransform(double X, double Y, double Width);

public static class VideoOverlayLayout
{
    public const double MinimumWidth = .05;
    public const double CameraAspectRatio = 16d / 9d;

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
