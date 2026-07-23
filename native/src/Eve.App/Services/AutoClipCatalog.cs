namespace Eve.App.Services;

public sealed record AutoClipEventDefinition(string Id, string Name, string? GroupId = null, int Priority = 0);
public sealed record AutoClipGroupDefinition(string Id, string Name);
public sealed record AutoClipGameDefinition(
    string Id,
    string Name,
    IReadOnlyList<AutoClipEventDefinition> Events,
    IReadOnlyList<AutoClipGroupDefinition> Groups,
    bool IsAvailable = true,
    bool RequiresSetup = false,
    int DefaultPort = 0);

public static class AutoClipCatalog
{
    public static readonly IReadOnlyList<AutoClipGameDefinition> Active = new[]
    {
        new AutoClipGameDefinition("cs2", "Counter-Strike 2", new[]
        {
            new AutoClipEventDefinition("kill", "Kill", "kills", 10), new AutoClipEventDefinition("2k", "2K", "kills", 20),
            new AutoClipEventDefinition("3k", "3K", "kills", 30), new AutoClipEventDefinition("4k", "4K", "kills", 40),
            new AutoClipEventDefinition("ace", "Ace", "kills", 50), new AutoClipEventDefinition("headshot", "Headshot", null, 15),
            new AutoClipEventDefinition("death", "Death"), new AutoClipEventDefinition("assist", "Assist")
        }, new[] { new AutoClipGroupDefinition("kills", "All Kills") }, DefaultPort: 3499),
        new AutoClipGameDefinition("dota2", "Dota 2", new[]
        {
            new AutoClipEventDefinition("kill", "Kill", "kills", 10), new AutoClipEventDefinition("double", "Double Kill", "kills", 20),
            new AutoClipEventDefinition("triple", "Triple Kill", "kills", 30), new AutoClipEventDefinition("ultra", "Ultra Kill", "kills", 40),
            new AutoClipEventDefinition("rampage", "Rampage", "kills", 50), new AutoClipEventDefinition("death", "Death"),
            new AutoClipEventDefinition("assist", "Assist"), new AutoClipEventDefinition("aegis-picked", "Aegis Picked Up", null, 35),
            new AutoClipEventDefinition("aegis-snatched", "Aegis Snatched", null, 45)
        }, new[] { new AutoClipGroupDefinition("kills", "All Kills") }, RequiresSetup: true, DefaultPort: 3500),
        new AutoClipGameDefinition("league", "League of Legends", new[]
        {
            new AutoClipEventDefinition("kill", "Enemy Slain", "kills", 10), new AutoClipEventDefinition("double", "Double Kill", "kills", 20),
            new AutoClipEventDefinition("triple", "Triple Kill", "kills", 30), new AutoClipEventDefinition("quadra", "Quadra Kill", "kills", 40),
            new AutoClipEventDefinition("penta", "Pentakill", "kills", 50), new AutoClipEventDefinition("ace", "Ace", "kills", 45),
            new AutoClipEventDefinition("baron-steal", "Baron Steal", "monsters", 45), new AutoClipEventDefinition("baron-kill", "Baron Kill", "monsters", 35),
            new AutoClipEventDefinition("dragon-steal", "Dragon Steal", "monsters", 45), new AutoClipEventDefinition("dragon-kill", "Dragon Kill", "monsters", 35),
            new AutoClipEventDefinition("herald-steal", "Herald Steal", "monsters", 45), new AutoClipEventDefinition("herald-kill", "Herald Kill", "monsters", 35),
            new AutoClipEventDefinition("voidgrub-steal", "Voidgrub Steal", "monsters", 45), new AutoClipEventDefinition("voidgrub-kill", "Voidgrub Kill", "monsters", 35),
            new AutoClipEventDefinition("turret", "Turret Destroyed", "objectives", 25), new AutoClipEventDefinition("inhibitor", "Inhibitor Destroyed", "objectives", 30),
            new AutoClipEventDefinition("death", "Player Slain"), new AutoClipEventDefinition("assist", "Assist")
        }, new[] { new AutoClipGroupDefinition("kills", "All Kills"), new AutoClipGroupDefinition("monsters", "All Epic Monsters"), new AutoClipGroupDefinition("objectives", "All Objectives") })
    };

    public static readonly IReadOnlyList<string> ComingSoon = new[]
    {
        "EA Sports FC Online", "Fortnite", "GTA V", "Minecraft", "PUBG", "Rematch", "REPO", "Roblox", "Rocket League", "RuneScape: Dragonwilds", "Valorant", "War Thunder", "YAPYAP"
    };

    public static AutoClipGameDefinition Get(string id) => Active.First(game => string.Equals(game.Id, id, StringComparison.OrdinalIgnoreCase));
}
