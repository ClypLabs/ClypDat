using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using ClypDat.Core.Settings;

namespace ClypDat.App.Services;

// Format checks for the three things a user types to link CS2 match history.
// Nothing invalid is ever saved or sent.
internal static partial class Cs2MatchHistoryFormats
{
    // Valve's share-code alphabet (no 0, 1, I, O, g, l): case matters.
    private const string ShareCodeAlphabet = "ABCDEFGHJKLMNOPQRSTUVWXYZabcdefhijkmnopqrstuvwxyz23456789";

    [GeneratedRegex(@"^7656119\d{10}$", RegexOptions.CultureInvariant)]
    private static partial Regex SteamIdPattern();

    [GeneratedRegex(@"^[A-Z0-9]{4}-[A-Z0-9]{5}-[A-Z0-9]{4}$", RegexOptions.CultureInvariant)]
    private static partial Regex AuthCodePattern();

    public static string? NormaliseSteamId(string? value) =>
        value?.Trim() is { } trimmed && SteamIdPattern().IsMatch(trimmed) ? trimmed : null;

    public static string? NormaliseAuthCode(string? value) =>
        value?.Trim().ToUpperInvariant() is { } trimmed && AuthCodePattern().IsMatch(trimmed) ? trimmed : null;

    public static string? NormaliseShareCode(string? value)
    {
        var trimmed = value?.Trim();
        if (trimmed is null || trimmed.Length != 34 || !trimmed.StartsWith("CSGO-", StringComparison.Ordinal)) return null;
        var groups = trimmed[5..].Split('-');
        return groups.Length == 5 && groups.All(group => group.Length == 5 && group.All(ShareCodeAlphabet.Contains)) ? trimmed : null;
    }

    public static string? SteamIdError(string? value) => string.IsNullOrWhiteSpace(value) ? "Enter your Steam ID."
        : NormaliseSteamId(value) is null ? "A Steam ID is 17 digits starting with 7656119." : null;

    public static string? AuthCodeError(string? value) => string.IsNullOrWhiteSpace(value) ? "Enter your game authentication code."
        : NormaliseAuthCode(value) is null ? "The code looks like AAAA-AAAAA-AAAA." : null;

    public static string? ShareCodeError(string? value) => string.IsNullOrWhiteSpace(value) ? "Enter a match code."
        : NormaliseShareCode(value) is null ? "A match code looks like CSGO-xxxxx-xxxxx-xxxxx-xxxxx-xxxxx." : null;

    /// <summary>For logs: enough to tell two values apart, nothing to recover them from.</summary>
    public static string Fingerprint(string? value) => string.IsNullOrEmpty(value) ? "none"
        : Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)))[..8].ToLowerInvariant();
}

// What linking CS2 match history stores. The authentication code reads the
// player's whole match history, so it is a credential: this is only ever
// written DPAPI-encrypted (the same protection as the ClypDat sign-in), never
// to settings.json, and ToString never shows it.
internal sealed class Cs2MatchHistoryLink
{
    public string SteamId { get; set; } = string.Empty;
    public string AuthCode { get; set; } = string.Empty;
    // The newest match code fully handled.
    public string KnownCode { get; set; } = string.Empty;
    // The code entered at linking, handled itself before anything newer:
    // pasting an older code backfills from that match onwards.
    public string? StartCode { get; set; }
    // Steam refused the code pair; nothing is retried until it is re-entered.
    public bool NeedsAttention { get; set; }
    public DateTime? LastCheckedUtc { get; set; }
    public DateTime? RetryAfterUtc { get; set; }

    public string AuthCodeHint => AuthCode.Length >= 4 ? AuthCode[^4..] : string.Empty;

    public override string ToString() => $"Cs2MatchHistoryLink(steam {Cs2MatchHistoryFormats.Fingerprint(SteamId)})";
}

internal static class Cs2MatchHistoryLinkStore
{
    private static readonly object Sync = new();
    public static string DefaultPath => Path.Combine(AppDataPaths.Root, "cs2-match-history.bin");

    public static Cs2MatchHistoryLink? Load(string? path = null)
    {
        lock (Sync)
        {
            try
            {
                path ??= DefaultPath;
                if (!File.Exists(path)) return null;
                var bytes = ProtectedData.Unprotect(File.ReadAllBytes(path), null, DataProtectionScope.CurrentUser);
                try { return JsonSerializer.Deserialize<Cs2MatchHistoryLink>(bytes); }
                finally { CryptographicOperations.ZeroMemory(bytes); }
            }
            catch (Exception error) when (error is IOException or CryptographicException or JsonException or UnauthorizedAccessException)
            {
                AppLog.Info($"Match history link could not be read ({error.GetType().Name}); it needs linking again.");
                return null;
            }
        }
    }

    public static void Save(Cs2MatchHistoryLink link, string? path = null)
    {
        lock (Sync)
        {
            path ??= DefaultPath;
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var temporary = path + ".tmp-" + Guid.NewGuid().ToString("N");
            var bytes = JsonSerializer.SerializeToUtf8Bytes(link);
            try
            {
                var encrypted = ProtectedData.Protect(bytes, null, DataProtectionScope.CurrentUser);
                using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    stream.Write(encrypted);
                    stream.Flush(flushToDisk: true);
                }
                File.Move(temporary, path, overwrite: true);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(bytes);
                try { File.Delete(temporary); } catch { }
            }
        }
    }

    public static void Delete(string? path = null)
    {
        lock (Sync)
        {
            try { File.Delete(path ?? DefaultPath); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                AppLog.Error("Match history link could not be removed", error);
            }
        }
    }
}
