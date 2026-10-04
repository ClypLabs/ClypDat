namespace ClypDat.App.Services;

/// <summary>Normalizes the user-selected NVIDIA NVENC preset.</summary>
public static class ReplayEncoderProfilePolicy
{
    public const string Automatic = "Automatic";
    public const string DefaultPreset = "P1";

    public static string Resolve(string? mode, string? requestedPreset)
    {
        if (!string.Equals(mode, "Manual", StringComparison.OrdinalIgnoreCase)) return Automatic;
        return NormalizePreset(requestedPreset);
    }

    public static string NormalizePreset(string? requestedPreset) => requestedPreset?.ToUpperInvariant() switch
    {
        "P1" => "P1", "P2" => "P2", "P3" => "P3", "P4" => "P4", "P5" => "P5", _ => DefaultPreset
    };
}
