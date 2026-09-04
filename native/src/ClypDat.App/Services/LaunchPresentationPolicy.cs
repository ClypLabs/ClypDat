namespace ClypDat.App.Services;

internal enum LaunchPresentation
{
    Normal,
    Restart,
    Minimized,
}

// Centralizes command-line presentation semantics so automatic launches cannot
// accidentally regain foreground behavior as startup code changes.
internal static class LaunchPresentationPolicy
{
    public static LaunchPresentation Resolve(IEnumerable<string>? arguments)
    {
        var values = arguments ?? [];
        if (values.Any(argument => string.Equals(argument, "--minimized", StringComparison.OrdinalIgnoreCase)))
            return LaunchPresentation.Minimized;
        if (values.Any(argument => string.Equals(argument, "--restart", StringComparison.OrdinalIgnoreCase)))
            return LaunchPresentation.Restart;
        return LaunchPresentation.Normal;
    }

    public static bool UsesStartupLoader(LaunchPresentation presentation) => presentation == LaunchPresentation.Normal;

    public static bool StartsInTray(LaunchPresentation presentation) => presentation == LaunchPresentation.Minimized;

    public static bool ActivatesAfterStartupLoader(LaunchPresentation presentation) => presentation == LaunchPresentation.Normal;
}
