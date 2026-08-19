namespace ClypDat.App.Services;

public sealed record AutoClipEvent(string Id, string Label, DateTime OccurredUtc, int Priority = 0);

// Medal-inspired naming is kept here, not read from Medal at runtime. The
// event id remains the stable machine value; title is only presentation.
public static class AutoClipTitleFormatter
{
    private static readonly Dictionary<string, string> Prefixes = new(StringComparer.OrdinalIgnoreCase)
    {
        ["kill"] = "⚔️", ["2k"] = "✌\u200D", ["double"] = "✌\u200D",
        ["3k"] = "👊", ["triple"] = "👊", ["4k"] = "🔥", ["quadra"] = "🔥", ["ultra"] = "🔥",
        ["ace"] = "👑", ["penta"] = "👑", ["rampage"] = "👑",
        ["headshot"] = "🎯", ["death"] = "💀", ["assist"] = "🤚",
        ["aegis-picked"] = "🛡️", ["aegis-snatched"] = "🥷",
        ["baron-kill"] = "🐲", ["dragon-kill"] = "🐉", ["herald-kill"] = "👁️", ["voidgrub-kill"] = "🐛",
        ["baron-steal"] = "🥷", ["dragon-steal"] = "🥷", ["herald-steal"] = "🥷", ["voidgrub-steal"] = "🥷",
        ["turret"] = "🏰", ["inhibitor"] = "🏰"
    };

    public static string Format(string gameId, IReadOnlyCollection<AutoClipEvent> events, string? suffix = null)
    {
        if (events.Count == 0) return string.Empty;
        var primary = events.OrderByDescending(item => item.Priority)
            .ThenBy(item => item.OccurredUtc)
            .First();
        var title = $"{Prefix(primary.Id)}{primary.Label}";

        return string.IsNullOrWhiteSpace(suffix) ? title : $"{title} - {suffix}";
    }

    public static string Prefix(string eventId) => Prefixes.TryGetValue(eventId, out var prefix) ? prefix : string.Empty;
}
