using System.Runtime.Versioning;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace Eve.App.Services;

// CS2's Game State Integration only activates once a gamestate_integration_*.cfg
// file exists in its cfg folder telling it where to POST state updates. This
// locates the CS2 install (via Steam's library folders) and drops that file in,
// so the user doesn't have to find and edit it by hand.
[SupportedOSPlatform("windows")]
public static class Cs2GsiDeployer
{
    private const string ConfigFileName = "gamestate_integration_eve.cfg";

    public static bool TryDeploy(int port, out string statusMessage)
    {
        var cfgFolder = FindCs2CfgFolder();
        if (cfgFolder is null)
        {
            statusMessage = "Could not find a CS2 install via Steam. Auto-clip will stay off until CS2 is found - start CS2 once, then reopen this page.";
            return false;
        }

        try
        {
            Directory.CreateDirectory(cfgFolder);
            var path = Path.Combine(cfgFolder, ConfigFileName);
            var content = BuildConfig(port);
            if (!File.Exists(path) || File.ReadAllText(path) != content)
            {
                File.WriteAllText(path, content);
            }

            statusMessage = $"Connected to CS2 at {cfgFolder}. Restart CS2 if it's already running for the config to take effect.";
            return true;
        }
        catch (Exception error)
        {
            AppLog.Error("CS2 GSI config deploy failed", error);
            statusMessage = $"Found CS2 but couldn't write its config file: {error.Message}";
            return false;
        }
    }

    private static string BuildConfig(int port) => $$"""
        "EVE GSI"
        {
         "uri"          "http://127.0.0.1:{{port}}/"
         "timeout"      "5.0"
         "buffer"       "0.1"
         "throttle"     "0.1"
         "heartbeat"    "30.0"
         "data"
         {
          "provider"            "1"
          "map"                 "1"
          "round"               "1"
          "player_id"           "1"
          "player_state"        "1"
          "player_match_stats"  "1"
         }
        }
        """;

    private static string? FindCs2CfgFolder()
    {
        foreach (var library in EnumerateSteamLibraryFolders())
        {
            var candidate = Path.Combine(library, "steamapps", "common", "Counter-Strike Global Offensive", "game", "csgo", "cfg");
            if (Directory.Exists(Path.GetDirectoryName(candidate) ?? candidate) || Directory.Exists(candidate))
            {
                return candidate;
            }
        }

        return null;
    }

    private static IEnumerable<string> EnumerateSteamLibraryFolders()
    {
        var steamPath = GetSteamInstallPath();
        if (steamPath is null) yield break;

        yield return steamPath;

        var vdfPath = Path.Combine(steamPath, "steamapps", "libraryfolders.vdf");
        if (!File.Exists(vdfPath)) yield break;

        string content;
        try
        {
            content = File.ReadAllText(vdfPath);
        }
        catch
        {
            yield break;
        }

        foreach (Match match in Regex.Matches(content, "\"path\"\\s*\"([^\"]+)\""))
        {
            yield return match.Groups[1].Value.Replace(@"\\", @"\");
        }
    }

    private static string? GetSteamInstallPath()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Valve\Steam");
            if (key?.GetValue("SteamPath") is string path && Directory.Exists(path)) return path.Replace('/', '\\');
        }
        catch
        {
            // Fall through to the 64-bit registry view below.
        }

        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\WOW6432Node\Valve\Steam");
            if (key?.GetValue("InstallPath") is string path && Directory.Exists(path)) return path;
        }
        catch
        {
            // Steam just isn't installed / registry key missing.
        }

        return null;
    }
}
