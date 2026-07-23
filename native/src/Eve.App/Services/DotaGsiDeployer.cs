using System.Runtime.Versioning;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace Eve.App.Services;

[SupportedOSPlatform("windows")]
public static class DotaGsiDeployer
{
    public static bool TryDeploy(int port, out string status)
    {
        var folder = FindCfgFolder();
        if (folder is null) { status = "Dota 2 was not found in your Steam libraries. Install it, then return here to finish setup."; return false; }
        try
        {
            Directory.CreateDirectory(folder);
            var path = Path.Combine(folder, "gamestate_integration_eve.cfg");
            var content = $$"""
                "EVE Dota GSI"
                {
                 "uri" "http://127.0.0.1:{{port}}/"
                 "timeout" "5.0"
                 "buffer" "0.1"
                 "throttle" "0.1"
                 "heartbeat" "15.0"
                 "data" { "provider" "1" "map" "1" "player" "1" "hero" "1" "items" "1" "events" "1" "roshan" "1" }
                }
                """;
            if (!File.Exists(path) || File.ReadAllText(path) != content) File.WriteAllText(path, content);
            status = "EVE's Dota config is installed. Add -gamestateintegration to Dota 2's Steam launch options, then restart Dota.";
            return true;
        }
        catch (Exception error) { AppLog.Error("Dota GSI config deploy failed", error); status = $"Dota was found but EVE could not write its config: {error.Message}"; return false; }
    }

    private static string? FindCfgFolder()
    {
        foreach (var library in Libraries())
        {
            var folder = Path.Combine(library, "steamapps", "common", "dota 2 beta", "game", "dota", "cfg", "gamestate_integration");
            if (Directory.Exists(Path.GetDirectoryName(folder) ?? folder)) return folder;
        }
        return null;
    }
    private static IEnumerable<string> Libraries()
    {
        string? steam = null;
        try { steam = Registry.CurrentUser.OpenSubKey(@"Software\Valve\Steam")?.GetValue("SteamPath") as string; } catch { }
        if (string.IsNullOrWhiteSpace(steam)) yield break;
        steam = steam.Replace('/', '\\'); yield return steam;
        var file = Path.Combine(steam, "steamapps", "libraryfolders.vdf"); if (!File.Exists(file)) yield break;
        foreach (Match match in Regex.Matches(File.ReadAllText(file), "\"path\"\\s*\"([^\"]+)\"")) yield return match.Groups[1].Value.Replace(@"\\", @"\");
    }
}
