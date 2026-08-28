using System.Globalization;

namespace ClypDat.App.Services;

/// <summary>
/// The editor's non-destructive effects (clip speed, aspect crop) expressed as
/// ffmpeg filter fragments.
/// </summary>
/// <remarks>
/// Three separate argument builders in MainWindowViewModel produce ffmpeg command
/// lines for the same clip - Save Trim, Export and Share - and every one of them
/// has to apply the same effects the editor is previewing, or the file the user
/// gets back is not what they were looking at. Building the fragments here keeps
/// the three in step and keeps the atempo chaining rule (below) in one place
/// rather than in three.
/// </remarks>
public static class ClipRenderFilters
{
    public const double MinSpeed = 0.25;
    public const double MaxSpeed = 4.0;
    public const string NoCrop = "None";

    // Presented in this order by the Effects sidebar. Keys are what lands in the
    // clip's sidecar, so they are stable strings, not an enum ordinal.
    public static readonly IReadOnlyList<string> CropModes = new[] { NoCrop, "16:9", "9:16", "1:1", "4:5" };

    // Slow exports remain valid for existing clips, but are intentionally not
    // offered as editor controls: their preview is impractically sluggish.
    public static readonly IReadOnlyList<double> SpeedPresets = new[] { 1.0, 1.5, 2.0, 4.0 };

    public static double NormalizeSpeed(double speed)
    {
        if (double.IsNaN(speed) || double.IsInfinity(speed) || speed <= 0) return 1.0;
        return Math.Clamp(speed, MinSpeed, MaxSpeed);
    }

    public static string NormalizeCropMode(string? mode)
    {
        if (string.IsNullOrWhiteSpace(mode)) return NoCrop;
        return CropModes.FirstOrDefault(candidate => string.Equals(candidate, mode, StringComparison.OrdinalIgnoreCase)) ?? NoCrop;
    }

    // A speed within a frame's worth of 1.0 is not worth an extra filter pass -
    // setpts/atempo on a "1.001x" clip costs a full re-encode of the audio for
    // nothing audible.
    public static bool IsSpeedActive(double speed) => Math.Abs(NormalizeSpeed(speed) - 1.0) > 0.005;

    public readonly record struct CropRect(int X, int Y, int Width, int Height);

    /// <summary>
    /// The largest rectangle of the requested aspect that fits inside the source,
    /// positioned by a 0..1 offset on each axis. Null when no crop applies.
    /// </summary>
    public static CropRect? ComputeCrop(string? mode, double offsetX, double offsetY, int sourceWidth, int sourceHeight)
    {
        var normalized = NormalizeCropMode(mode);
        if (normalized == NoCrop || sourceWidth <= 0 || sourceHeight <= 0) return null;

        var parts = normalized.Split(':');
        if (parts.Length != 2 ||
            !double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var aspectWidth) ||
            !double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var aspectHeight) ||
            aspectWidth <= 0 || aspectHeight <= 0)
        {
            return null;
        }

        var targetAspect = aspectWidth / aspectHeight;
        var sourceAspect = (double)sourceWidth / sourceHeight;

        int width, height;
        if (sourceAspect > targetAspect)
        {
            // Source is wider than the target: keep full height, cut the sides.
            height = sourceHeight;
            width = (int)Math.Round(sourceHeight * targetAspect, MidpointRounding.AwayFromZero);
        }
        else
        {
            width = sourceWidth;
            height = (int)Math.Round(sourceWidth / targetAspect, MidpointRounding.AwayFromZero);
        }

        // Every encoder here wants even dimensions (4:2:0 chroma), and an odd
        // width silently fails the encode rather than rounding itself.
        width = Math.Clamp(EvenDown(width), 2, EvenDown(sourceWidth));
        height = Math.Clamp(EvenDown(height), 2, EvenDown(sourceHeight));

        var x = EvenDown((int)Math.Round((sourceWidth - width) * Math.Clamp(offsetX, 0, 1), MidpointRounding.AwayFromZero));
        var y = EvenDown((int)Math.Round((sourceHeight - height) * Math.Clamp(offsetY, 0, 1), MidpointRounding.AwayFromZero));

        // Nothing to do if the source already is the requested aspect.
        if (width == sourceWidth && height == sourceHeight) return null;
        return new CropRect(x, y, width, height);
    }

    /// <summary>
    /// Video filter chain for the given effects, plus any caller-supplied tail
    /// (Share appends its scale/fps downscale). Null when there is nothing to do.
    /// </summary>
    public static string? BuildVideoFilter(CropRect? crop, double speed, string? tail = null)
    {
        var stages = new List<string>();
        if (crop is { } rect) stages.Add($"crop={rect.Width}:{rect.Height}:{rect.X}:{rect.Y}");
        // Divides, not multiplies: setpts rewrites each frame's presentation
        // timestamp, so halving the timestamps is what plays the clip twice as
        // fast.
        if (IsSpeedActive(speed)) stages.Add($"setpts=PTS/{Format(NormalizeSpeed(speed))}");
        if (!string.IsNullOrWhiteSpace(tail)) stages.Add(tail!);
        return stages.Count == 0 ? null : string.Join(",", stages);
    }

    /// <summary>
    /// Audio tempo chain, or an empty string when the clip plays at 1x.
    /// </summary>
    /// <remarks>
    /// atempo only accepts 0.5-2.0 per instance. Older clips can still contain
    /// slow speeds, so anything outside that window has to be reached by chaining
    /// instances (4x is atempo=2,atempo=2). Feeding it 4.0 directly does not
    /// clamp - ffmpeg rejects the filter and the whole export fails.
    /// </remarks>
    public static string BuildAudioSpeedFilter(double speed)
    {
        var rate = NormalizeSpeed(speed);
        if (!IsSpeedActive(rate)) return string.Empty;

        var stages = new List<string>();
        var remaining = rate;
        while (remaining > 2.0)
        {
            stages.Add("atempo=2.0");
            remaining /= 2.0;
        }
        while (remaining < 0.5)
        {
            stages.Add("atempo=0.5");
            remaining /= 0.5;
        }
        stages.Add($"atempo={Format(remaining)}");
        return string.Join(",", stages);
    }

    /// <summary>
    /// How long the encoded output actually runs, given a trimmed source length.
    /// </summary>
    public static double AdjustDuration(double seconds, double speed) =>
        Math.Max(0.1, seconds / NormalizeSpeed(speed));

    /// <summary>
    /// LibVLC's crop geometry string ("WxH+X+Y"), used for the editor's live
    /// preview. Empty string clears the crop - libvlc treats null as "unchanged".
    /// </summary>
    public static string ToVlcCropGeometry(CropRect? crop) =>
        crop is { } rect ? $"{rect.Width}x{rect.Height}+{rect.X}+{rect.Y}" : string.Empty;

    private static int EvenDown(int value) => value - (value % 2);

    private static string Format(double value) => value.ToString("0.####", CultureInfo.InvariantCulture);
}
