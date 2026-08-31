using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ClypDat.Core.Settings;

namespace ClypDat.App.Services;

internal sealed record XboxActivitySnapshot(
    bool IsConnected,
    string? Gamertag,
    string? CurrentTitle,
    string? ConsoleName,
    DateTimeOffset? UpdatedAt,
    string? Error)
{
    public static XboxActivitySnapshot Disconnected { get; } = new(false, null, null, null, null, null);
}

/// <summary>Accountless, read-only Xbox activity link for the local Windows user.</summary>
internal sealed class XboxActivityService : IDisposable
{
    // Public client identifier. A client ID is not a credential; never add a secret here.
    public const string ClientId = "94deab64-e2f5-4ff0-bcf0-8989f79052e5";
    private const string RedirectUri = "http://localhost:51337/";
    private const string Scope = "XboxLive.signin XboxLive.offline_access";
    private static readonly Uri AuthorizeUri = new("https://login.microsoftonline.com/consumers/oauth2/v2.0/authorize");
    private static readonly Uri TokenUri = new("https://login.microsoftonline.com/consumers/oauth2/v2.0/token");
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(20) };
    private readonly string _cachePath = Path.Combine(AppDataPaths.Root, "xbox-auth.bin");
    private CancellationTokenSource? _pollCts;
    private XboxTokens? _tokens;
    private XboxActivitySnapshot _snapshot = XboxActivitySnapshot.Disconnected;

    public XboxActivitySnapshot Snapshot => _snapshot;
    public event EventHandler<XboxActivitySnapshot>? Changed;

    public async Task<bool> TryRestoreAsync(CancellationToken cancellationToken = default)
    {
        _tokens = LoadTokens();
        if (_tokens is null) return false;
        try
        {
            await RefreshPresenceAsync(cancellationToken).ConfigureAwait(false);
            StartPolling();
            return true;
        }
        catch
        {
            _tokens = null;
            return false;
        }
    }

    public async Task<bool> ConnectAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            _tokens = LoadTokens();
            if (_tokens is null || _tokens.ExpiresAt <= DateTimeOffset.UtcNow.AddMinutes(2))
                _tokens = await RunPkceLoginAsync(cancellationToken).ConfigureAwait(false);

            await RefreshPresenceAsync(cancellationToken).ConfigureAwait(false);
            _snapshot = _snapshot with { IsConnected = true, Error = null };
            Changed?.Invoke(this, _snapshot);
            StartPolling();
            return true;
        }
        catch (Exception error)
        {
            AppLog.Error("Xbox: connection failed.", error);
            var message = error is HttpRequestException && !string.IsNullOrWhiteSpace(error.Message)
                ? error.Message
                : "Xbox connection failed. Reconnect and try again.";
            _snapshot = new XboxActivitySnapshot(false, null, null, null, null, message);
            Changed?.Invoke(this, _snapshot);
            return false;
        }
    }

    public void Disconnect()
    {
        _pollCts?.Cancel();
        _pollCts?.Dispose();
        _pollCts = null;
        _tokens = null;
        TryDeleteCache();
        _snapshot = XboxActivitySnapshot.Disconnected;
        Changed?.Invoke(this, _snapshot);
    }

    private void StartPolling()
    {
        _pollCts?.Cancel();
        _pollCts = new CancellationTokenSource();
        _ = PollAsync(_pollCts.Token);
    }

    private async Task PollAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(_snapshot.CurrentTitle is null ? 60 : 15), cancellationToken).ConfigureAwait(false);
                await RefreshPresenceAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
            catch (Exception error)
            {
                AppLog.Error("Xbox: presence refresh failed.", error);
                _snapshot = _snapshot with { Error = "Xbox activity is temporarily unavailable." };
                Changed?.Invoke(this, _snapshot);
                try { await Task.Delay(TimeSpan.FromSeconds(60), cancellationToken).ConfigureAwait(false); }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
            }
        }
    }

    private async Task RefreshPresenceAsync(CancellationToken cancellationToken)
    {
        if (_tokens is null) throw new InvalidOperationException("Xbox is not authenticated.");
        if (_tokens.ExpiresAt <= DateTimeOffset.UtcNow.AddMinutes(2))
        {
            // Microsoft OAuth refresh support is intentionally isolated here; a failed refresh
            // makes the next explicit Connect prompt for login rather than looping noisily.
            _tokens = await RefreshTokenAsync(_tokens, cancellationToken).ConfigureAwait(false);
        }

        using var request = new HttpRequestMessage(HttpMethod.Get, "https://userpresence.xboxlive.com/users/me?level=title");
        request.Headers.TryAddWithoutValidation("Authorization", $"XBL3.0 x={_tokens.UserHash};{_tokens.XstsToken}");
        request.Headers.TryAddWithoutValidation("x-xbl-contract-version", "3");
        request.Headers.TryAddWithoutValidation("Accept-Language", "en-US");
        request.Headers.TryAddWithoutValidation("Accept", "application/json");
        using var response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var json = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);
        var activity = FindActiveTitle(json.RootElement);
        _snapshot = _snapshot with { IsConnected = true, CurrentTitle = activity.Title, ConsoleName = activity.Console, UpdatedAt = DateTimeOffset.UtcNow, Error = null };
        Changed?.Invoke(this, _snapshot);
    }

    private static (string? Title, string? Console) FindActiveTitle(JsonElement root)
    {
        if (!root.TryGetProperty("devices", out var devices)) return (null, null);
        string? best = null;
        string? console = null;
        DateTimeOffset newest = DateTimeOffset.MinValue;
        foreach (var device in devices.EnumerateArray())
        {
            var deviceType = device.TryGetProperty("type", out var type) ? type.GetString() : null;
            var deviceConsole = ConsoleLabel(deviceType);
            if (deviceConsole is null) continue;
            if (!device.TryGetProperty("titles", out var titles)) continue;
            foreach (var title in titles.EnumerateArray())
            {
                if (!title.TryGetProperty("name", out var name) || !title.TryGetProperty("state", out var state) ||
                    !string.Equals(state.GetString(), "active", StringComparison.OrdinalIgnoreCase)) continue;
                var timestamp = title.TryGetProperty("timestamp", out var stamp) && DateTimeOffset.TryParse(stamp.GetString(), out var parsed)
                    ? parsed : DateTimeOffset.MinValue;
                if (timestamp >= newest) { newest = timestamp; best = name.GetString(); console = deviceConsole; }
            }
        }
        return (string.IsNullOrWhiteSpace(best) ? null : best.Trim(), console);
    }

    private static string? ConsoleLabel(string? deviceType) => deviceType?.ToUpperInvariant() switch
    {
        "XBOXONE" or "XBOXONE-S" or "XBOXONE-X" => "Xbox One",
        "SCARLETT" or "XBOX-SERIES" or "XBOX-SERIES-X" => "Series X|S",
        "XBOX360" => "Xbox 360",
        "PC" or "WIN32" or "WINDOWS8" or "WINDOWSONECORE" or "WINDOWSONECOREMOBILE" or "WEB" or "IOS" or "ANDROID" or "NINTENDO" or "PLAYSTATION" => null,
        _ => "console"
    };

    private async Task<XboxTokens> RunPkceLoginAsync(CancellationToken cancellationToken)
    {
        using var listener = new HttpListener();
        listener.Prefixes.Add(RedirectUri);
        listener.Start();
        var verifier = Base64Url(RandomNumberGenerator.GetBytes(32));
        var challenge = Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));
        var state = Base64Url(RandomNumberGenerator.GetBytes(16));
        var query = $"client_id={Uri.EscapeDataString(ClientId)}&response_type=code&redirect_uri={Uri.EscapeDataString(RedirectUri)}&response_mode=query&scope={Uri.EscapeDataString(Scope)}&state={Uri.EscapeDataString(state)}&code_challenge={Uri.EscapeDataString(challenge)}&code_challenge_method=S256";
        Process.Start(new ProcessStartInfo($"{AuthorizeUri}?{query}") { UseShellExecute = true });
        var context = await listener.GetContextAsync().WaitAsync(TimeSpan.FromMinutes(5), cancellationToken).ConfigureAwait(false);
        var code = context.Request.QueryString["code"];
        if (!string.Equals(context.Request.QueryString["state"], state, StringComparison.Ordinal) || string.IsNullOrWhiteSpace(code))
            throw new InvalidOperationException("Xbox sign-in returned an invalid response.");
        var body = Encoding.UTF8.GetBytes("You may close this window and return to ClypDat.");
        context.Response.ContentLength64 = body.Length;
        await context.Response.OutputStream.WriteAsync(body, cancellationToken).ConfigureAwait(false);
        context.Response.Close();
        using var form = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["client_id"] = ClientId, ["grant_type"] = "authorization_code", ["code"] = code,
            ["redirect_uri"] = RedirectUri, ["code_verifier"] = verifier
        });
        using var response = await _http.PostAsync(TokenUri, form, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        var oauth = JsonSerializer.Deserialize<OAuthTokenResponse>(await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false))
            ?? throw new InvalidOperationException("Microsoft sign-in returned no token.");
        if (string.IsNullOrWhiteSpace(oauth.RefreshToken)) throw new InvalidOperationException("Microsoft sign-in did not return a refresh token.");
        return await ExchangeXboxTokensAsync(oauth.AccessToken, oauth.RefreshToken, oauth.ExpiresIn, cancellationToken).ConfigureAwait(false);
    }

    private async Task<XboxTokens> RefreshTokenAsync(XboxTokens old, CancellationToken cancellationToken)
    {
        using var form = new FormUrlEncodedContent(new Dictionary<string, string>
        { ["client_id"] = ClientId, ["grant_type"] = "refresh_token", ["refresh_token"] = old.RefreshToken, ["scope"] = Scope });
        using var response = await _http.PostAsync(TokenUri, form, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        var oauth = JsonSerializer.Deserialize<OAuthTokenResponse>(await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false))
            ?? throw new InvalidOperationException("Microsoft refresh returned no token.");
        return await ExchangeXboxTokensAsync(oauth.AccessToken, oauth.RefreshToken ?? old.RefreshToken, oauth.ExpiresIn, cancellationToken).ConfigureAwait(false);
    }

    private async Task<XboxTokens> ExchangeXboxTokensAsync(string accessToken, string refreshToken, int expiresIn, CancellationToken cancellationToken)
    {
        var user = await PostXboxAsync("https://user.auth.xboxlive.com/user/authenticate", new
        { Properties = new { AuthMethod = "RPS", SiteName = "user.auth.xboxlive.com", RpsTicket = $"d={accessToken}" }, RelyingParty = "http://auth.xboxlive.com", TokenType = "JWT" }, cancellationToken).ConfigureAwait(false);
        var userToken = user.RootElement.GetProperty("Token").GetString()!;
        var xsts = await PostXboxAsync("https://xsts.auth.xboxlive.com/xsts/authorize", new
        { Properties = new { SandboxId = "RETAIL", UserTokens = new[] { userToken } }, RelyingParty = "http://xboxlive.com", TokenType = "JWT" }, cancellationToken).ConfigureAwait(false);
        var xui = xsts.RootElement.GetProperty("DisplayClaims").GetProperty("xui")[0];
        var tokens = new XboxTokens(refreshToken, xui.GetProperty("uhs").GetString()!, xsts.RootElement.GetProperty("Token").GetString()!, DateTimeOffset.UtcNow.AddSeconds(expiresIn));
        SaveTokens(tokens);
        return tokens;
    }

    private async Task<JsonDocument> PostXboxAsync(string uri, object payload, CancellationToken cancellationToken)
    {
        using var response = await _http.PostAsJsonAsync(uri, payload, cancellationToken).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            string detail = "no error detail";
            try
            {
                using var error = JsonDocument.Parse(body);
                if (error.RootElement.TryGetProperty("XErr", out var xerr)) detail = $"XErr={xerr.GetRawText()}";
                else if (error.RootElement.TryGetProperty("Message", out var message)) detail = message.GetString() ?? detail;
                else if (error.RootElement.TryGetProperty("error", out var code)) detail = code.GetString() ?? detail;
            }
            catch (JsonException) { }
            throw new HttpRequestException($"Xbox endpoint {new Uri(uri).Host} rejected the request ({(int)response.StatusCode}); {detail}.");
        }
        return JsonDocument.Parse(body);
    }

    private XboxTokens? LoadTokens()
    {
        try
        {
            if (!File.Exists(_cachePath)) return null;
            var protectedBytes = File.ReadAllBytes(_cachePath);
            var bytes = ProtectedData.Unprotect(protectedBytes, null, DataProtectionScope.CurrentUser);
            return JsonSerializer.Deserialize<XboxTokens>(bytes);
        }
        catch { return null; }
    }

    private void SaveTokens(XboxTokens tokens)
    {
        Directory.CreateDirectory(AppDataPaths.Root);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(tokens);
        File.WriteAllBytes(_cachePath, ProtectedData.Protect(bytes, null, DataProtectionScope.CurrentUser));
    }

    private void TryDeleteCache() { try { if (File.Exists(_cachePath)) File.Delete(_cachePath); } catch { } }
    private static string Base64Url(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    public void Dispose() { _pollCts?.Cancel(); _pollCts?.Dispose(); _http.Dispose(); }

    private sealed class OAuthTokenResponse
    {
        [System.Text.Json.Serialization.JsonPropertyName("access_token")] public string AccessToken { get; set; } = string.Empty;
        [System.Text.Json.Serialization.JsonPropertyName("refresh_token")] public string? RefreshToken { get; set; }
        [System.Text.Json.Serialization.JsonPropertyName("expires_in")] public int ExpiresIn { get; set; }
    }
    private sealed record XboxTokens(string RefreshToken, string UserHash, string XstsToken, DateTimeOffset ExpiresAt);
}
