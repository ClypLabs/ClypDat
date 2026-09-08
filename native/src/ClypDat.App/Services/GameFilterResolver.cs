namespace ClypDat.App.Services;

// Resolves the one key used by cards, cached startup filtering, and sidebar
// counts. FileTitle is a recording title, not necessarily a game name, so
// session conventions are unwrapped only when no explicit game was saved.
public static class GameFilterResolver
{
    private const string SessionPrefix = "Session - ";
    private const string LegacySessionSuffix = " Full Session";

    public static string Resolve(ClipInfo? clipInfo, string fileName)
    {
        var explicitGame = clipInfo?.GameDisplayName;
        if (!string.IsNullOrWhiteSpace(explicitGame)) return NormalizeGameDisplayName(explicitGame);

        var fallbackTitle = !string.IsNullOrWhiteSpace(clipInfo?.FileTitle)
            ? clipInfo.FileTitle
            : ClipFileNaming.StripTimestampSuffix(fileName);
        return NormalizeGameDisplayName(RemoveSessionConvention(fallbackTitle));
    }

    public static string NormalizeGameDisplayName(string? name)
    {
        var normalized = name?.Trim() ?? string.Empty;
        if (normalized.EndsWith(" (Trimmed)", StringComparison.OrdinalIgnoreCase))
        {
            normalized = normalized[..^" (Trimmed)".Length].TrimEnd();
        }

        return normalized.ToUpperInvariant() switch
        {
            "DESKTOP" or "DESKTOPCAPTURE" => "Desktop Capture",
            "FORTNITECLIENT-WIN64-SHIPPING" or "FORTNITECLIENT-WIN64-SHIPPING.EXE" => "Fortnite",
            "ROBLOXPLAYERBETA" or "ROBLOXPLAYERBETA.EXE" or "ROBLOXPLAYERLAUNCHER" or "ROBLOXPLAYERLAUNCHER.EXE" => "Roblox",
            "VALORANT" or "VALORANT-WIN64-SHIPPING" or "VALORANT-WIN64-SHIPPING.EXE" => "Valorant",
            "LEAGUECLIENT" or "LEAGUECLIENT.EXE" or "LEAGUECLIENTUX" or "LEAGUECLIENTUX.EXE" or "LEAGUECLIENTUXRELEASE" or "LEAGUECLIENTUXRELEASE.EXE" => "League of Legends",
            _ => normalized
        };
    }

    private static string? RemoveSessionConvention(string? title)
    {
        if (string.IsNullOrWhiteSpace(title)) return title;
        var trimmed = title.Trim();
        if (trimmed.StartsWith(SessionPrefix, StringComparison.OrdinalIgnoreCase))
        {
            return trimmed[SessionPrefix.Length..].Trim();
        }

        if (trimmed.EndsWith(LegacySessionSuffix, StringComparison.OrdinalIgnoreCase))
        {
            return trimmed[..^LegacySessionSuffix.Length].Trim();
        }

        return trimmed;
    }
}
