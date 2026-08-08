using System.Linq;

namespace ClypDat.App.Converters;

// Shared "relative" matching for the Settings search: every word of the query
// has to start a word SOMEWHERE in the target, in any order, rather than the
// query having to appear as one exact contiguous substring - "sidebar filter"
// or "filter sidebar" both find "Combine sidebar filters" this way, where a
// strict match would need the words in that exact order and (for "filter") the
// exact substring, missing the plural "filters".
//
// Word-START, not anywhere. Plain Contains matched inside words: "co" hit
// "reCOrding", so typing it surfaced a card whose only visible content was a
// header that happened to contain those two letters, with every setting under
// it hidden. Nobody searching "co" means "recording", and an empty card is a
// worse answer than no result at all. Prefixes still work the way people
// expect - "rec" finds Recording, "buf" finds Replay Buffer.
public static class SettingsSearchMatching
{
    public static bool MatchesRelative(string target, string query)
    {
        query = query.Trim();
        if (query.Length == 0) return true;
        if (target.Length == 0) return false;

        var words = query.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        return words.Length == 0 || words.All(word => StartsAWord(target, word));
    }

    // A word boundary is the start of the string or any non-letter/digit before
    // it, so "audio" matches "Game Audio Exclusions" and "clip" matches
    // "Auto-Clip" (the hyphen ends the previous word) without either matching
    // mid-word.
    private static bool StartsAWord(string target, string word)
    {
        var index = target.IndexOf(word, StringComparison.OrdinalIgnoreCase);
        while (index >= 0)
        {
            if (index == 0 || !char.IsLetterOrDigit(target[index - 1])) return true;
            index = target.IndexOf(word, index + 1, StringComparison.OrdinalIgnoreCase);
        }

        return false;
    }
}
