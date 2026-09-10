namespace ClypDat.App.Services;

/// <summary>Storage owned by one clip. Video identity stays separate because
/// probing, waveform caches and bitrate all describe only the video file.</summary>
public static class ClipStorageCalculator
{
    public static long Calculate(string libraryRoot, string videoPath, ClipInfo? info = null)
    {
        long total = Length(videoPath);
        var assets = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var path in ClipOverlayManifest.ExistingAssetPaths(libraryRoot, info?.OverlayManifest))
            if (assets.Add(path)) total = SaturatingAdd(total, Length(path));
        return total;
    }

    private static long Length(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return 0;
        try { return new FileInfo(path).Exists ? new FileInfo(path).Length : 0; }
        catch (Exception) { return 0; }
    }

    private static long SaturatingAdd(long left, long right) => right > 0 && left > long.MaxValue - right ? long.MaxValue : left + right;
}
