using System.Collections.Frozen;

namespace ClypDat.App.Services;

// All collections are copied on construction; readers keep one coherent generation.
public sealed class SteamClassificationSnapshot
{
    private static readonly FrozenSet<int> KnownSoftware = new[]
        { 1905180, 1009850, 250820, 365670, 431960, 629520, 993090 }.ToFrozenSet();
    private static readonly FrozenSet<string> KnownSoftwareExecutables = new[]
    {
        "obs.exe", "obs32.exe", "obs64.exe", "AdvancedSettings.exe", "OVRAdvancedSettings.exe",
        "vrmonitor.exe", "vrserver.exe", "vrcompositor.exe", "vrdashboard.exe", "blender.exe",
        "wallpaper32.exe", "wallpaper64.exe", "soundpad.exe", "LosslessScaling.exe"
    }.ToFrozenSet(StringComparer.OrdinalIgnoreCase);
    internal static SteamClassificationSnapshot Empty { get; } = new([], new Dictionary<int, SteamAppKind>(), new Dictionary<string, SteamGameInstall?>(), false);
    private readonly SteamGameInstall[] _installs;
    private readonly FrozenDictionary<string, SteamGameInstall?> _names;
    internal FrozenDictionary<int, SteamAppKind> Kinds { get; }
    internal bool HasMetadata { get; }

    internal SteamClassificationSnapshot(IEnumerable<SteamGameInstall> installs, IReadOnlyDictionary<int, SteamAppKind> kinds,
        IReadOnlyDictionary<string, SteamGameInstall?> names, bool hasMetadata = true)
    {
        _installs = installs.OrderByDescending(i => i.InstallPath.Length).ToArray();
        Kinds = kinds.ToFrozenDictionary();
        _names = names.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase);
        HasMetadata = hasMetadata;
    }

    public SteamAppKind Classify(int appId) => KnownSoftware.Contains(appId) ? SteamAppKind.NonGame : Kinds.GetValueOrDefault(appId);

    internal SteamGameInstall? FindInstall(string? path) => string.IsNullOrWhiteSpace(path) ? null :
        _installs.FirstOrDefault(i => SteamGameLibrary.IsUnderPath(path, i.InstallPath));

    public SteamGameInstall? FindGameByPath(string? path) => FindInstall(path) is { } install && Classify(install.AppId) == SteamAppKind.Game ? install : null;

    public SteamGameInstall? FindGameByName(string? name) => name is not null && _names.TryGetValue(name, out var install)
        && install is not null && Classify(install.AppId) == SteamAppKind.Game ? install : null;

    public bool IsSoftware(string? path = null, string? executableName = null, string? detectionKey = null)
    {
        // Explicit non-game IDs win. A verified path wins over ambiguous names.
        if (TryAppId(detectionKey, out var id) && Classify(id) == SteamAppKind.NonGame) return true;
        var install = FindInstall(path);
        if (install is not null && Classify(install.AppId) != SteamAppKind.Unknown)
            return Classify(install.AppId) == SteamAppKind.NonGame;
        if (TryAppId(detectionKey, out id) && Classify(id) == SteamAppKind.Game) return false;
        var name = !string.IsNullOrWhiteSpace(path) ? Path.GetFileName(path) : executableName;
        if (string.IsNullOrWhiteSpace(name)) name = detectionKey;
        if (name is null) return false;
        if (_names.TryGetValue(name, out var owner)) return owner is not null && Classify(owner.AppId) == SteamAppKind.NonGame;
        // A shared filename alone cannot exclude a verified game.
        return KnownSoftwareExecutables.Contains(name);
    }

    private static bool TryAppId(string? key, out int id)
    {
        id = 0;
        return key?.StartsWith("steam-", StringComparison.OrdinalIgnoreCase) == true && int.TryParse(key.AsSpan(6), out id);
    }

    internal bool EquivalentTo(SteamClassificationSnapshot other) => HasMetadata == other.HasMetadata &&
        _installs.SequenceEqual(other._installs) && Kinds.Count == other.Kinds.Count &&
        Kinds.All(p => other.Kinds.TryGetValue(p.Key, out var kind) && kind == p.Value) &&
        _names.Count == other._names.Count && _names.All(p => other._names.TryGetValue(p.Key, out var install) && install == p.Value);
}
