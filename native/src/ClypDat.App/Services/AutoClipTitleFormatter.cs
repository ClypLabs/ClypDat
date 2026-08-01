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

    private static readonly HashSet<string> Milestones = new(StringComparer.OrdinalIgnoreCase)
    {
        "2k", "3k", "4k", "ace", "double", "triple", "ultra", "rampage", "quadra", "penta"
    };

    public static string Format(string gameId, IReadOnlyCollection<AutoClipEvent> events, string? suffix = null)
    {
        if (events.Count == 0) return string.Empty;
        var grouped = events.GroupBy(item => item.Id, StringComparer.OrdinalIgnoreCase)
            .Select(group => new EventCount(group.First(), group.Count()))
            .ToList();
        var primary = grouped.Where(item => Milestones.Contains(item.Event.Id))
            .OrderByDescending(item => item.Event.Priority).ThenBy(item => item.Event.OccurredUtc).FirstOrDefault();

        string title;
        if (primary is not null)
        {
            var companions = grouped.Where(item => !ReferenceEquals(item, primary))
                // Medal's current CS2 streak titles omit lower kills/headshots.
                .Where(item => !string.Equals(gameId, "cs2", StringComparison.OrdinalIgnoreCase) ||
                               (item.Event.Id is not "kill" and not "headshot"))
                .OrderByDescending(item => item.Event.Priority).ThenBy(item => item.Event.OccurredUtc)
                .Select(PlainLabel);
            title = $"{Prefix(primary.Event.Id)}{primary.Event.Label}";
            var tail = string.Join(", ", companions);
            if (!string.IsNullOrWhiteSpace(tail)) title += $", {tail}";
        }
        else
        {
            title = string.Join(" ", grouped.OrderBy(item => SortOrder(item.Event.Id)).ThenBy(item => item.Event.OccurredUtc)
                .Select(item => $"{Prefix(item.Event.Id)}{PlainLabel(item)}"));
        }

        return string.IsNullOrWhiteSpace(suffix) ? title : $"{title} - {suffix}";
    }

    public static string Prefix(string eventId) => Prefixes.TryGetValue(eventId, out var prefix) ? prefix : string.Empty;
    private static string PlainLabel(EventCount item) => item.Count > 1 ? $"{item.Event.Label} x{item.Count}" : item.Event.Label;
    private static int SortOrder(string id) => id.ToLowerInvariant() switch { "headshot" => 0, "kill" => 1, "death" => 2, "assist" => 3, _ => 4 };
    private sealed record EventCount(AutoClipEvent Event, int Count);
}
