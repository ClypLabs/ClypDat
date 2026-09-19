namespace ClypDat.Core.Settings;

public static class FullSessionFormat
{
    public static string Normalize(string? value) => string.Equals(value, "MP4", StringComparison.OrdinalIgnoreCase) ? "MP4" : "MKV";
    public static string Extension(string? value) => Normalize(value).ToLowerInvariant();
    public const string Recommendation = "Recommended for Full Session recording. Saves video and separate audio tracks as you play. Completed footage can usually be recovered if ClypDat or your computer crashes.";
    public const string Warning = "MP4 is not recommended for long-term recording. If a recording crashes or is interrupted, the entire file will become corrupted, unrecoverable, or completely lost.";
}
