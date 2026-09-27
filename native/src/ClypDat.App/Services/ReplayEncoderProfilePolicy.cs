namespace ClypDat.App.Services;

/// <summary>
/// One recorder profile instead of exposing vendor-specific quality presets.
/// The native recorder sizes its encoder pipeline per backend
/// (encoder_backend.h); this only names the profile it is handed.
/// </summary>
public static class ReplayEncoderProfilePolicy
{
    public const string Automatic = "Automatic";

    public static string Resolve() => Automatic;
}
