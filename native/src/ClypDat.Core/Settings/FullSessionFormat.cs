namespace ClypDat.Core.Settings;

public static class FullSessionFormat
{
    public static string Normalize(string? value) => string.Equals(value, "MP4", StringComparison.OrdinalIgnoreCase) ? "MP4" : "MKV";
    public static string Extension(string? value) => Normalize(value).ToLowerInvariant();
    public const string Recommendation = "Recommended for Full Session recording. Saves video and separate audio tracks as you play. Completed footage can usually be recovered if ClypDat or your computer crashes.";
    public const string Warning = "A computer crash can leave an MP4 recording incomplete or cause recent footage to be lost. Choose MKV (recommended) for Full Session recording.";
}
