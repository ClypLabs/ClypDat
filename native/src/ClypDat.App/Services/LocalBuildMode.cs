namespace ClypDat.App.Services;

// A build that build.ps1 published into %LOCALAPPDATA%\Programs\ClypDat - the
// current worktree or a branch - rather than a release. It keeps everything a
// release has: its real version (About shows "v1.6.0+<commit>"), the normal
// ClypDat data root, settings, library and caches, the usual names and icons.
// The one difference: it is not on the Stable release line, so the Stable
// updater never offers or installs a public release over it. Updating it is
// publishing it again.
//
// Unlike the Dev channel (ClypDatChannel=Dev), which is its own product with its
// own data root and updater, this changes nothing else. Set by the
// ClypDatLocalBuild MSBuild property; release and CI builds never set it.
public static class LocalBuildMode
{
#if CLYPDAT_LOCAL_BUILD
    public static bool Enabled => true;
#else
    public static bool Enabled => false;
#endif

    public const string UpdatesSuppressedMessage =
        "Stable updates are suppressed: this is a locally published development build, and a release would replace it.";

    private static int _logged;

    // Once per run: the update check repeats on a timer.
    public static void LogUpdatesSuppressed()
    {
        if (Interlocked.Exchange(ref _logged, 1) == 0) AppLog.Info(UpdatesSuppressedMessage);
    }
}

// Whether the startup loader installs an update on its own. The user's
// "install updates on launch" setting and "skip this version" still decide for
// a release; a locally published build never installs one.
public static class StartupUpdatePolicy
{
    public static bool ShouldInstall(bool localBuild, bool installUpdatesOnLaunch, string? ignoredUpdateVersion, string candidateTag) =>
        !localBuild && installUpdatesOnLaunch &&
        !string.Equals(ignoredUpdateVersion, candidateTag, StringComparison.OrdinalIgnoreCase);
}
