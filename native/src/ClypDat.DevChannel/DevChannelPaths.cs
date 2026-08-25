using System.Text.RegularExpressions;

namespace ClypDat.DevChannel;

/// <summary>
/// One definition of where the Dev channel keeps staged builds, shared by the updater
/// that writes them and the launcher that runs them.
///
/// These used to be computed independently: DevUpdateService staged verified payloads
/// under %LOCALAPPDATA%\ClypDat-Dev\versions, while DevLauncher looked for them under
/// {install}\versions. The state.json paths agreed, so nothing looked broken - but
/// ActivatePending never found the staged build and returned early, stranding every
/// RSA-verified update, and the launcher then fell through to running whatever
/// directory sorted highest.
/// </summary>
public static class DevChannelPaths
{
    public const string DataFolderName = "ClypDat-Dev";

    public static string DataRoot => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), DataFolderName);

    public static string VersionsRootFor(string dataRoot) => Path.Combine(dataRoot, "versions");
    public static string StatePathFor(string dataRoot) => Path.Combine(dataRoot, "state.json");

    // Build ids are "<40-hex clypdat commit>-<7-hex avalonia commit>", the same shape
    // DevUpdateService asserts against the signed manifest's BuildIdSource. Anything
    // else is not something this channel produced.
    private static readonly Regex BuildIdPattern = new("^[0-9a-f]{40}-[0-9a-f]{7}$", RegexOptions.Compiled);

    /// <summary>
    /// True when the value is a well-formed build id. Rejects path separators, "." and
    /// "..", and anything that did not come from the Dev pipeline - the launcher's
    /// previous check blocked only / \ and :, so ".." resolved to the install root, and
    /// any directory name sorting above a hex SHA (such as "zzzzzzzz") would be picked
    /// up by the fallback scan and executed.
    /// </summary>
    public static bool IsValidBuildId(string? buildId) =>
        !string.IsNullOrEmpty(buildId) && BuildIdPattern.IsMatch(buildId);
}
