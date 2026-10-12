using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ClypDat.App.Services;

internal enum Cs2MatchHistoryFailure
{
    InvalidInput,
    SignedOut,
    SteamRejected,
    RateLimited,
    Unavailable,
    MatchNotFound,
    Unexpected
}

// Messages here are for the app, not the log: they never carry the request.
internal sealed class Cs2MatchHistoryException(Cs2MatchHistoryFailure failure, TimeSpan? retryAfter = null)
    : Exception($"CS2 match history request failed: {failure}.")
{
    public Cs2MatchHistoryFailure Failure { get; } = failure;
    public TimeSpan? RetryAfter { get; } = retryAfter;
}

internal sealed record Cs2ShareCodePage(IReadOnlyList<string> Codes, bool Complete);

internal sealed record Cs2MatchInfo(string ShareCode, string MatchId, DateTimeOffset? MatchTime, string? Map, string? DemoUrl);

// clypdat.xyz's side of CS2 match history: it holds the Steam Web API key and
// talks to the game coordinator, so the desktop only ever sends the player's
// codes to our own API, over HTTPS, in a JSON body, with the sign-in token.
internal sealed class Cs2MatchHistoryClient
{
    private readonly HttpClient _http;

    public Cs2MatchHistoryClient() : this(new HttpClientHandler { AllowAutoRedirect = false }, ClipHostingService.ApiBaseAddress) { }

    internal Cs2MatchHistoryClient(HttpMessageHandler handler, Uri baseAddress)
    {
        // The authentication code goes in these requests. Never anywhere but
        // an HTTPS origin, whatever the build was pointed at.
        if (baseAddress.Scheme != Uri.UriSchemeHttps) throw new InvalidOperationException("Match history needs an HTTPS API address.");
        _http = new HttpClient(handler) { BaseAddress = baseAddress, Timeout = TimeSpan.FromSeconds(45), MaxResponseContentBufferSize = 256 * 1024 };
    }

    public async Task<Cs2ShareCodePage> GetShareCodesAsync(string token, string steamId, string authCode, string knownCode, CancellationToken cancellationToken)
    {
        using var request = ClipHostingService.Authorized(HttpMethod.Post, "api/desktop/cs2/share-codes", token);
        request.Content = JsonContent.Create(new ShareCodesRequest { SteamId = steamId, AuthCode = authCode, KnownCode = knownCode });
        using var response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        await ThrowIfFailedAsync(response, cancellationToken).ConfigureAwait(false);
        var body = await response.Content.ReadFromJsonAsync<ShareCodesResponse>(cancellationToken).ConfigureAwait(false)
                   ?? throw new Cs2MatchHistoryException(Cs2MatchHistoryFailure.Unexpected);
        var codes = (body.Codes ?? []).Select(Cs2MatchHistoryFormats.NormaliseShareCode).OfType<string>().ToArray();
        return new Cs2ShareCodePage(codes, body.Complete);
    }

    /// <summary>Null when Steam no longer knows the match.</summary>
    public async Task<Cs2MatchInfo?> GetMatchAsync(string token, string shareCode, CancellationToken cancellationToken)
    {
        using var request = ClipHostingService.Authorized(HttpMethod.Post, "api/desktop/cs2/match", token);
        request.Content = JsonContent.Create(new MatchRequest { ShareCode = shareCode });
        using var response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        try { await ThrowIfFailedAsync(response, cancellationToken).ConfigureAwait(false); }
        catch (Cs2MatchHistoryException error) when (error.Failure == Cs2MatchHistoryFailure.MatchNotFound) { return null; }
        var body = await response.Content.ReadFromJsonAsync<MatchResponse>(cancellationToken).ConfigureAwait(false)
                   ?? throw new Cs2MatchHistoryException(Cs2MatchHistoryFailure.Unexpected);
        DateTimeOffset? matchTime = body.MatchTime is > 0 and < 32503680000 ? DateTimeOffset.FromUnixTimeSeconds(body.MatchTime.Value) : null;
        return new Cs2MatchInfo(shareCode, body.MatchId ?? string.Empty, matchTime, body.Map, body.DemoUrl);
    }

    internal static async Task ThrowIfFailedAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        if (response.IsSuccessStatusCode) return;
        string? code = null;
        try
        {
            using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false));
            if (json.RootElement.ValueKind == JsonValueKind.Object && json.RootElement.TryGetProperty("code", out var value) && value.ValueKind == JsonValueKind.String)
                code = value.GetString();
        }
        catch (JsonException) { }
        throw new Cs2MatchHistoryException(Classify(response.StatusCode, code), RetryAfter(response));
    }

    internal static Cs2MatchHistoryFailure Classify(HttpStatusCode status, string? code) => (status, code) switch
    {
        (HttpStatusCode.Unauthorized, _) => Cs2MatchHistoryFailure.SignedOut,
        (HttpStatusCode.BadRequest, _) => Cs2MatchHistoryFailure.InvalidInput,
        (HttpStatusCode.UnprocessableEntity, _) => Cs2MatchHistoryFailure.SteamRejected,
        (HttpStatusCode.TooManyRequests, _) => Cs2MatchHistoryFailure.RateLimited,
        (HttpStatusCode.NotFound, "match_not_found") => Cs2MatchHistoryFailure.MatchNotFound,
        (HttpStatusCode.ServiceUnavailable or HttpStatusCode.BadGateway or HttpStatusCode.GatewayTimeout or HttpStatusCode.InternalServerError, _) => Cs2MatchHistoryFailure.Unavailable,
        // A 404 without the code is the site not offering this yet.
        (HttpStatusCode.NotFound, _) => Cs2MatchHistoryFailure.Unavailable,
        _ => Cs2MatchHistoryFailure.Unexpected
    };

    private static TimeSpan? RetryAfter(HttpResponseMessage response)
    {
        if (response.Headers.RetryAfter is not { } header) return null;
        if (header.Delta is { } delta) return delta;
        return header.Date is { } date ? date - DateTimeOffset.UtcNow : null;
    }

    private sealed class ShareCodesRequest
    {
        [JsonPropertyName("steamId")] public string SteamId { get; init; } = string.Empty;
        [JsonPropertyName("authCode")] public string AuthCode { get; init; } = string.Empty;
        [JsonPropertyName("knownCode")] public string KnownCode { get; init; } = string.Empty;
        public override string ToString() => nameof(ShareCodesRequest);
    }

    private sealed class ShareCodesResponse
    {
        [JsonPropertyName("codes")] public string[]? Codes { get; set; }
        [JsonPropertyName("complete")] public bool Complete { get; set; } = true;
    }

    private sealed class MatchRequest
    {
        [JsonPropertyName("shareCode")] public string ShareCode { get; init; } = string.Empty;
    }

    private sealed class MatchResponse
    {
        [JsonPropertyName("shareCode")] public string? ShareCode { get; set; }
        [JsonPropertyName("matchId")] public string? MatchId { get; set; }
        [JsonPropertyName("reservationId")] public string? ReservationId { get; set; }
        [JsonPropertyName("tvPort")] public int TvPort { get; set; }
        [JsonPropertyName("matchTime")] public long? MatchTime { get; set; }
        [JsonPropertyName("map")] public string? Map { get; set; }
        [JsonPropertyName("demoUrl")] public string? DemoUrl { get; set; }
    }
}
