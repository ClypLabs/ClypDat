namespace ClypDat.App.Services;

// Single shared source of truth for "games ClypDat knows about" - used by
// ForegroundGameDetector (to recognize a running game) and by the Game
// Detection settings page (to list games and let a user override their
// capture backend), so the two never drift out of sync with each other.
public static class GameCatalog
{
    public static readonly Dictionary<string, string> BuiltIn = new(StringComparer.OrdinalIgnoreCase)
    {
        ["FortniteBootstrapper.exe"] = "Fortnite",
        ["FortniteLauncher.exe"] = "Fortnite",
        ["FortniteClient-Win64-Shipping.exe"] = "Fortnite",
        ["FortniteClient-Win64-Shipping_EAC.exe"] = "Fortnite",
        ["FortniteClient-Win64-Shipping_EAC_EOS.exe"] = "Fortnite",
        ["cs2.exe"] = "Counter-Strike 2",
        ["dota2.exe"] = "Dota 2",
        ["League of Legends.exe"] = "League of Legends",
        // Without a catalog entry these fall back to a cleaned-up executable
        // name ("RocketLeague", "r5apex"), which is both what the sidebar
        // labels the game and the key every icon lookup is done under - so a
        // missing entry costs the game its artwork as well as its name.
        ["VALORANT-Win64-Shipping.exe"] = "VALORANT",
        ["RocketLeague.exe"] = "Rocket League",
        ["r5apex.exe"] = "Apex Legends",
        ["r5apex_dx12.exe"] = "Apex Legends",
        ["RainbowSix.exe"] = "Rainbow Six Siege",
        ["RainbowSix_Vulkan.exe"] = "Rainbow Six Siege",
        ["destiny2.exe"] = "Destiny 2",
        ["RobloxPlayerBeta.exe"] = "Roblox",
        ["Minecraft.Windows.exe"] = "Minecraft",
        ["GTA5.exe"] = "Grand Theft Auto V",
        ["GTA5_Enhanced.exe"] = "Grand Theft Auto V",
        ["TslGame.exe"] = "PUBG: BATTLEGROUNDS",
        ["RustClient.exe"] = "Rust",
        ["EscapeFromTarkov.exe"] = "Escape from Tarkov",
        ["Warframe.x64.exe"] = "Warframe",
        ["Marvel-Win64-Shipping.exe"] = "Marvel Rivals",
        ["Among Us.exe"] = "Among Us",
        ["Back4Blood.exe"] = "Back 4 Blood",
        ["Barotrauma.exe"] = "Barotrauma",
        ["cod.exe"] = "Call of Duty",
        ["cod24-cod.exe"] = "Call of Duty",
        ["Cyberpunk2077.exe"] = "Cyberpunk 2077",
        ["DeadByDaylight.exe"] = "Dead by Daylight",
        ["DeadByDaylight-Win64-Shipping.exe"] = "Dead by Daylight",
        ["Elgato.Studio.exe"] = "Elgato Studio",
        ["TheFirstDescendant.exe"] = "The First Descendant",
        ["forhonor.exe"] = "For Honor",
        ["forzahorizon6.exe"] = "Forza Horizon 6",
        ["GeometryDash.exe"] = "Geometry Dash",
        ["helldivers2.exe"] = "Helldivers 2",
        ["PenguinHotel.exe"] = "Meccha Chameleon",
        ["Overwatch.exe"] = "Overwatch",
        ["PEAK.exe"] = "PEAK",
        ["Phasmophobia.exe"] = "Phasmophobia",
        ["ProjectZomboid64.exe"] = "Project Zomboid",
        ["RimWorldWin64.exe"] = "RimWorld",
        ["Risk of Rain 2.exe"] = "Risk of Rain 2",
        ["Wuthering Waves.exe"] = "Wuthering Waves"
    };

    // Games known to fight OBS's game_capture hook (VAC blocks it for CS2 without a
    // launch option, causing a black/frozen capture, or the anti-cheat closes the
    // game outright) - these default to Windows Capture instead when the user
    // hasn't explicitly picked a backend.
    public static readonly HashSet<string> AntiCheatSensitive = new(StringComparer.OrdinalIgnoreCase)
    {
        "cs2.exe",
        "Marvel-Win64-Shipping.exe",
        "FortniteClient-Win64-Shipping.exe",
        "FortniteClient-Win64-Shipping_EAC.exe",
        "FortniteClient-Win64-Shipping_EAC_EOS.exe",
        "helldivers2.exe",
        "forhonor.exe",
        "DeadByDaylight.exe",
        "DeadByDaylight-Win64-Shipping.exe",
        "TheFirstDescendant.exe",
        "cod.exe",
        "cod24-cod.exe",
        "Wuthering Waves.exe",
        "Overwatch.exe",
        "forzahorizon6.exe"
    };
}
