using Microsoft.Win32;
using System.Text;
using System.Text.RegularExpressions;

namespace ClypDat.App.Services;

public sealed record SteamGameInstall(int AppId, string DisplayName, string InstallPath);

public sealed class SteamGameLibrary
{
    private static readonly Regex VdfPath = new("\\\"path\\\"\\s*\\\"(?<path>[^\\\"]+)\\\"", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex ManifestValue = new("\\\"(?<key>appid|name|installdir)\\\"\\s*\\\"(?<value>[^\\\"]*)\\\"", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private readonly object _sync = new();
    private IReadOnlyList<SteamGameInstall> _installs = Array.Empty<SteamGameInstall>();
    private DateTime _nextRefreshUtc = DateTime.MinValue;

    public SteamGameInstall? FindByExecutablePath(string? executablePath)
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
            catch (Exception error) { AppLog.Error("Steam game library scan failed", error); }
            finally { _nextRefreshUtc = DateTime.UtcNow.AddMinutes(5); }
        }
    }

    private static IReadOnlyList<SteamGameInstall> LoadInstalls()
    {
        var steamPath = Registry.GetValue(@"HKEY_LOCAL_MACHINE\SOFTWARE\WOW6432Node\Valve\Steam", "InstallPath", null) as string;
        if (string.IsNullOrWhiteSpace(steamPath) || !Directory.Exists(steamPath)) return Array.Empty<SteamGameInstall>();

        var libraries = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { steamPath };
        var libraryFile = Path.Combine(steamPath, "steamapps", "libraryfolders.vdf");
        if (File.Exists(libraryFile))
        {
            foreach (Match match in VdfPath.Matches(File.ReadAllText(libraryFile)))
            {
                var path = match.Groups["path"].Value.Replace("\\\\", "\\");
                if (Directory.Exists(path)) libraries.Add(path);
            }
        }

        var games = new List<SteamGameInstall>();
        foreach (var library in libraries)
        {
            var steamApps = Path.Combine(library, "steamapps");
            if (!Directory.Exists(steamApps)) continue;
            foreach (var manifestPath in Directory.EnumerateFiles(steamApps, "appmanifest_*.acf"))
            {
                var values = ManifestValue.Matches(ReadManifestText(manifestPath))
                    .Cast<Match>()
                    .GroupBy(match => match.Groups["key"].Value, StringComparer.OrdinalIgnoreCase)
                    .ToDictionary(group => group.Key, group => group.Last().Groups["value"].Value, StringComparer.OrdinalIgnoreCase);
                if (!values.TryGetValue("appid", out var idText) || !int.TryParse(idText, out var appId) ||
                    !values.TryGetValue("name", out var name) || !values.TryGetValue("installdir", out var installDir) ||
                    string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(installDir)) continue;
                var installPath = Path.Combine(steamApps, "common", installDir);
                if (Directory.Exists(installPath)) games.Add(new SteamGameInstall(appId, name, installPath));
            }
        }

        return games.OrderByDescending(game => game.InstallPath.Length).ToArray();
    }

    // File.ReadAllText(path) defaults to strict-ish UTF-8, which silently
    // swaps any invalid byte for U+FFFD instead of throwing - most
    // appmanifest_*.acf files are genuinely UTF-8, but some game names
    // (trademark/registered symbols especially) come through from Steam's own
    // metadata as a single-byte codepage, so a name like "Overwatch (R)"
    // decodes as "Overwatch<FFFD>" instead of "Overwatch(R)". Decoding
    // strictly first and only falling back on a real failure keeps the common
    // UTF-8 case untouched while fixing the mis-encoded one. Latin1 (not a
    // registered Windows-1252 provider, which .NET Core doesn't carry by
    // default) - same byte range as the printable symbols actually seen here.
    private static string ReadManifestText(string path)
    {
        var bytes = File.ReadAllBytes(path);
        try
        {
            return new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true).GetString(bytes);
        }
        catch (DecoderFallbackException)
        {
            return Encoding.Latin1.GetString(bytes);
        }
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
