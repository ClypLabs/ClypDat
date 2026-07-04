namespace Eve.App.Services;

public static class HotkeyCombo
{
    private static readonly string[] ModifierOrder = { "Ctrl", "Alt", "Shift", "Win" };

    public static string Normalize(IEnumerable<string> keys)
    {
        var normalized = keys
            .Select(NormalizeKey)
            .Where(key => !string.IsNullOrWhiteSpace(key))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        var modifiers = ModifierOrder
            .Where(mod => normalized.Contains(mod, StringComparer.OrdinalIgnoreCase));
        var rest = normalized
            .Where(key => !ModifierOrder.Contains(key, StringComparer.OrdinalIgnoreCase))
            .OrderBy(key => key, StringComparer.OrdinalIgnoreCase);

        return string.Join("+", modifiers.Concat(rest));
    }

    public static HashSet<string> Parse(string hotkey)
    {
        return hotkey
            .Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(NormalizeKey)
            .Where(key => !string.IsNullOrWhiteSpace(key))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    public static string NormalizeKey(string key)
    {
        return key switch
        {
            "Control" or "LeftCtrl" or "RightCtrl" or "LControlKey" or "RControlKey" => "Ctrl",
            "LeftShift" or "RightShift" or "ShiftKey" => "Shift",
            "Menu" or "LeftAlt" or "RightAlt" or "LMenu" or "RMenu" => "Alt",
            "LWin" or "RWin" or "LeftMeta" or "RightMeta" => "Win",
            "D0" => "0",
            "D1" => "1",
            "D2" => "2",
            "D3" => "3",
            "D4" => "4",
            "D5" => "5",
            "D6" => "6",
            "D7" => "7",
            "D8" => "8",
            "D9" => "9",
            _ => key
        };
    }
}
