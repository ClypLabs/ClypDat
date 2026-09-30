using System.Text.Json;

namespace ClypDat.App.Services;

internal sealed record AutoClipPackSelection(string PackId, string GameId, string Version, string Hash, string? Directory);

internal sealed class AutoClipPackStore
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private readonly string _root;

    public AutoClipPackStore(string? root = null) => _root = root ?? Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ClypDat", "AutoClip");

    public AutoClipPackSelection Resolve(string gameId)
    {
        var pointer = ReadPointer(Path.Combine(_root, gameId, "current.json"));
        if (pointer is not null && Directory.Exists(pointer.Directory)) return pointer;
        return BuiltIn(gameId);
    }

    public void Quarantine(AutoClipPackSelection selection)
    {
        if (selection.Directory is null) return;
        var gameRoot = Path.Combine(_root, selection.GameId);
        var quarantine = Path.Combine(gameRoot, "quarantine");
        Directory.CreateDirectory(quarantine);
        WriteAtomic(Path.Combine(quarantine, selection.Version + ".json"), selection);
        var previous = ReadPointer(Path.Combine(gameRoot, "previous.json"));
        if (previous is not null) WriteAtomic(Path.Combine(gameRoot, "current.json"), previous);
        else if (File.Exists(Path.Combine(gameRoot, "current.json"))) File.Delete(Path.Combine(gameRoot, "current.json"));
    }

    // Built-in detectors ship inside the app rather than as a downloadable
    // pack: the "pack" is just an identity for the host policy and the version
    // the status line reports.
    private static AutoClipPackSelection BuiltIn(string gameId) => gameId switch
    {
        "helldivers2" => new("clypdat.helldivers2", gameId, "0.1.0", "builtin", null),
        "overwatch" => new("clypdat.overwatch", gameId, "0.1.0", "builtin", null),
        "fortnite" => new("clypdat.fortnite", gameId, "0.1.0", "builtin", null),
        _ => throw new InvalidOperationException($"No detector pack is installed for '{gameId}'.")
    };

    private static AutoClipPackSelection? ReadPointer(string path)
    {
        try { return File.Exists(path) ? JsonSerializer.Deserialize<AutoClipPackSelection>(File.ReadAllBytes(path), JsonOptions) : null; }
        catch { return null; }
    }

    private static void WriteAtomic(string path, AutoClipPackSelection selection)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = path + ".tmp-" + Guid.NewGuid().ToString("N");
        File.WriteAllBytes(temporary, JsonSerializer.SerializeToUtf8Bytes(selection, JsonOptions));
        File.Move(temporary, path, true);
    }
}
