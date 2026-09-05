namespace ClypDat.App.Services;

public sealed record AutoClipEventDefinition(
    string Id, string Name, string? GroupId = null, int Priority = 0,
    bool DefaultEnabled = false, int LeadSeconds = 4, int TailSeconds = 4);

public sealed record AutoClipGroupDefinition(string Id, string Name);

public enum AutoClipSetupCapability { None, LocalGameStateIntegration, BuiltInDetector, DownloadableDetectorPack }

public sealed record AutoClipGameDefinition(
    string Id, string Name, IReadOnlyList<AutoClipEventDefinition> Events,
    IReadOnlyList<AutoClipGroupDefinition> Groups, bool IsAvailable = true,
    bool RequiresSetup = false, int DefaultPort = 0,
    string ProviderId = "local", IReadOnlyList<string>? DetectionAliases = null,
    AutoClipSetupCapability SetupCapability = AutoClipSetupCapability.None,
    string? PackId = null, bool DefaultEnabled = true,
    string? PortraitDetectionKey = null, string? PortraitDisplayName = null)
{
    public IReadOnlyList<string> Aliases => DetectionAliases ?? Array.Empty<string>();
    public bool UsesDetectorPack => SetupCapability == AutoClipSetupCapability.DownloadableDetectorPack;
    public bool UsesDetector => SetupCapability is AutoClipSetupCapability.BuiltInDetector or AutoClipSetupCapability.DownloadableDetectorPack;
}

public static class AutoClipCatalog
{
    private static AutoClipEventDefinition Event(string id, string name, string? group = null, int priority = 0,
        bool enabled = false, int lead = 4, int tail = 4) => new(id, name, group, priority, enabled, lead, tail);

