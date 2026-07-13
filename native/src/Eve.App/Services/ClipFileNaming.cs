using System.Globalization;

namespace Eve.App.Services;

public static class ClipFileNaming
{
    public static string BuildBaseName(string gameDisplayName)
    {
        var name = string.IsNullOrWhiteSpace(gameDisplayName) ? "Replay" : gameDisplayName.Trim();
        foreach (var invalid in Path.GetInvalidFileNameChars())
        {
            name = name.Replace(invalid, '_');
        }

        return name;
    }

    // "Counter-Strike 2 - Jul-27-2026 - 17-38-06" - InvariantCulture so the
    // month abbreviation stays "Jul" regardless of the machine's locale.
    public static string BuildFileName(string gameDisplayName, DateTime timestamp, string extension)
    {
        return $"{BuildBaseName(gameDisplayName)} - {timestamp.ToString("MMM-dd-yyyy", CultureInfo.InvariantCulture)} - {timestamp:HH-mm-ss}.{extension.TrimStart('.')}";
    }
}
