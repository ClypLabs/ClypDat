namespace ClypDat.App.Services;

/// <summary>
/// Normalizes the persisted encoder preset before it reaches FFmpeg.
/// </summary>
public static class ReplayEncoderPresetPolicy
{
    public static string Resolve(string? requestedPreset) => Normalize(requestedPreset);

    private static string Normalize(string? preset) => preset?.ToUpperInvariant() switch
    {
        "P1" or "P2" or "P3" or "P4" or "P5" => preset.ToUpperInvariant(),
        _ => "P4"
    };
}
