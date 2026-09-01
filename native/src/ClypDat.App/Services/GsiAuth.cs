using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ClypDat.Core.Settings;

namespace ClypDat.App.Services;

/// <summary>
/// Shared-secret handling for the CS2/Dota Game State Integration listeners.
///
/// The listeners bind http://127.0.0.1:{port}/ with HttpListener. Without a token,
/// every request that reaches them is trusted: any local process can drive them, and
/// so can any web page, because a POST to a literal loopback IP with
/// Content-Type: text/plain is a CORS "simple request" - no preflight, so the browser
/// sends it cross-origin and the opaque response does not matter. The reachable sink
/// writes a video of the user's screen to disk, so an unauthenticated listener is a
/// drive-by screen-recording trigger.
///
/// Valve's GSI config format carries an "auth" block whose contents are echoed back in
/// every payload, which is the intended mechanism. ClypDat generates one token per
/// install, writes it into the deployed .cfg, and rejects payloads that do not carry it.
/// </summary>
public static class GsiAuth
{
    public const string TokenKey = "clypdat";

    /// <summary>Returns the install's GSI token, generating and persisting one if absent.</summary>
    public static string EnsureToken(AppSettings settings, Action persist)
    {
        var existing = settings.AutoClipping.GsiAuthToken;
        if (!string.IsNullOrWhiteSpace(existing)) return existing;

        var token = Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();
        settings.AutoClipping.GsiAuthToken = token;
        persist();
        AppLog.Info("Generated a new GSI auth token for this install.");
        return token;
    }

    /// <summary>
    /// Constant-time check of the payload's auth block against the expected token.
    /// A payload with no auth block fails, which is what makes an omitted block
    /// non-bypassable - the mistake the old provider/steamid check made.
    /// </summary>
    public static bool IsPayloadAuthorized(JsonElement root, string expectedToken)
    {
        if (string.IsNullOrEmpty(expectedToken)) return false;
        if (root.ValueKind != JsonValueKind.Object) return false;
        if (!root.TryGetProperty("auth", out var auth) || auth.ValueKind != JsonValueKind.Object) return false;
        if (!auth.TryGetProperty(TokenKey, out var tokenElement) || tokenElement.ValueKind != JsonValueKind.String) return false;

        var supplied = tokenElement.GetString();
        if (string.IsNullOrEmpty(supplied)) return false;

        return CryptographicOperations.FixedTimeEquals(
            Encoding.UTF8.GetBytes(supplied),
            Encoding.UTF8.GetBytes(expectedToken));
    }

    /// <summary>
    /// Rejects requests a real game client would never send. This alone closes the
    /// browser vector: CS2 and Dota send neither Origin nor Sec-Fetch-Site, and a
    /// fetch() from a page always carries at least one of them. Method and
    /// Content-Type are checked for the same reason - GSI always POSTs JSON.
    /// </summary>
    public static bool IsRequestShapeValid(System.Net.HttpListenerRequest request)
    {
        if (!string.Equals(request.HttpMethod, "POST", StringComparison.OrdinalIgnoreCase)) return false;
        if (request.Headers["Origin"] is not null) return false;
        if (request.Headers["Sec-Fetch-Site"] is not null) return false;
        if (request.Headers["Sec-Fetch-Mode"] is not null) return false;

        var contentType = request.ContentType;
        if (string.IsNullOrEmpty(contentType)) return false;
        return contentType.Contains("json", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Largest GSI payload accepted. Real snapshots are a few KB; HttpListener bounds
    /// request lines and headers but not bodies, so without a cap a single request can
    /// exhaust memory (the body string is then parsed, roughly doubling peak usage).
    /// </summary>
    public const long MaximumPayloadBytes = 256 * 1024;

    /// <summary>
    /// Reads at most <see cref="MaximumPayloadBytes"/> from the request, returning null
    /// when the declared or actual length exceeds it.
    /// </summary>
    public static async Task<string?> ReadBoundedBodyAsync(System.Net.HttpListenerRequest request, CancellationToken token)
    {
        if (request.ContentLength64 > MaximumPayloadBytes) return null;

        var buffer = new byte[8192];
        using var accumulated = new MemoryStream();
        int read;
        while ((read = await request.InputStream.ReadAsync(buffer, token).ConfigureAwait(false)) > 0)
        {
            if (accumulated.Length + read > MaximumPayloadBytes) return null;
            accumulated.Write(buffer, 0, read);
        }

        var encoding = request.ContentEncoding ?? Encoding.UTF8;
        return encoding.GetString(accumulated.GetBuffer(), 0, (int)accumulated.Length);
    }
}
