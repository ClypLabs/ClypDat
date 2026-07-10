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

    public static string BuildFileName(string gameDisplayName, DateTime timestamp, string extension)
    {
        return $"{BuildBaseName(gameDisplayName)} {timestamp:yyyy-MM-dd HH-mm-ss}.{extension.TrimStart('.')}";
    }
}