    public static readonly IReadOnlyList<AutoClipGameDefinition> Active = new[]
    {
        new AutoClipGameDefinition("cs2", "Counter-Strike 2", new[]
        {
            Event("kill", "Kill", "kills", 10), Event("2k", "2K", "kills", 20),
            Event("3k", "3K", "kills", 30, true), Event("4k", "4K", "kills", 40, true),
            Event("ace", "Ace", "kills", 50, true), Event("headshot", "Headshot", priority: 15),
            Event("death", "Death"), Event("assist", "Assist")
        }, new[] { new AutoClipGroupDefinition("kills", "All Kills") }, DefaultPort: 3499,
            ProviderId: "valve-gsi",
            DetectionAliases: new[] { "cs2", "Counter-Strike 2" }, SetupCapability: AutoClipSetupCapability.LocalGameStateIntegration,
            PortraitDetectionKey: "steam-730", PortraitDisplayName: "Counter-Strike 2"),
        new AutoClipGameDefinition("dota2", "Dota 2", new[]
        {
            Event("kill", "Kill", "kills", 10), Event("double", "Double Kill", "kills", 20),
            Event("triple", "Triple Kill", "kills", 30, true), Event("ultra", "Ultra Kill", "kills", 40, true),
            Event("rampage", "Rampage", "kills", 50, true), Event("death", "Death"),
            Event("assist", "Assist"), Event("aegis-picked", "Aegis Picked Up", priority: 35),
            Event("aegis-snatched", "Aegis Snatched", priority: 45, enabled: true)
        }, new[] { new AutoClipGroupDefinition("kills", "All Kills") }, RequiresSetup: true, DefaultPort: 3500,
            ProviderId: "valve-gsi",
            DetectionAliases: new[] { "dota2", "Dota 2" }, SetupCapability: AutoClipSetupCapability.LocalGameStateIntegration,
            PortraitDetectionKey: "steam-570", PortraitDisplayName: "Dota 2"),
        new AutoClipGameDefinition("fortnite", "Fortnite", new[]
        {
            Event("eliminated-player", "Eliminated Player", "eliminations", 10, lead: 8, tail: 6),
            Event("got-eliminated", "Got Eliminated", priority: 15, lead: 12, tail: 6),
            Event("distance-shot", "Distance Shot", priority: 20, enabled: true, lead: 8, tail: 6),
            Event("impossible-shot", "Impossible Shot", priority: 30, enabled: true, lead: 8, tail: 6),
            Event("double-elimination", "Double Elimination", "eliminations", 40, true, 6, 8),
            Event("multi-elimination", "Multi-Elimination", "eliminations", 50, true, 6, 8),
            Event("headshot", "Headshot", priority: 25, lead: 8, tail: 6),
            Event("match-complete", "Match Complete", priority: 10, lead: 15, tail: 10),
            Event("bounty-complete", "Bounty Complete", priority: 15, lead: 8, tail: 6),
            Event("quest-complete", "Quest Complete", priority: 15, lead: 8, tail: 6),
            Event("top-3", "Top 3", priority: 60, enabled: true, lead: 10, tail: 6),
            Event("victory-royale", "Victory Royale", priority: 100, enabled: true, lead: 15, tail: 10)
        }, new[] { new AutoClipGroupDefinition("eliminations", "Eliminations") }, ProviderId: "clypdat-cv",
            DetectionAliases: new[] { "fortnite", "FortniteClient-Win64-Shipping" },
            SetupCapability: AutoClipSetupCapability.DownloadableDetectorPack, PackId: "fortnite", DefaultEnabled: false,
            PortraitDetectionKey: "epic-fortnite", PortraitDisplayName: "Fortnite"),
        new AutoClipGameDefinition("helldivers2", "HELLDIVERS™ 2", new[]
        {
            Event("eliminated", "Eliminated", priority: 10, lead: 12, tail: 6),
            Event("killstreak-20", "Killstreak ×20", "streaks", 20, true, 10, 6),
            Event("killstreak-50", "Killstreak ×50", "streaks", 50, true, 10, 6),
            Event("killstreak-100", "Killstreak ×100", "streaks", 100, true, 10, 6),
            Event("successful-mission", "Successful Mission", "missions", 80, true, 15, 10)
        }, new[]
        {
            new AutoClipGroupDefinition("streaks", "Killstreaks"),
            new AutoClipGroupDefinition("missions", "Missions")
        }, ProviderId: "clypdat-cv",
            DetectionAliases: new[] { "helldivers2", "Helldivers 2", "HELLDIVERS™ 2" },
            SetupCapability: AutoClipSetupCapability.BuiltInDetector, PackId: "helldivers2-prototype", DefaultEnabled: false,
            PortraitDetectionKey: "steam-553850", PortraitDisplayName: "HELLDIVERS™ 2"),
        new AutoClipGameDefinition("league", "League of Legends", new[]
        {
            Event("kill", "Enemy Slain", "kills", 10), Event("double", "Double Kill", "kills", 20),
            Event("triple", "Triple Kill", "kills", 30, true), Event("quadra", "Quadra Kill", "kills", 40, true),
            Event("penta", "Pentakill", "kills", 50, true), Event("ace", "Ace", "kills", 45),
            Event("baron-steal", "Baron Steal", "monsters", 45, true), Event("baron-kill", "Baron Kill", "monsters", 35),
            Event("dragon-steal", "Dragon Steal", "monsters", 45, true), Event("dragon-kill", "Dragon Kill", "monsters", 35),
            Event("herald-steal", "Herald Steal", "monsters", 45), Event("herald-kill", "Herald Kill", "monsters", 35),
            Event("voidgrub-steal", "Voidgrub Steal", "monsters", 45), Event("voidgrub-kill", "Voidgrub Kill", "monsters", 35),
            Event("turret", "Turret Destroyed", "objectives", 25), Event("inhibitor", "Inhibitor Destroyed", "objectives", 30),
            Event("death", "Player Slain"), Event("assist", "Assist")
        }, new[] { new AutoClipGroupDefinition("kills", "All Kills"), new AutoClipGroupDefinition("monsters", "All Epic Monsters"), new AutoClipGroupDefinition("objectives", "All Objectives") },
            ProviderId: "league-live-client",
            DetectionAliases: new[] { "league", "League of Legends", "LeagueClient" },
            // League is not on Steam, so the portrait comes from the curated
            // "portraits" map in game-icons.json rather than the store search.
            PortraitDetectionKey: "riot-league of legends", PortraitDisplayName: "League of Legends"),
        new AutoClipGameDefinition("overwatch", "Overwatch®", new[]
        {
            // Overwatch names each tier outright on screen ("DOUBLE KILL",
            // "TRIPLE KILL", "QUADRUPLE KILL", "QUINTUPLE KILL"), so they are
            // separate events rather than one "Multikill" - there is nothing to
            // infer, and a player who only wants the rare ones can say so.
            // Priority ascends with the tier so the bigger streak wins when two
            // land in the same window.
            Event("elimination", "Elimination", "eliminations", 10, lead: 8, tail: 6),
            Event("double-kill", "Double Kill", "eliminations", 20, true, 8, 6),
            Event("triple-kill", "Triple Kill", "eliminations", 30, true, 8, 6),
            Event("quadruple-kill", "Quadruple Kill", "eliminations", 40, true, 8, 6),
            Event("quintuple-kill", "Quintuple Kill", "eliminations", 50, true, 8, 6),
            Event("team-kill", "Team Kill", "eliminations", 60, true, 10, 8),
            Event("play-of-the-game", "Play of the Game", priority: 100, enabled: true, lead: 15, tail: 10)
        }, new[] { new AutoClipGroupDefinition("eliminations", "Eliminations") }, ProviderId: "clypdat-cv",
            DetectionAliases: new[] { "overwatch", "Overwatch®", "Overwatch" },
            SetupCapability: AutoClipSetupCapability.DownloadableDetectorPack, PackId: "overwatch", DefaultEnabled: false,
            PortraitDetectionKey: "steam-2357570", PortraitDisplayName: "Overwatch®")
    };

    public static readonly IReadOnlyList<string> ComingSoon = new[]
    {
        "EA Sports FC Online", "GTA V", "Minecraft", "PUBG", "Rematch", "REPO", "Roblox", "Rocket League", "RuneScape: Dragonwilds", "War Thunder", "YAPYAP"
    };

    public static AutoClipGameDefinition Get(string id) => Active.First(game => string.Equals(game.Id, id, StringComparison.OrdinalIgnoreCase));

    public static string? MatchGame(string? detectionKey, string? executable, string? displayName)
    {
        var candidates = new[] { detectionKey, executable, displayName }.Where(value => !string.IsNullOrWhiteSpace(value));
        return Active.FirstOrDefault(game => candidates.Any(candidate => game.Aliases.Any(alias =>
            candidate!.Contains(alias, StringComparison.OrdinalIgnoreCase))))?.Id;
    }
}
