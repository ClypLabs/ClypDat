using System.Globalization;
using ClypDat.Core.Settings;

namespace ClypDat.App.Services;

/// <summary>
/// Where a captured overlay lands in an exported frame, and when it is visible.
/// Pure arithmetic, deliberately separate from anything that spawns FFmpeg, so
/// the parts that are easy to get subtly wrong can be tested directly.
/// </summary>
internal static class ClipOverlayBurnLayout
{
    /// <summary>
    /// Destination rectangle in output-frame pixels.
    ///
    /// Note the asymmetry, which is inherited from the editor preview and has to
    /// be preserved or the burn lands somewhere the user did not put it: X is a
    /// fraction of frame width and Y a fraction of frame height, but the
    /// overlay's pixel height comes from its own pixel width divided by its
    /// aspect - not from a normalized height.
    /// </summary>
    public static SpotifyOverlayBounds Resolve(VideoOverlayTransform transform, double aspectRatio, int frameWidth, int frameHeight)
    {
        var safeAspect = double.IsFinite(aspectRatio) && aspectRatio > .01 ? aspectRatio : 1;
        var normalized = VideoOverlayLayout.Normalize(transform, safeAspect);
        var width = Even(Math.Max(2, (int)Math.Round(frameWidth * normalized.Width)));
        var height = Even(Math.Max(2, (int)Math.Round(width / safeAspect)));
        var x = Math.Clamp((int)Math.Round(frameWidth * normalized.X), 0, Math.Max(0, frameWidth - width));
        var y = Math.Clamp((int)Math.Round(frameHeight * normalized.Y), 0, Math.Max(0, frameHeight - height));
        return new SpotifyOverlayBounds(x, y, width, height);
    }

    /// <summary>
    /// The stretches of the OUTPUT timeline the camera actually covers.
    ///
    /// Trim is an input option applied to input 0 only, so output time starts at
    /// zero at the trim point while manifest ranges are clip-relative - hence the
    /// shift. Speed divides, because a 2x export puts four clip seconds into two
    /// output ones.
    /// </summary>
    public static IReadOnlyList<(double Start, double End)> Coverage(
        IReadOnlyList<ClipOverlayAsset>? assets, double trimStartSeconds, double trimEndSeconds, double speed)
    {
        if (assets is not { Count: > 0 }) return [];
        var rate = double.IsFinite(speed) && speed > 0 ? speed : 1;
        var frame = 1.0 / ClipOverlayBurn.FrameRate;
        var spans = new List<(double Start, double End)>();
        foreach (var asset in assets.OrderBy(asset => asset.StartSeconds))
        {
            var start = (Math.Max(asset.StartSeconds, trimStartSeconds) - trimStartSeconds) / rate;
            var end = (Math.Min(asset.EndSeconds, trimEndSeconds) - trimStartSeconds) / rate;
            // Anything trimmed away entirely, or too short to survive a frame,
            // contributes nothing.
            if (end - start <= frame) continue;
            spans.Add((Math.Max(0, start), end));
        }
        if (spans.Count == 0) return [];
        var merged = new List<(double Start, double End)> { spans[0] };
        foreach (var span in spans.Skip(1))
        {
            var last = merged[^1];
            // Consecutive capture segments are contiguous; joining them keeps
            // the enable expression to a single term in the common case.
            if (span.Start - last.End <= frame) merged[^1] = (last.Start, Math.Max(last.End, span.End));
            else merged.Add(span);
        }
        return merged;
    }

    /// <summary>
    /// An ffmpeg <c>enable</c> expression, or null when the overlay is visible
    /// for the whole export and the option can be left off entirely.
    ///
    /// This is what makes a gap in camera coverage *disappear* rather than
    /// freeze on its last frame: <c>enable=0</c> passes the main frame through
    /// untouched. <c>eof_action</c> only covers the end of a stream, not holes
    /// in the middle of one.
    /// </summary>
    public static string? Enable(IReadOnlyList<(double Start, double End)> coverage, double outputDurationSeconds)
    {
        if (coverage.Count == 0) return null;
        var frame = 1.0 / ClipOverlayBurn.FrameRate;
        if (coverage.Count == 1 && coverage[0].Start <= frame && coverage[0].End >= outputDurationSeconds - frame) return null;
        // '+' is OR in ffmpeg's expression language. Invariant culture is not
        // optional: a comma decimal separator turns between(t,0,28.5) into
        // between(t,0,28,5) and fails the whole encode.
        return string.Join("+", coverage.Select(span =>
            $"between(t,{Format(span.Start)},{Format(span.End)})"));
    }

    private static string Format(double value) => value.ToString("0.###", CultureInfo.InvariantCulture);
    private static int Even(int value) => value - (value % 2);
}
