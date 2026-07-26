using System.Linq;

namespace ClypDat.App.Converters;

// Shared "relative" matching for the Settings search: every word of the
// query has to appear SOMEWHERE in the target, in any order, rather than the
// query having to appear as one exact contiguous substring - "sidebar
// filter" or "filter sidebar" both find "Combine sidebar filters" this way,
// where a strict Contains match would need the words in that exact order
// and (for "filter") the exact substring, missing the plural "filters".
public static class SettingsSearchMatching
{
    public static bool MatchesRelative(string target, string query)
    {
        query = query.Trim();
        if (query.Length == 0) return true;
        if (target.Length == 0) return false;

        var words = query.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        return words.Length == 0 || words.All(word => target.Contains(word, StringComparison.OrdinalIgnoreCase));
    }
}
