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
    private const string MinimizedArgument = "--minimized";
    private const string PublishRestartArgument = "--publish-restart";
    private const string RestartArgument = "--restart";

    public static bool RequiresForegroundGameCheck(IEnumerable<string>? arguments) =>
        HasArgument(arguments, PublishRestartArgument) && !HasArgument(arguments, MinimizedArgument);

    public static LaunchPresentation Resolve(
        IEnumerable<string>? arguments,
        bool foregroundGameDetected = false,
        bool foregroundGameDetectionFailed = false)
    {
        if (HasArgument(arguments, MinimizedArgument))
            return LaunchPresentation.Minimized;
        if (HasArgument(arguments, PublishRestartArgument))
            return foregroundGameDetected || foregroundGameDetectionFailed
                ? LaunchPresentation.Minimized
                : LaunchPresentation.Normal;
        if (HasArgument(arguments, RestartArgument))
            return LaunchPresentation.Restart;
        return LaunchPresentation.Normal;
    }

    private static bool HasArgument(IEnumerable<string>? arguments, string expected) =>
        arguments?.Any(argument => string.Equals(argument, expected, StringComparison.OrdinalIgnoreCase)) == true;

    public static bool UsesStartupLoader(LaunchPresentation presentation) => presentation == LaunchPresentation.Normal;

    public static bool StartsInTray(LaunchPresentation presentation) => presentation == LaunchPresentation.Minimized;

    public static bool ActivatesAfterStartupLoader(LaunchPresentation presentation) => presentation == LaunchPresentation.Normal;
}
