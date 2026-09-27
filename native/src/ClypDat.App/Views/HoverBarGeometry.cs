namespace ClypDat.App.Views;

internal static class HoverBarGeometry
{
    // All coordinates and sizes here are physical pixels. Avalonia's Width and
    // Height remain DIPs; only the native rectangle decides whether Win32 needs
    // another move or resize after a monitor scale transition.
    internal static bool MatchesNative(int left, int top, int right, int bottom,
        int targetLeft, int targetTop, int targetWidth, int targetHeight) =>
        left == targetLeft && top == targetTop &&
        right - left == targetWidth && bottom - top == targetHeight;

    internal static int PixelSize(double dips, double scale) =>
        Math.Max(1, (int)Math.Round(dips * (scale > 0 ? scale : 1)));
}

internal static class HoverBarEligibility
{
    internal static string? BlockedReason(bool hasViewModel, bool mainVisible, bool editorVisible,
        bool fullscreen, bool hasPlayback, bool loading, bool covered)
    {
        if (!hasViewModel) return "no view model";
        if (!mainVisible) return "main window hidden";
        if (!editorVisible) return "editor hidden";
        if (fullscreen) return "fullscreen";
        if (!hasPlayback) return "no playback";
        if (loading) return "video loading";
        return covered ? "covered" : null;
    }
}
