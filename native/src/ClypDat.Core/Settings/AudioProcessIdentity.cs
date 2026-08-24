namespace ClypDat.Core.Settings;

public static class AudioProcessIdentity
{
    private static readonly HashSet<string> SocialProcessNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "Discord", "Guilded", "TeamSpeak", "Mumble", "Skype", "Teams",
        "Zoom", "Slack", "Signal", "Telegram", "WhatsApp"
    };

    public static string Normalize(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return string.Empty;
        var value = name.Trim();
        var separator = Math.Max(value.LastIndexOf('\\'), value.LastIndexOf('/'));
        if (separator >= 0) value = value[(separator + 1)..];
        return value.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? value[..^4] : value;
    }

    public static bool Equals(string? left, string? right)
    {
        var normalizedLeft = Normalize(left);
        var normalizedRight = Normalize(right);
        return normalizedLeft.Length > 0 && normalizedLeft.Equals(normalizedRight, StringComparison.OrdinalIgnoreCase);
    }

    public static bool IsSocial(string? name) => SocialProcessNames.Contains(Normalize(name));

    public static List<string> NormalizeList(IEnumerable<string>? names)
    {
        var result = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var name in names ?? Array.Empty<string>())
        {
            var normalized = Normalize(name);
            if (normalized.Length > 0 && seen.Add(normalized)) result.Add(normalized);
        }

        return result;
    }

    public static Dictionary<string, int> NormalizeDictionary(IEnumerable<KeyValuePair<string, int>>? values)
    {
        var result = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var group in (values ?? Array.Empty<KeyValuePair<string, int>>())
                     .Where(pair => Normalize(pair.Key).Length > 0)
                     .GroupBy(pair => Normalize(pair.Key), StringComparer.OrdinalIgnoreCase))
        {
            var preferred = group
                .OrderBy(pair => pair.Key.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? 1 : 0)
                .First();
            result[Normalize(preferred.Key)] = preferred.Value;
        }

        return result;
    }

    public static bool TryGetValue(IReadOnlyDictionary<string, int>? values, string? name, out int value)
    {
        var normalized = Normalize(name);
        if (values is not null && normalized.Length > 0)
        {
            foreach (var pair in values)
            {
                if (Equals(pair.Key, normalized))
                {
                    value = pair.Value;
                    return true;
                }
            }
        }

        value = default;
        return false;
    }

    public static IEnumerable<string> OrderForRecording(IEnumerable<string> names)
    {
        return names
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(name => IsSocial(name) ? 0 : 1)
            .ThenBy(name => name, StringComparer.OrdinalIgnoreCase);
    }
}
