using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using ClypDat.Core.Settings;

namespace ClypDat.App.Services;

/// <summary>What Spotify says is playing right now, or why it cannot say.</summary>
internal sealed record SpotifyNowPlaying(
    bool IsConnected,
    string? DisplayName,
    string? Track,
    string? Artist,
    string? Album,
    TimeSpan? Duration,
    TimeSpan? Progress,
    bool IsPlaying,
    DateTimeOffset? UpdatedAt,
    string? Error,
    // Cover art for the card. A URL rather than the image: it is only fetched
    // when a clip is actually saved, so a session that never clips never
    // downloads anything.
    string? ArtUrl = null)
{
    public static SpotifyNowPlaying Disconnected { get; } = new(false, null, null, null, null, null, null, false, null, null);

    /// <summary>"Song - Artist", the line an overlay leads with.</summary>
    public string Label => string.IsNullOrWhiteSpace(Track)
        ? string.Empty
        : string.IsNullOrWhiteSpace(Artist) ? Track! : $"{Track} - {Artist}";

    /// <summary>
    /// Where the track is now, carried forward from the last poll. A playing
    /// track advances in real time and the poll only samples it, so a caller
    /// asking twice between polls should get two different answers - otherwise
    /// the elapsed time reads as stuck for seconds at a stretch.
    /// </summary>
    public TimeSpan? ProgressNow
    {
        get
        {
            if (Progress is not { } progress) return null;
            if (!IsPlaying || UpdatedAt is not { } updated) return progress;
            var advanced = progress + (DateTimeOffset.UtcNow - updated);
            return Duration is { } duration && advanced > duration ? duration : advanced;
        }
    }
}

/// <summary>
/// Reads the signed-in listener's current track so a clip can say what was
/// playing while it was captured.
///
/// Authorization Code with PKCE and no client secret, the same shape
/// <see cref="XboxActivityService"/> uses: a desktop app cannot keep a secret,
/// and Spotify's own guidance for installed apps is PKCE for exactly that
/// reason. The refresh token is the only durable credential and it is written
/// through DPAPI, so it is readable by this Windows user and nobody else.
/// </summary>
internal sealed class SpotifyNowPlayingService : IDisposable
{
    // A client ID is a public identifier, not a credential - the same one ships
    // in every copy of the app, and PKCE is what stops it being useful on its
    // own. Registered at developer.spotify.com; the redirect below has to be on
    // that registration verbatim or Spotify rejects the authorize call before
    // the user ever sees a consent screen.
    public const string ClientId = "2b86cd1dd2bb4375a378b486312a3ab4";

    // Loopback by IP, not by name. Spotify's redirect rules take http only for
    // a loopback ADDRESS - "localhost" is refused - and the port has to be
    // fixed because it is half of what is registered.
    private const string RedirectUri = "http://127.0.0.1:51338/callback";

    // Only what the overlay needs. currently-playing alone covers track,
    // artist, length and position; playback-state is what makes a paused
    // player readable as paused rather than as nothing playing.
    private const string Scope = "user-read-currently-playing user-read-playback-state";

    private const string AuthorizeUri = "https://accounts.spotify.com/authorize";
    private const string TokenUri = "https://accounts.spotify.com/api/token";
    private const string CurrentlyPlayingUri = "https://api.spotify.com/v1/me/player/currently-playing";
    private const string ProfileUri = "https://api.spotify.com/v1/me";

