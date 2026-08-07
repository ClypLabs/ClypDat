namespace ClypDat.App.Services;

/// <summary>
/// Normalizes the persisted encoder preset before it reaches FFmpeg.
/// </summary>
public static class ReplayEncoderPresetPolicy
{
    public static string Resolve(string? requestedPreset) => Normalize(requestedPreset);

    // P3-P5 are gone from the UI, but they are still sitting in the
    // settings.json of anyone who picked one before, so they have to be mapped
    // rather than merely rejected - a bare fallback would silently move a user
    // who deliberately chose P5 down to the cheapest option. P2 is the closest
    // surviving preset in both cost and picture, so every removed preset lands
    // there, same as an unrecognised value.
    private static string Normalize(string? preset) => preset?.ToUpperInvariant() switch
    {
        "P1" => "P1",
        _ => "P2"
    };
}
