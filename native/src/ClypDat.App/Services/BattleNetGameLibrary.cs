using System.Text;
using System.Text.RegularExpressions;

namespace ClypDat.App.Services;

public sealed record BattleNetGameInstall(string DisplayName, string InstallPath);

// Battle.net has no per-game uninstall entry and no Epic-style JSON manifest.
// Every installed product (and the client itself) lives in one undocumented
// protobuf blob, Agent\product.db. Decoding that schema properly would mean
// chasing an internal format Blizzard doesn't publish and can change at
// will, so instead this greps the raw bytes for embedded Windows paths -
// protobuf strings are length-prefixed UTF8, so a path always shows up as a
// contiguous run of printable ASCII bounded by non-printable framing bytes -
// and uses the install folder's own name as the display name, since
// Blizzard's installer already names that folder after the game (e.g.
// "C:\Program Files (x86)\Overwatch").
public sealed class BattleNetGameLibrary
{
    private static readonly Regex InstallPathPattern = new(@"[A-Za-z]:[\\/][ -~]{3,180}", RegexOptions.Compiled);
    private readonly object _sync = new();
    private IReadOnlyList<BattleNetGameInstall> _installs = Array.Empty<BattleNetGameInstall>();
    private DateTime _nextRefreshUtc = DateTime.MinValue;

    public BattleNetGameInstall? FindByExecutablePath(string? executablePath)
    {
        if (string.IsNullOrWhiteSpace(executablePath)) return null;
        EnsureLoaded();
        return _installs.FirstOrDefault(game => IsUnderPath(executablePath, game.InstallPath));
    }

    private void EnsureLoaded()
    {
        if (DateTime.UtcNow < _nextRefreshUtc) return;
        lock (_sync)
        {
            if (DateTime.UtcNow < _nextRefreshUtc) return;
            try { _installs = LoadInstalls(); }
            catch (Exception error) { AppLog.Error("Battle.net game library scan failed", error); }
            finally { _nextRefreshUtc = DateTime.UtcNow.AddMinutes(5); }
        }
    }

    private static IReadOnlyList<BattleNetGameInstall> LoadInstalls()
    {
        var agentRoot = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "Battle.net");
        var productDb = Path.Combine(agentRoot, "Agent", "product.db");
        if (!File.Exists(productDb)) return Array.Empty<BattleNetGameInstall>();

        // Latin1 maps each byte to exactly one char, so the regex still lines
        // up against the original bytes even though the file isn't valid UTF8.
        var text = Encoding.Latin1.GetString(File.ReadAllBytes(productDb));
        var games = new Dictionary<string, BattleNetGameInstall>(StringComparer.OrdinalIgnoreCase);
        foreach (Match match in InstallPathPattern.Matches(text))
        {
            var path = match.Value.Replace('/', Path.DirectorySeparatorChar).TrimEnd();
            if (!Directory.Exists(path)) continue;
            // The Agent's own working folder (and anything beneath it, like
            // the Agent's own path) lives under here - never a game.
            if (IsUnderPath(path, agentRoot) || string.Equals(Path.GetFullPath(path), Path.GetFullPath(agentRoot), StringComparison.OrdinalIgnoreCase)) continue;

            var name = Path.GetFileName(Path.TrimEndingDirectorySeparator(path));
            // That's the client's own install folder, not a game.
            if (string.IsNullOrWhiteSpace(name) || string.Equals(name, "Battle.net", StringComparison.OrdinalIgnoreCase)) continue;

            games.TryAdd(path, new BattleNetGameInstall(name, path));
        }

        return games.Values.OrderByDescending(game => game.InstallPath.Length).ToArray();
    }

    private static bool IsUnderPath(string candidate, string root)
    {
        try
        {
            var normalizedCandidate = Path.GetFullPath(candidate);
            var normalizedRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root)) + Path.DirectorySeparatorChar;
            return normalizedCandidate.StartsWith(normalizedRoot, StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }
    }
}
