namespace Eve.App.Services;

// Single shared source of truth for "games EVE knows about" - used by
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
        ["Marvel-Win64-Shipping.exe"] = "Marvel Rivals",
        ["Among Us.exe"] = "Among Us",
        ["Back4Blood.exe"] = "Back 4 Blood",
        ["Barotrauma.exe"] = "Barotrauma",
        ["cod.exe"] = "Call of Duty",
        ["cod24-cod.exe"] = "Call of Duty",
        ["Cyberpunk2077.exe"] = "Cyberpunk 2077",
        ["DeadByDaylight.exe"] = "Dead by Daylight",
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
        "TheFirstDescendant.exe",
        "cod.exe",
        "cod24-cod.exe",
        "Wuthering Waves.exe",
        "Overwatch.exe",
        "forzahorizon6.exe"
    };
}
