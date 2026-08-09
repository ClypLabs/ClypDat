using System.Text.Json;

namespace ClypDat.App.Services;

public static class SteelSeriesImportHistoryStore
{
    private const string FileName = "steelseries-imports.json";
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public static bool TryLoad(string libraryRoot, out HashSet<string> keys)
    {
        keys = new HashSet<string>(StringComparer.Ordinal);
        if (string.IsNullOrWhiteSpace(libraryRoot)) return false;
        var path = Path.Combine(LibraryLayout.ClipInfoRoot(libraryRoot), FileName);
        if (!File.Exists(path)) return true;
        try
        {
            var history = JsonSerializer.Deserialize<History>(File.ReadAllText(path));
            if (history?.ImportedClipKeys is not null) keys.UnionWith(history.ImportedClipKeys.Where(key => !string.IsNullOrWhiteSpace(key)));
            return true;
        }
        catch (Exception error)
        {
            AppLog.Error($"SteelSeries import history read failed: {path}", error);
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
            var history = new History(keys.Where(key => !string.IsNullOrWhiteSpace(key))
                .Distinct(StringComparer.Ordinal).OrderBy(key => key, StringComparer.Ordinal).ToArray());
            File.WriteAllText(temporaryPath, JsonSerializer.Serialize(history, JsonOptions));
            File.Move(temporaryPath, path, overwrite: true);
            return true;
        }
        catch (Exception error)
        {
            AppLog.Error($"SteelSeries import history save failed: {path}", error);
            return false;
        }
    }

    private sealed record History(IReadOnlyList<string> ImportedClipKeys);
}
