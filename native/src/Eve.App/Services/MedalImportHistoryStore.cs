using System.Text.Json;

namespace Eve.App.Services;

// Import history belongs to a library: it needs to travel with the library
// rather than remain in the machine-specific settings file.
public static class MedalImportHistoryStore
{
    private const string FileName = "medal-imports.json";
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public static bool TryLoad(string libraryRoot, out HashSet<string> keys)
    {
        keys = new HashSet<string>(StringComparer.Ordinal);
        if (string.IsNullOrWhiteSpace(libraryRoot)) return false;

        var path = Path.Combine(LibraryLayout.ClipInfoRoot(libraryRoot), FileName);
        if (!File.Exists(path)) return true;

        try
        {
            var history = JsonSerializer.Deserialize<MedalImportHistory>(File.ReadAllText(path));
            if (history?.ImportedClipKeys is not null) keys.UnionWith(history.ImportedClipKeys.Where(key => !string.IsNullOrWhiteSpace(key)));
            return true;
        }
        catch (Exception error)
        {
            AppLog.Error($"Medal import history read failed: {path}", error);
            return false;
        }
    }

    public static bool TrySave(string libraryRoot, IEnumerable<string> keys)
    {
        if (string.IsNullOrWhiteSpace(libraryRoot)) return false;

        var path = Path.Combine(LibraryLayout.ClipInfoRoot(libraryRoot), FileName);
        var temporaryPath = path + ".tmp";
        try
        {
            LibraryLayout.EnsureClipInfoRoot(libraryRoot);
            var history = new MedalImportHistory(keys
                .Where(key => !string.IsNullOrWhiteSpace(key))
                .Distinct(StringComparer.Ordinal)
                .OrderBy(key => key, StringComparer.Ordinal)
                .ToArray());
            File.WriteAllText(temporaryPath, JsonSerializer.Serialize(history, JsonOptions));
            File.Move(temporaryPath, path, overwrite: true);
            return true;
        }
        catch (Exception error)
        {
            AppLog.Error($"Medal import history save failed: {path}", error);
            return false;
        }
    }

    private sealed record MedalImportHistory(IReadOnlyList<string> ImportedClipKeys);
}
