using System.Text.RegularExpressions;

namespace ClypDat.App.Services;

/// <summary>
/// The local player's Fortnite display name, read from the game's own log.
///
/// The kill feed shows every player's eliminations, not just yours, and the
/// only thing marking a line as yours is that your name renders green - a
/// signal the detector pipeline cannot see, because it crops the luminance
/// plane and OCR returns no colour. Fortnite prints the name once at login:
///
///   LogOnlineAccount: Display: [...][process_user_login] Successfully logged
///   in user. UserId=[...] DisplayName=[Arashii ッ] EpicAccountId=[MCP:...]
///
/// so an exact string is available for the cost of one read. Note this is the
/// only event source Fortnite's log is good for - it never logs kills.
/// </summary>
public static partial class FortniteIdentity
{
    private static string? _cached;

    public static string LogFolder => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "FortniteGame", "Saved", "Logs");

    /// <summary>
    /// Re-read on every detector start rather than cached for the process: a
    /// second Epic account on the same machine would otherwise keep matching
    /// the previous player's feed lines.
    /// </summary>
    public static string? Resolve(string? logFolder = null)
    {
        try
        {
            var folder = logFolder ?? LogFolder;
            if (!Directory.Exists(folder)) return _cached;
            // Newest first: the live log is FortniteGame.log, but a session that
            // has just rotated leaves the name only in the newest backup.
            foreach (var file in new DirectoryInfo(folder)
                         .EnumerateFiles("FortniteGame*.log")
                         .OrderByDescending(item => item.LastWriteTimeUtc))
            {
                if (ReadDisplayName(file.FullName) is { } name)
                {
                    _cached = name;
                    return name;
                }
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
        }
        return _cached;
    }

    private static string? ReadDisplayName(string path)
    {
        try
        {
            // Shared read: Fortnite holds the live log open while playing.
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var reader = new StreamReader(stream);
            string? found = null;
            while (reader.ReadLine() is { } line)
            {
                if (!line.Contains("Successfully logged in user", StringComparison.Ordinal)) continue;
                var match = DisplayNameRegex().Match(line);
                // Keep scanning: the last login in the file is the current one.
                if (match.Success) found = match.Groups["name"].Value.Trim();
            }
            return string.IsNullOrWhiteSpace(found) ? null : found;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>
    /// Compares the way OCR sees names rather than the way the game stores
    /// them. "Arashii ッ" comes back with the small katakana dropped, mangled or
    /// spaced differently depending on the frame, so both sides are reduced to
    /// their letters and digits before comparing.
    /// </summary>
    public static string Fold(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return string.Empty;
        Span<char> buffer = stackalloc char[value.Length];
        var length = 0;
        foreach (var character in value)
        {
            if (char.IsAsciiLetterOrDigit(character)) buffer[length++] = char.ToUpperInvariant(character);
        }
        return new string(buffer[..length]);
    }

    public static bool IsLocalPlayer(string? candidate, string? localName)
    {
        var folded = Fold(localName);
        // No name resolved means no way to tell whose kill it was; callers
        // decide whether to fall back to clipping everything.
        return folded.Length > 0 && string.Equals(Fold(candidate), folded, StringComparison.Ordinal);
    }

    [GeneratedRegex(@"DisplayName=\[(?<name>[^\]]+)\]", RegexOptions.CultureInvariant)]
    private static partial Regex DisplayNameRegex();
}
