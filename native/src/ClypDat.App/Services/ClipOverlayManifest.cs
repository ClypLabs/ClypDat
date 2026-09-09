using ClypDat.Core.Settings;

namespace ClypDat.App.Services;

// Capture provenance stays beside a clip rather than in app settings. Relative
// paths make copied libraries portable; placement is intentionally per clip.
public sealed record ClipOverlayManifest(
    int Version,
    ClipOverlayLayer? Camera = null,
    ClipOverlayLayer? Peripherals = null)
{
    // v4 adds an owned input-history index.  Keep v2/v3 readable: optional
    // record fields deserialize as null and are deliberately treated as
    // history that was never captured, not as an empty keyboard.
    public const int CurrentVersion = 4;
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

    public static bool IsUsable(string libraryRoot, ClipOverlayLayer? layer)
    {
        if (layer is not { Available: true }) return false;
        var assets = layer.Assets;
        if (assets is { Count: > 0 }) return assets.All(asset => File.Exists(ResolveAssetPath(libraryRoot, asset.AssetPath)));
        return string.IsNullOrWhiteSpace(layer.AssetPath) || File.Exists(ResolveAssetPath(libraryRoot, layer.AssetPath));
    }

    /// <summary>
    /// Old builds measured camera assets against requested replay window. A
    /// short retained video could put every usable asset after clip end. Keep
    /// original metadata untouched; expose a marked in-memory approximation.
    /// </summary>
    public static ClipOverlayManifest ForPlayback(ClipOverlayManifest manifest, double gameplayDurationSeconds)
    {
        if (gameplayDurationSeconds <= 0 || manifest.Version >= CurrentVersion) return manifest;
        return manifest with { Camera = CorrectLegacyTiming(manifest.Camera, gameplayDurationSeconds) };
    }

    private static ClipOverlayLayer? CorrectLegacyTiming(ClipOverlayLayer? layer, double duration)
    {
        if (layer?.Assets is not { Count: > 0 } assets || layer.SynchronizationApproximate) return layer;
        var earliest = assets.Min(asset => asset.StartSeconds);
        var latest = assets.Max(asset => asset.EndSeconds);
        if (earliest < duration || latest <= duration) return layer;
        var correction = latest - duration;
        var corrected = assets.Select(asset => asset with
        {
            StartSeconds = Math.Clamp(asset.StartSeconds - correction, 0, duration),
            EndSeconds = Math.Clamp(asset.EndSeconds - correction, 0, duration)
        }).Where(asset => asset.EndSeconds > asset.StartSeconds).ToArray();
        return corrected.Length == 0 ? layer : layer with
        {
            Assets = corrected,
            SynchronizationApproximate = true,
            Error = "Camera timing recovered approximately from legacy capture metadata."
        };
    }

    /// <summary>Returns only references which are safe to delete from this library.</summary>
    public static IEnumerable<string> ExistingAssetPaths(string libraryRoot, ClipOverlayManifest? manifest)
    {
        foreach (var layer in new[] { manifest?.Camera, manifest?.Peripherals })
        {
            var path = ResolveAssetPath(libraryRoot, layer?.AssetPath);
            if (path is not null && File.Exists(path)) yield return path;
            if (layer?.Assets is not { Count: > 0 }) continue;
            foreach (var asset in layer.Assets)
            {
                path = ResolveAssetPath(libraryRoot, asset.AssetPath);
                if (path is not null && File.Exists(path)) yield return path;
            }
        }
    }
}

public sealed record ClipOverlayLayer(
    string Source,
    bool Available,
    IReadOnlyList<ClipOverlayInterval>? Intervals = null,
    string? AssetPath = null,
    VideoOverlayTransform? InitialTransform = null,
    string? Error = null,
    bool Flattened = false,
    IReadOnlyList<ClipOverlayAsset>? Assets = null,
    bool SynchronizationApproximate = false,
    string? InputIndexPath = null);

public sealed record ClipOverlayInterval(double StartSeconds, double EndSeconds);
public sealed record ClipOverlayAsset(string AssetPath, double StartSeconds, double EndSeconds);
