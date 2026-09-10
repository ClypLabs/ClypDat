using ClypDat.Core.Settings;

namespace ClypDat.App.Services;

// Capture provenance stays beside a clip rather than in app settings. Relative
// paths make copied libraries portable; placement is intentionally per clip.
public sealed record ClipOverlayManifest(
    int Version,
    ClipOverlayLayer? Camera = null,
    ClipOverlayLayer? Peripherals = null)
{
    // v6 bakes a custom keyboard's caps into the layer so a clip keeps drawing
    // the keys it was recorded with after the user edits or deletes that set.
    // v5 adds source offsets and playback rate to camera assets, plus input
    // v2. Keep v2-v4 readable: optional
    // record fields deserialize as null and are deliberately treated as
    // history that was never captured, not as an empty keyboard.
    public const int CurrentVersion = 6;
    /// <summary>The version that fixed camera asset timing. The legacy correction
    /// below stays pinned to it: writing it as "older than current" would silently
    /// re-admit every already-correct clip on the next version bump.</summary>
    private const int TimingCorrectedVersion = 5;
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
        // Already part of the picture: nothing left to draw, move or burn.
        if (layer.Flattened) return false;
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
        if (gameplayDurationSeconds <= 0 || manifest.Version >= TimingCorrectedVersion) return manifest;
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

    /// <summary>
    /// Marks burned layers as flattened and strips their assets. Both halves
    /// matter: Flattened stops playback drawing an editable copy over pixels
    /// that are already in the video, and clearing Assets keeps
    /// <see cref="ExistingAssetPaths"/> from handing a deleting caller the
    /// SOURCE clip's camera segments.
    /// </summary>
    public static ClipOverlayManifest? Flatten(ClipOverlayManifest? manifest, bool camera, bool peripherals)
    {
        if (manifest is null || (!camera && !peripherals)) return manifest;
        return manifest with
        {
            Camera = camera ? Flatten(manifest.Camera) : manifest.Camera,
            Peripherals = peripherals ? Flatten(manifest.Peripherals) : manifest.Peripherals,
        };
    }

    private static ClipOverlayLayer? Flatten(ClipOverlayLayer? layer) => layer is null
        ? null
        : layer with { Flattened = true, Assets = null, AssetPath = null, InputIndexPath = null };

    /// <summary>
    /// The board a layer draws, rebuilt from the caps baked into it, or null when
    /// it uses one of the built-in layouts that its name already describes.
    /// Clips recorded before v6 have no caps and take the null path.
    /// </summary>
    public static CustomKeyboardBoardShape? BoardOf(ClipOverlayLayer? layer)
    {
        if (layer?.Keys is not { } keys) return null;
        return new(Group(keys.Where(cap => cap.Row >= 0), ascending: true),
            Group(keys.Where(cap => cap.Row < 0), ascending: false), layer.ShowMouse);
    }

    /// <summary>The aspect a layer is placed at: its own when it carries a board,
    /// otherwise the catalog's for its named layout.</summary>
    public static double AspectOf(ClipOverlayLayer? layer) =>
        BoardOf(layer) is { } board
            ? CustomKeyboardBoard.AspectRatio(board)
            : KeyboardOverlayCatalog.Get(layer?.Source ?? KeyboardOverlayCatalog.QwertyCompact).AspectRatio;

    // Cluster rows ride on negative indices, so -1 is the row above -2.
    private static IReadOnlyList<IReadOnlyList<CustomKeyCap>> Group(IEnumerable<ClipOverlayKeyCap> caps, bool ascending)
    {
        var rows = caps.GroupBy(cap => cap.Row);
        rows = ascending ? rows.OrderBy(row => row.Key) : rows.OrderByDescending(row => row.Key);
        return rows.Select(row => (IReadOnlyList<CustomKeyCap>)row
            .Select(cap => new CustomKeyCap(cap.Code, cap.Label, cap.Units)).ToArray()).ToArray();
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
    string? InputIndexPath = null,
    IReadOnlyList<ClipOverlayKeyCap>? Keys = null,
    string? SourceName = null,
    bool ShowMouse = true);

/// <summary>
/// One key of a custom board, already packed: its row, its order within that row,
/// its final width and the label it draws. Baked rather than referenced, so a clip
/// is independent of a key set the user may later edit or delete - and independent
/// of the packing algorithm itself, which may change.
/// </summary>
public sealed record ClipOverlayKeyCap(string Code, string Label, int Row, double Units = 1);

public sealed record ClipOverlayInterval(double StartSeconds, double EndSeconds);
/// <summary>
/// A source range is explicit because clip trimming can start in the middle of
/// a camera segment. Legacy assets have offset zero and rate one.
/// </summary>
public sealed record ClipOverlayAsset(string AssetPath, double StartSeconds, double EndSeconds,
    double SourceOffsetSeconds = 0, double PlaybackRate = 1);
