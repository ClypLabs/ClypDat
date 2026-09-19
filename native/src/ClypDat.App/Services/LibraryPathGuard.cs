namespace ClypDat.App.Services;

internal static class LibraryPathGuard
{
    // Validate before opening a sidecar-controlled path. Lexical containment
    // alone does not stop a junction or symlink from redirecting the operation.
    internal static bool IsWithin(string root, string path)
    {
        try
        {
            var fullRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
            var full = Path.GetFullPath(path);
            var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
            var prefix = Path.EndsInDirectorySeparator(fullRoot) ? fullRoot : fullRoot + Path.DirectorySeparatorChar;
            if (string.Equals(full, fullRoot, comparison) || !full.StartsWith(prefix, comparison)) return false;
            for (string? current = full; current is not null; current = Path.GetDirectoryName(current))
            {
                try
                {
                    if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0) return false;
                }
                catch (FileNotFoundException) { }
                catch (DirectoryNotFoundException) { }
            }
            return true;
        }
        catch { return false; }
    }
}
