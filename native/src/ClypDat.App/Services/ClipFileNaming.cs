using System.Globalization;
using System.Text.RegularExpressions;

namespace ClypDat.App.Services;

public static class ClipFileNaming
{
    public const string StandardScheme = "Standard";
    public const string ReadableScheme = "Readable";
    public const string CustomScheme = "Custom";

    // Matches a trailing date/time this class itself appended under either the
    // current "<title> - MMM-dd-yyyy - HH-mm-ss" scheme or the older
    // "<title> yyyy-MM-dd HH-mm-ss" one, so a title re-derived from a filename
    // that already has one of these suffixes doesn't carry the date along as
    // part of the "title".
    private static readonly Regex TrailingTimestampPattern = new(
        @"(\s-\s[A-Za-z]{3}-\d{2}-\d{4}\s-\s\d{2}-\d{2}-\d{2}|\s\d{4}-\d{2}-\d{2}\s\d{2}-\d{2}-\d{2})$",
        RegexOptions.Compiled);

    public static string StripTimestampSuffix(string nameWithoutExtension) =>
        TrailingTimestampPattern.Replace(nameWithoutExtension, string.Empty);

    // Windows forbids nine printable characters in a name, and users type them
    // constantly - "Tom Clancy's Rainbow Six: Siege", a clip called Who?, a
    // title in "quotes". Flattening them all to _ or - threw away what they
    // said; each maps to the nearest character Explorer will accept instead,
    // so the name still reads as the name. The two with no ASCII equivalent
    // use their full-width twins, which are ordinary filename characters.
    private static readonly Dictionary<char, string> InvalidCharacterReplacements = new()
    {
        ['"'] = "'",
        [':'] = ";",
        ['<'] = "(",
        ['>'] = ")",
        ['/'] = "-",
        ['\\'] = "-",
        ['|'] = "-",
        ['?'] = "？",
        ['*'] = "＊",
    };

    /// <summary>
    /// Makes one path segment (a filename stem or a folder name) legal on
    /// Windows while keeping it readable. Note this is only ever applied to
    /// what goes ON DISK - the clip's sidecar keeps the title and game name
    /// exactly as the user typed them, so the library still displays the real
    /// characters.
    /// </summary>
    public static string SanitizeSegment(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return string.Empty;

        var builder = new System.Text.StringBuilder(value.Length);
        foreach (var character in value)
        {
            if (InvalidCharacterReplacements.TryGetValue(character, out var replacement))
            {
                builder.Append(replacement);
            }
            else if (char.IsControl(character))
            {
                // No sensible stand-in for a control character, and nothing is
                // lost by dropping one.
            }
            else if (Array.IndexOf(Path.GetInvalidFileNameChars(), character) >= 0)
            {
                builder.Append('-');
            }
            else
            {
                builder.Append(character);
            }
        }

        // Windows silently strips these from the end of a name, so a file
        // written as "Game." comes back as "Game" and never matches again.
        return builder.ToString().Trim().TrimEnd('.', ' ');
    }

    public static string BuildBaseName(string gameDisplayName)
    {
        var name = SanitizeSegment(gameDisplayName);
        return string.IsNullOrWhiteSpace(name) ? "Replay" : name;
    }

    public static string BuildFileName(string gameDisplayName, DateTime timestamp, string extension)
    {
        return BuildFileName(gameDisplayName, timestamp, extension, StandardScheme, string.Empty, gameDisplayName);
    }

    public static string BuildFileName(string title, DateTime timestamp, string extension, string scheme, string customTemplate, string? gameDisplayName = null)
    {
        var stem = BuildStem(title, gameDisplayName, timestamp, scheme, customTemplate);
        return $"{stem}.{extension.TrimStart('.')}";
    }

    public static bool TryBuildPreview(string template, DateTime timestamp, string title, string gameDisplayName, out string preview, out string error)
    {
        try
        {
            preview = BuildStem(title, gameDisplayName, timestamp, CustomScheme, template);
            error = string.Empty;
            return true;
        }
        catch (Exception exception) when (exception is FormatException or ArgumentException)
        {
            preview = string.Empty;
            error = exception.Message;
            return false;
        }
    }

    public static string BuildUniquePath(string directory, string fileName)
    {
        var candidate = Path.Combine(directory, fileName);
        if (!File.Exists(candidate)) return candidate;

        var stem = Path.GetFileNameWithoutExtension(fileName);
        var extension = Path.GetExtension(fileName);
        for (var index = 2; ; index++)
        {
            candidate = Path.Combine(directory, $"{stem} ({index}){extension}");
            if (!File.Exists(candidate)) return candidate;
        }
    }

    private static string BuildStem(string title, string? gameDisplayName, DateTime timestamp, string scheme, string customTemplate)
    {
        string value;
        if (string.Equals(scheme, ReadableScheme, StringComparison.OrdinalIgnoreCase))
        {
            // Colons are not legal Windows filename characters, so use hyphens
            // for the time portion while preserving the requested readable form.
            value = $"{timestamp.ToString("MMMM", CultureInfo.InvariantCulture)} {timestamp.Day}{OrdinalSuffix(timestamp.Day)}, {timestamp:yyyy} - {timestamp:HH-mm-ss} - {title}";
        }
        else if (string.Equals(scheme, CustomScheme, StringComparison.OrdinalIgnoreCase))
        {
            value = ExpandTemplate(customTemplate, timestamp, title, gameDisplayName);
        }
        else
        {
            value = $"{title} - {timestamp.ToString("MMM-dd-yyyy", CultureInfo.InvariantCulture)} - {timestamp:HH-mm-ss}";
        }

        return SanitizeStem(value);
    }

    private static string ExpandTemplate(string template, DateTime timestamp, string title, string? gameDisplayName)
    {
        if (string.IsNullOrWhiteSpace(template)) throw new FormatException("Enter a filename template.");

        var result = System.Text.RegularExpressions.Regex.Replace(template, @"\{datetime:([^{}]*)\}", match =>
        {
            var format = match.Groups[1].Value;
            if (string.IsNullOrWhiteSpace(format)) throw new FormatException("The datetime token needs a format.");
            return timestamp.ToString(format, CultureInfo.CurrentCulture);
        });
        result = result.Replace("{title}", title, StringComparison.OrdinalIgnoreCase);
        result = result.Replace("{game}", string.IsNullOrWhiteSpace(gameDisplayName) ? title : gameDisplayName, StringComparison.OrdinalIgnoreCase);
        if (result.Contains('{') || result.Contains('}')) throw new FormatException("Use {title}, {game}, or {datetime:format} tokens only.");
        return result;
    }

    private static string SanitizeStem(string value)
    {
        value = SanitizeSegment(value);
        if (string.IsNullOrWhiteSpace(value)) value = "Replay";
        if (value.Length > 180) value = value[..180].TrimEnd('.', ' ');
        var reserved = new[] { "CON", "PRN", "AUX", "NUL", "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9", "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9" };
        return reserved.Contains(value, StringComparer.OrdinalIgnoreCase) ? $"_{value}" : value;
    }

    private static string OrdinalSuffix(int day)
    {
        if (day is >= 11 and <= 13) return "th";
        return (day % 10) switch { 1 => "st", 2 => "nd", 3 => "rd", _ => "th" };
    }
}