    // Two seconds, not five. This is what decides how stale the track written
    // onto a clip can be - a save landing seconds after a song change would
    // otherwise carry the previous song - and it is what the settings row's
    // now-playing line reads as responsiveness. At one request per two seconds
    // per user it is a fraction of Spotify's per-app rolling limit.
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(2);

    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(20) };
    private readonly string _cachePath = Path.Combine(AppDataPaths.Root, "spotify-auth.bin");
    private CancellationTokenSource? _pollCts;
    private SpotifyTokens? _tokens;
    private SpotifyNowPlaying _snapshot = SpotifyNowPlaying.Disconnected;

    public SpotifyNowPlaying Snapshot => _snapshot;
    public event EventHandler<SpotifyNowPlaying>? Changed;

    /// <summary>
    /// The last track this process saw playing, or a disconnected snapshot.
    ///
    /// Static because a clip is saved by the capture worker and the replay
    /// buffer, neither of which is handed the view model that owns the
    /// connection - and a save is not a good moment to be starting an HTTP
    /// request anyway. The poll keeps this current; a save just reads it.
    /// </summary>
    public static SpotifyNowPlaying Current { get; private set; } = SpotifyNowPlaying.Disconnected;

    /// <summary>Whether the build carries a registration to authorize against.</summary>
    public static bool IsConfigured => !string.IsNullOrWhiteSpace(ClientId);

    /// <summary>Signs back in from the stored refresh token, without a browser.</summary>
    public async Task<bool> TryRestoreAsync(CancellationToken cancellationToken = default)
    {
        if (!IsConfigured) return false;
        _tokens = LoadTokens();
        if (_tokens is null) return false;
        try
        {
            await RefreshNowPlayingAsync(cancellationToken).ConfigureAwait(false);
            StartPolling();
            return true;
        }
        catch (Exception error)
        {
            AppLog.Error("Spotify: restoring the saved session failed.", error);
            _tokens = null;
            return false;
        }
    }

    public async Task<bool> ConnectAsync(CancellationToken cancellationToken = default)
    {
        if (!IsConfigured)
        {
            _snapshot = SpotifyNowPlaying.Disconnected with { Error = "This build has no Spotify application registered." };
            Changed?.Invoke(this, _snapshot);
            return false;
        }

        try
        {
            _tokens = LoadTokens();
            if (_tokens is null || _tokens.ExpiresAt <= DateTimeOffset.UtcNow.AddMinutes(2))
                _tokens = await RunPkceLoginAsync(cancellationToken).ConfigureAwait(false);

            await RefreshNowPlayingAsync(cancellationToken).ConfigureAwait(false);
            StartPolling();
            return true;
        }
        catch (Exception error)
        {
            AppLog.Error("Spotify: connection failed.", error);
            var message = error is HttpRequestException && !string.IsNullOrWhiteSpace(error.Message)
                ? error.Message
                : "Spotify connection failed. Try connecting again.";
            _snapshot = SpotifyNowPlaying.Disconnected with { Error = message };
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
        _snapshot = SpotifyNowPlaying.Disconnected;
        Current = _snapshot;
        Changed?.Invoke(this, _snapshot);
    }

    private void StartPolling()
    {
        _pollCts?.Cancel();
        _pollCts?.Dispose();
        _pollCts = new CancellationTokenSource();
        _ = PollAsync(_pollCts.Token);
    }

    private async Task PollAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(PollInterval, cancellationToken).ConfigureAwait(false);
                await RefreshNowPlayingAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { return; }
            catch (Exception error)
            {
                // A poll failing is a network blip or a token that needs one
                // more refresh, not a reason to sign the user out - the next
                // tick retries. Only an explicit Disconnect ends this loop.
                AppLog.Error("Spotify: now-playing poll failed.", error);
            }
        }
    }

    private async Task RefreshNowPlayingAsync(CancellationToken cancellationToken)
    {
        var token = await AccessTokenAsync(cancellationToken).ConfigureAwait(false);
        var name = _snapshot.DisplayName ?? await DisplayNameAsync(token, cancellationToken).ConfigureAwait(false);

        using var request = new HttpRequestMessage(HttpMethod.Get, CurrentlyPlayingUri);
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
        using var response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);

        // 204 is Spotify for "connected, nothing playing" - an idle player, not
        // a failure, and the overlay simply has nothing to say for that clip.
        if (response.StatusCode == HttpStatusCode.NoContent)
        {
            Publish(_snapshot with
            {
                IsConnected = true,
                DisplayName = name,
                Track = null,
                Artist = null,
                Album = null,
                Duration = null,
                Progress = null,
                IsPlaying = false,
                UpdatedAt = DateTimeOffset.UtcNow,
                Error = null
            });
            return;
        }

        response.EnsureSuccessStatusCode();
        var payload = await response.Content.ReadFromJsonAsync<CurrentlyPlayingResponse>(cancellationToken).ConfigureAwait(false);
        var item = payload?.Item;

        Publish(new SpotifyNowPlaying(
            IsConnected: true,
            DisplayName: name,
            Track: item?.Name,
            Artist: item?.Artists is { Length: > 0 } artists ? string.Join(", ", artists.Select(artist => artist.Name)) : null,
            Album: item?.Album?.Name,
            Duration: item?.DurationMs is { } duration ? TimeSpan.FromMilliseconds(duration) : null,
            Progress: payload?.ProgressMs is { } progress ? TimeSpan.FromMilliseconds(progress) : null,
            IsPlaying: payload?.IsPlaying ?? false,
            UpdatedAt: DateTimeOffset.UtcNow,
            Error: null,
            // Spotify returns its images largest first; the card is drawn at a
            // few hundred pixels, so the smallest one that still covers it is
            // the cheapest correct choice.
            ArtUrl: SmallestUsableArt(item?.Album?.Images)));
    }

    // 300px is the middle image Spotify publishes for an album, and the card's
    // art is 220 at 1080p. Anything smaller starts to show.
    private const int MinimumArtSize = 250;

    private static string? SmallestUsableArt(ImageResponse[]? images)
    {
        if (images is null || images.Length == 0) return null;
        return images
            .Where(image => !string.IsNullOrWhiteSpace(image.Url))
            .OrderBy(image => image.Width)
            .FirstOrDefault(image => image.Width >= MinimumArtSize)?.Url
            ?? images[0].Url;
    }

    private void Publish(SpotifyNowPlaying snapshot)
    {
        // Poll ticks that change nothing still arrive every five seconds; only
        // a real change is worth waking the UI for. Progress is deliberately
        // not compared - it moves on every tick by definition.
        var unchanged = _snapshot.IsConnected == snapshot.IsConnected &&
            string.Equals(_snapshot.Track, snapshot.Track, StringComparison.Ordinal) &&
            string.Equals(_snapshot.Artist, snapshot.Artist, StringComparison.Ordinal) &&
            _snapshot.IsPlaying == snapshot.IsPlaying &&
            string.Equals(_snapshot.Error, snapshot.Error, StringComparison.Ordinal);

        _snapshot = snapshot;
        Current = snapshot;
        if (!unchanged) Changed?.Invoke(this, snapshot);
    }

    private async Task<string?> DisplayNameAsync(string token, CancellationToken cancellationToken)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, ProfileUri);
            request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
            using var response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            var profile = await response.Content.ReadFromJsonAsync<ProfileResponse>(cancellationToken).ConfigureAwait(false);
            return profile?.DisplayName;
        }
        catch (Exception error)
        {
            // A name is a nicety. Losing it must not cost the connection.
            AppLog.Error("Spotify: profile lookup failed.", error);
            return null;
        }
    }

    private async Task<string> AccessTokenAsync(CancellationToken cancellationToken)
    {
        if (_tokens is null) throw new InvalidOperationException("Spotify is not connected.");
        if (_tokens.ExpiresAt > DateTimeOffset.UtcNow.AddMinutes(1)) return _tokens.AccessToken;

        var refreshed = await PostTokenAsync(new Dictionary<string, string>
        {
            ["grant_type"] = "refresh_token",
            ["refresh_token"] = _tokens.RefreshToken,
            ["client_id"] = ClientId
        }, cancellationToken).ConfigureAwait(false);

        // A refresh response may omit refresh_token, which means "keep using
        // the one you have" rather than "you no longer have one".
        _tokens = new SpotifyTokens(
            refreshed.AccessToken,
            string.IsNullOrWhiteSpace(refreshed.RefreshToken) ? _tokens.RefreshToken : refreshed.RefreshToken!,
            DateTimeOffset.UtcNow.AddSeconds(refreshed.ExpiresIn));
        SaveTokens(_tokens);
        return _tokens.AccessToken;
    }

    private async Task<SpotifyTokens> RunPkceLoginAsync(CancellationToken cancellationToken)
    {
        using var listener = new HttpListener();
        listener.Prefixes.Add("http://127.0.0.1:51338/");
        listener.Start();

        var verifier = Base64Url(RandomNumberGenerator.GetBytes(32));
        var challenge = Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));
        var state = Base64Url(RandomNumberGenerator.GetBytes(16));
        var query = $"client_id={Uri.EscapeDataString(ClientId)}&response_type=code" +
            $"&redirect_uri={Uri.EscapeDataString(RedirectUri)}&scope={Uri.EscapeDataString(Scope)}" +
            $"&state={Uri.EscapeDataString(state)}&code_challenge_method=S256&code_challenge={Uri.EscapeDataString(challenge)}";

        Process.Start(new ProcessStartInfo($"{AuthorizeUri}?{query}") { UseShellExecute = true });

        var context = await listener.GetContextAsync().WaitAsync(TimeSpan.FromMinutes(5), cancellationToken).ConfigureAwait(false);
        var code = context.Request.QueryString["code"];
        var returnedState = context.Request.QueryString["state"];
        var page = BrowserCallbackPage.Success();
        context.Response.ContentType = "text/html; charset=utf-8";
        context.Response.ContentLength64 = page.Length;
        await context.Response.OutputStream.WriteAsync(page, cancellationToken).ConfigureAwait(false);
        context.Response.Close();

        // The state check is what stops a page the user did not open from
        // handing us a code for an account they did not choose.
        if (!string.Equals(returnedState, state, StringComparison.Ordinal) || string.IsNullOrWhiteSpace(code))
            throw new InvalidOperationException("Spotify did not return an authorization code.");

        var tokens = await PostTokenAsync(new Dictionary<string, string>
        {
            ["grant_type"] = "authorization_code",
            ["code"] = code!,
            ["redirect_uri"] = RedirectUri,
            ["client_id"] = ClientId,
            ["code_verifier"] = verifier
        }, cancellationToken).ConfigureAwait(false);

        if (string.IsNullOrWhiteSpace(tokens.RefreshToken))
            throw new InvalidOperationException("Spotify did not return a refresh token.");

        var saved = new SpotifyTokens(tokens.AccessToken, tokens.RefreshToken!, DateTimeOffset.UtcNow.AddSeconds(tokens.ExpiresIn));
        SaveTokens(saved);
        return saved;
    }

    private async Task<OAuthTokenResponse> PostTokenAsync(Dictionary<string, string> form, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, TokenUri) { Content = new FormUrlEncodedContent(form) };
        using var response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException($"Spotify rejected the sign-in ({(int)response.StatusCode}: {OAuthError(body)}).");

        return JsonSerializer.Deserialize<OAuthTokenResponse>(body)
            ?? throw new InvalidOperationException("Spotify returned an unreadable token response.");
    }

    private SpotifyTokens? LoadTokens()
    {
        try
        {
            if (!File.Exists(_cachePath)) return null;
            var bytes = ProtectedData.Unprotect(File.ReadAllBytes(_cachePath), null, DataProtectionScope.CurrentUser);
            return JsonSerializer.Deserialize<SpotifyTokens>(bytes);
        }
        catch { return null; }
    }

    private void SaveTokens(SpotifyTokens tokens)
    {
        try
        {
            Directory.CreateDirectory(AppDataPaths.Root);
            var bytes = JsonSerializer.SerializeToUtf8Bytes(tokens);
            File.WriteAllBytes(_cachePath, ProtectedData.Protect(bytes, null, DataProtectionScope.CurrentUser));
        }
        catch (Exception error)
        {
            // The session still works; it just will not survive a restart.
            AppLog.Error("Spotify: saving the session failed.", error);
        }
    }

    private void TryDeleteCache() { try { if (File.Exists(_cachePath)) File.Delete(_cachePath); } catch { } }

    private static string Base64Url(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static string OAuthError(string body)
    {
        try
        {
            using var json = JsonDocument.Parse(body);
            var code = json.RootElement.TryGetProperty("error_description", out var description) ? description.GetString() : null;
            if (string.IsNullOrWhiteSpace(code))
                code = json.RootElement.TryGetProperty("error", out var error) ? error.GetString() : null;
            return string.IsNullOrWhiteSpace(code) ? "no error detail" : code;
        }
        catch (JsonException) { return "no error detail"; }
    }

    public void Dispose()
    {
        _pollCts?.Cancel();
        _pollCts?.Dispose();
        _http.Dispose();
    }

    private sealed record SpotifyTokens(string AccessToken, string RefreshToken, DateTimeOffset ExpiresAt);

    private sealed class OAuthTokenResponse
    {
        [JsonPropertyName("access_token")] public string AccessToken { get; set; } = string.Empty;
        [JsonPropertyName("refresh_token")] public string? RefreshToken { get; set; }
        [JsonPropertyName("expires_in")] public int ExpiresIn { get; set; }
    }

    private sealed class ProfileResponse
    {
        [JsonPropertyName("display_name")] public string? DisplayName { get; set; }
    }

    private sealed class CurrentlyPlayingResponse
    {
        [JsonPropertyName("progress_ms")] public int? ProgressMs { get; set; }
        [JsonPropertyName("is_playing")] public bool IsPlaying { get; set; }
        [JsonPropertyName("item")] public TrackResponse? Item { get; set; }
    }

    private sealed class TrackResponse
    {
        [JsonPropertyName("name")] public string? Name { get; set; }
        [JsonPropertyName("duration_ms")] public int? DurationMs { get; set; }
        [JsonPropertyName("artists")] public ArtistResponse[]? Artists { get; set; }
        [JsonPropertyName("album")] public AlbumResponse? Album { get; set; }
    }

    private sealed class ArtistResponse
    {
        [JsonPropertyName("name")] public string Name { get; set; } = string.Empty;
    }

    private sealed class AlbumResponse
    {
        [JsonPropertyName("name")] public string? Name { get; set; }
        [JsonPropertyName("images")] public ImageResponse[]? Images { get; set; }
    }

    private sealed class ImageResponse
    {
        [JsonPropertyName("url")] public string? Url { get; set; }
        [JsonPropertyName("width")] public int Width { get; set; }
    }
}
