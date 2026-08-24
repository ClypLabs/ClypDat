namespace ClypDat.App.Services;

// Native WGC owns one bounded frame pool and captures the target HWND through
// occlusion and Alt-Tab. ScreenRecorderLib's rotating WGC implementation stays
// behind explicit Legacy selection because its long-session resource leak is
// unrelated to this source.
internal static class HybridCaptureBackendPolicy
{
    public static bool UseWgcForGame(string? _) => true;

    public static bool UseDxgiForDesktop(bool isMonitorMode) => isMonitorMode;
}
