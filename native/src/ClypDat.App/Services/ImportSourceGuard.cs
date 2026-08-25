using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;

namespace ClypDat.App.Services;

/// <summary>
/// Trust checks for third-party importer data.
///
/// The SteelSeries importer reads C:\ProgramData\SteelSeries\GG\apps\moments\db\database.db
/// and takes both the capture-scan root and each clip's path from it verbatim. The
/// default ACL on C:\ProgramData grants CREATE_CHILD to Users and gives CREATOR OWNER
/// full control of what it creates, so when SteelSeries GG is NOT installed any
/// unprivileged local user can create that whole directory chain and plant the database.
/// A planted row pointing at, say, a private video elsewhere on disk would make that file
/// appear as an importable clip - and importing it copies it into the ClypDat library,
/// or MOVES it out of its original location when the copy toggle is off.
/// </summary>
[SupportedOSPlatform("windows")]
public static class ImportSourceGuard
{
    // Owners a genuine machine-wide application database can legitimately have. A file
    // planted by an unprivileged user is owned by that user, which is what this rejects.
    private static readonly string[] TrustedOwnerSids =
    {
        "S-1-5-18",     // NT AUTHORITY\SYSTEM
        "S-1-5-32-544", // BUILTIN\Administrators
        "S-1-5-80-956008885-3418522649-1831038044-1853292631-2271478464", // NT SERVICE\TrustedInstaller
    };

    /// <summary>
    /// True when a machine-wide database is owned by SYSTEM, Administrators, or
    /// TrustedInstaller - i.e. was created by an installer rather than planted by a user.
    /// Fails closed: if the owner cannot be read, the file is not trusted.
    /// </summary>
    public static bool IsTrustedMachineWideDatabase(string path)
    {
        try
        {
            var owner = new FileInfo(path).GetAccessControl().GetOwner(typeof(SecurityIdentifier)) as SecurityIdentifier;
            if (owner is null) return false;

            foreach (var sid in TrustedOwnerSids)
            {
                if (owner.Value.Equals(sid, StringComparison.OrdinalIgnoreCase)) return true;
            }

            AppLog.Error($"Ignoring importer database {path}: owned by {owner.Value}, which is not SYSTEM or Administrators.");
            return false;
        }
        catch (Exception error)
        {
            AppLog.Error($"Ignoring importer database {path}: could not read its owner", error);
            return false;
        }
    }

    /// <summary>
    /// True when a path named by importer data is one ClypDat is willing to read or move.
    /// Rejects UNC paths, non-fixed drives, and reparse points - a symlink or junction
    /// would otherwise redirect a copy (or, with the move toggle, a relocation) to a
    /// target the importer never named.
    /// </summary>
    public static bool IsAllowedSourcePath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;

        string full;
        try
        {
            full = Path.GetFullPath(path);
        }
        catch
        {
            return false;
        }

        var root = Path.GetPathRoot(full);
        if (string.IsNullOrEmpty(root)) return false;
        if (full.StartsWith(@"\\", StringComparison.Ordinal)) return false;

        try
        {
            if (new DriveInfo(root).DriveType != DriveType.Fixed) return false;

            var attributes = File.GetAttributes(full);
            if (attributes.HasFlag(FileAttributes.ReparsePoint)) return false;
        }
        catch
        {
            return false;
        }

        return true;
    }
}
