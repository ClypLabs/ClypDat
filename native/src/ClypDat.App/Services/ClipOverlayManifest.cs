using ClypDat.Core.Settings;

namespace ClypDat.App.Services;

// Capture provenance stays beside a clip rather than in app settings. Relative
// paths make copied libraries portable; placement is intentionally per clip.
public sealed record ClipOverlayManifest(
    int Version,
    ClipOverlayLayer? Camera = null,
    ClipOverlayLayer? Peripherals = null)
{
    public const int CurrentVersion = 1;
    public static ClipOverlayManifest Empty { get; } = new(CurrentVersion);
}

public sealed record ClipOverlayLayer(
    string Source,
    bool Available,
    IReadOnlyList<ClipOverlayInterval>? Intervals = null,
    string? AssetPath = null,
    VideoOverlayTransform? InitialTransform = null,
    string? Error = null,
    bool Flattened = false);

public sealed record ClipOverlayInterval(double StartSeconds, double EndSeconds);
