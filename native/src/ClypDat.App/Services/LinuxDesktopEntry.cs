namespace ClypDat.App.Services;

internal static class LinuxDesktopEntry
{
    // Exec is parsed twice (desktop-entry escaping, then argument quoting).
    // Percent is a field-code escape even within quotes.
    public static string QuoteExec(string path)
    {
        if (!Path.IsPathRooted(path) || path.Any(char.IsControl))
            throw new ArgumentException("Desktop executable must be an absolute path without control characters.", nameof(path));
        var argument = path.Replace("\\", "\\\\").Replace("\"", "\\\"")
            .Replace("`", "\\`").Replace("$", "\\$").Replace("%", "%%");
        return "\"" + argument.Replace("\\", "\\\\") + "\"";
    }
}
