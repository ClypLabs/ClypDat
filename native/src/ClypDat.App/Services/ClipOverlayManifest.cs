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

    /// <summary>
    /// Overlay assets are library-relative. Reject escaped references so a
    /// damaged sidecar cannot make the editor open an arbitrary local file.
    /// </summary>
    public static string? ResolveAssetPath(string libraryRoot, string? assetPath)
    {
        if (string.IsNullOrWhiteSpace(libraryRoot) || string.IsNullOrWhiteSpace(assetPath) || Path.IsPathRooted(assetPath)) return null;
        try
        {
            var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(libraryRoot)) + Path.DirectorySeparatorChar;
            var path = Path.GetFullPath(Path.Combine(root, assetPath));
            return path.StartsWith(root, StringComparison.OrdinalIgnoreCase) ? path : null;
        }
        catch (Exception) { return null; }
    }

    public static bool IsUsable(string libraryRoot, ClipOverlayLayer? layer) =>
        layer is { Available: true } && (string.IsNullOrWhiteSpace(layer.AssetPath) || File.Exists(ResolveAssetPath(libraryRoot, layer.AssetPath)));
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
