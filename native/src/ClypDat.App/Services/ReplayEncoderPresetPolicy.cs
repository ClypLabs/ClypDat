namespace ClypDat.App.Services;

/// <summary>
/// Keeps high-frame-rate replay capture ahead of uncapped gameplay. A game
/// with VSync disabled can consume nearly all GPU scheduling time; P1 leaves
/// NVENC enough headroom to keep a real 60 FPS timeline instead of dropping
/// frames from a full encode queue.
/// </summary>
public static class ReplayEncoderPresetPolicy
{
    public const int ProtectedFrameRate = 60;

    public static string Resolve(string? requestedPreset, int frameRate, string? backend)
    {
        var preset = Normalize(requestedPreset);
        if (frameRate < ProtectedFrameRate) return preset;

        return ResolveBackend(backend) is ReplayBackendOption.Auto or ReplayBackendOption.Native
            ? "P1"
            : preset;
    }

    public static bool RequiresFastPreset(int frameRate, string? backend) =>
        frameRate >= ProtectedFrameRate &&
        ResolveBackend(backend) is ReplayBackendOption.Auto or ReplayBackendOption.Native;

    private static ReplayBackendOption ResolveBackend(string? backend) =>
        Enum.TryParse<ReplayBackendOption>(backend, ignoreCase: true, out var parsed)
            ? parsed
            : ReplayBackendOption.Auto;

    private static string Normalize(string? preset) => preset?.ToUpperInvariant() switch
    {
        "P1" or "P2" or "P3" or "P4" or "P5" => preset.ToUpperInvariant(),
        _ => "P4"
    };
}
