namespace ClypDat.App.Services;

// Native WGC owns one bounded frame pool and captures the target HWND through
// occlusion and Alt-Tab. ScreenRecorderLib's rotating WGC implementation stays
// behind explicit Legacy selection because its long-session resource leak is
// unrelated to this source.
internal static class HybridCaptureBackendPolicy
{
    internal const string ForceDxgiVariable = "CLYPDAT_FORCE_DXGI";

    public static bool UseWgcForGame(string? forceDxgiValue) =>
        !string.Equals(forceDxgiValue, "1", StringComparison.Ordinal);
}
