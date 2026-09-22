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
    // Cover art is fetched when saving clips or previewing the overlay dialog.
    string? ArtUrl = null,
    string? TrackId = null,
    // A cover read from Windows' media controls (SpotifyLocalSource), for a
    // track the Web API has not described. Cached in app data until a saved
    // clip imports it into the library's archive.
    string? LocalArtPath = null)
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
/// Combines the two places the playing track can come from. Pure, so the
/// precedence is testable without Windows' media controls or Spotify.
/// </summary>
internal static class SpotifySnapshotMerge
{
    /// <param name="enabled">Spotify is turned on in ClypDat at all.</param>
    /// <param name="local">The Spotify app on this PC, or null when it is not running.</param>
    /// <param name="web">The signed-in account's player, or null when not signed in.</param>
    public static SpotifyNowPlaying Merge(bool enabled, SpotifyNowPlaying? local, SpotifyNowPlaying? web, string? accountName, string? notice)
    {
        if (!enabled) return SpotifyNowPlaying.Disconnected with { Error = notice };
        var localTrack = local is { Track: not null } ? local : null;
        var webTrack = web is { Track: not null } ? web : null;

        SpotifyNowPlaying chosen;
        if (localTrack is not null && webTrack is not null && SameTrack(localTrack, webTrack))
        {
            // The PC's session is instant and free; the account adds what the
            // media controls do not carry - the cover URL, the track id, and a
            // position when the app reports none.
            chosen = localTrack with
            {
                ArtUrl = webTrack.ArtUrl,
                TrackId = webTrack.TrackId,
                Duration = localTrack.Duration ?? webTrack.Duration,
                Progress = localTrack.Progress ?? webTrack.ProgressNow,
                UpdatedAt = localTrack.Progress is null ? DateTimeOffset.UtcNow : localTrack.UpdatedAt,
            };
        }
        // Different songs: the one actually playing is the one to show. The
        // desktop app keeps its last track, paused, while a phone plays on.
        else if (webTrack is { IsPlaying: true } && localTrack is not { IsPlaying: true }) chosen = webTrack;
        else if (localTrack is not null) chosen = localTrack;
        else if (webTrack is not null) chosen = webTrack;
        else chosen = SpotifyNowPlaying.Disconnected with { IsConnected = true, UpdatedAt = DateTimeOffset.UtcNow };

        return chosen with { IsConnected = true, DisplayName = accountName, Error = notice };
    }

    // The media controls give Spotify's title and its artists joined one way,
    // the Web API another; the title plus any shared artist is enough.
    internal static bool SameTrack(SpotifyNowPlaying a, SpotifyNowPlaying b)
    {
        if (!string.Equals(a.Track?.Trim(), b.Track?.Trim(), StringComparison.OrdinalIgnoreCase)) return false;
        if (string.IsNullOrWhiteSpace(a.Artist) || string.IsNullOrWhiteSpace(b.Artist)) return true;
        var first = FirstArtist(b.Artist!);
        var other = FirstArtist(a.Artist!);
        return a.Artist!.Contains(first, StringComparison.OrdinalIgnoreCase) || b.Artist!.Contains(other, StringComparison.OrdinalIgnoreCase);
    }

    private static string FirstArtist(string artists) => artists.Split(',', ';', '&')[0].Trim();
}

/// <summary>Spotify refused an account that signed in fine - Development Mode's allow-list.</summary>
internal sealed class SpotifyNotApprovedException(string? detail)
    : Exception($"Spotify refused this account ({detail ?? "403"}).");

/// <summary>
/// Spotify's accounts service refused a token request. Still an
/// <see cref="HttpRequestException"/> with its status, so a dead refresh token
/// (400/401) reads as a lost account wherever that is checked.
/// </summary>
internal sealed class SpotifyTokenException(HttpStatusCode status, string? code, string detail)
    : HttpRequestException($"Spotify rejected the sign-in ({(int)status}: {detail}).", null, status)
{
    public string? Code { get; } = code;
}

/// <summary>A sign-in that stopped for a reason the user should read as-is.</summary>
internal sealed class SpotifySignInException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>
/// Reads the listener's current track so a clip can say what was playing
/// while it was captured.
///
/// Two sources. The Spotify app on this PC, through Windows' media controls
/// (<see cref="SpotifyLocalSource"/>), needs no sign-in and works for anyone -
/// it is the default. Signing in with Spotify adds the Web API: Spotify's own
/// cover art and Spotify playing on another device.
///
/// Sign-in uses the person's own Spotify developer app (their Client ID, set
/// up with SpotifyOwnAppDialog), never one shared by ClypDat. Spotify caps a
/// Development Mode app at five approved accounts and grants more only to
/// businesses with 250k monthly users; with their own app each person is its
/// first user, so the cap never comes into it.
///
/// Web sign-in is Authorization Code with PKCE and no client secret, the same
/// shape <see cref="XboxActivityService"/> uses: a desktop app cannot keep a
/// secret. The refresh token is the only durable credential and it is written
/// through DPAPI, so it is readable by this Windows user and nobody else.
/// </summary>
internal sealed class SpotifyNowPlayingService : IDisposable
{
    // Loopback by IP, not by name. Spotify's redirect rules take http only for
    // a loopback ADDRESS - "localhost" is refused - and the port has to be
    // fixed because it is half of what the user registers on their Spotify app
    // (SpotifyOwnAppDialog tells them this exact value). Spotify rejects the
    // authorize call before any consent screen if it does not match.
    private const int RedirectPort = 51338;
    public const string RedirectUri = "http://127.0.0.1:51338/callback";

    // Only what the overlay needs. currently-playing alone covers track,
    // artist, length and position; playback-state is what makes a paused
    // player readable as paused rather than as nothing playing.
    private const string Scope = "user-read-currently-playing user-read-playback-state";

    private const string AuthorizeUri = "https://accounts.spotify.com/authorize";
    private const string TokenUri = "https://accounts.spotify.com/api/token";
    private const string CurrentlyPlayingUri = "https://api.spotify.com/v1/me/player/currently-playing";
    private const string ProfileUri = "https://api.spotify.com/v1/me";

    internal const string NotApprovedMessage =
        "Spotify refused this account. In your Spotify app's settings, open User Management and add the Spotify account you signed in with. Reading the Spotify app on this PC meanwhile.";
    private const string AccountExpiredMessage = "Spotify sign-in expired. Reading the Spotify app on this PC instead; sign in again for cover art from other devices.";
    internal const string SharedAppRetiredMessage =
        "Spotify sign-in now uses your own Spotify app. Reading the Spotify app on this PC; set up your own under Advanced to sign in again.";

    // With the PC's own session showing the song, the Web API only adds cover
    // art and the track id, and a track change wakes it at once - so it can
    // poll slowly. Without one it is the only source and has to keep up. Every
    // user's polls share one per-app rate budget, which two-second polling
    // from everyone was exhausting.
    private static readonly TimeSpan PollWithLocal = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan PollWebOnly = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan SignInWindow = TimeSpan.FromMinutes(5);

    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(20) };
    private readonly string _cachePath = Path.Combine(AppDataPaths.Root, "spotify-auth.bin");
    private readonly SemaphoreSlim _pollWake = new(0, 1);
    private readonly SpotifyLocalSource _local = new();
    private readonly object _gate = new();
    private CancellationTokenSource? _pollCts;
    private CancellationTokenSource? _signInCts;
    private SpotifyTokens? _tokens;
    private SpotifyNowPlaying? _web;
    private string? _accountName;
    private string? _notice;
    private bool _enabled;
    private SpotifyNowPlaying _snapshot = SpotifyNowPlaying.Disconnected;
    private DateTimeOffset _lastRefresh;
    private string? _lastLocalTrack;
    // Set from a 429's Retry-After. Polling on through a rate limit is what
    // kept a tester's app limited for over half an hour.
    private DateTimeOffset _rateLimitedUntil;
    private bool _policyPaused;

    public SpotifyNowPlayingService()
    {
        _local.Changed += (_, _) => OnLocalChanged();
    }

    public SpotifyNowPlaying Snapshot => _snapshot;
    public event EventHandler<SpotifyNowPlaying>? Changed;
    /// <summary>Raised for every successful poll, including progress-only corrections.</summary>
    public event EventHandler<SpotifyNowPlaying>? Sampled;

    /// <summary>
    /// The last track this process saw playing, or a disconnected snapshot.
    ///
    /// Static because a clip is saved by the capture worker and the replay
    /// buffer, neither of which is handed the view model that owns the
    /// connection - and a save is not a good moment to be starting an HTTP
    /// request anyway. The poll keeps this current; a save just reads it.
    /// </summary>
    public static SpotifyNowPlaying Current { get; private set; } = SpotifyNowPlaying.Disconnected;

    /// <summary>
    /// The user's own Spotify app to sign in through, or null for none. Set
    /// from settings at launch; a successful <see cref="SignInAsync"/> sets it.
    /// </summary>
    public string? ClientId { get; set; }

    /// <summary>A Client ID is set up to sign in with.</summary>
    public bool IsConfigured => IsValidClientId(ClientId);

    // Spotify Client IDs are 32 hex characters.
    internal static bool IsValidClientId(string? value) =>
        value is { Length: 32 } && value.All(Uri.IsHexDigit);

    /// <summary>The Client ID in the form it is stored, or null when it is not one.</summary>
    internal static string? NormaliseClientId(string? value)
    {
        var trimmed = value?.Trim();
        return IsValidClientId(trimmed) ? trimmed!.ToLowerInvariant() : null;
    }

    /// <summary>Spotify is turned on - reading this PC, and the account if signed in.</summary>
    public bool IsEnabled => _enabled;
    /// <summary>Signed in with a Spotify account as well.</summary>
    public bool IsAccountConnected => _tokens is not null;
    public string? AccountName => _accountName;
    /// <summary>The Spotify app on this PC has a media session.</summary>
    public bool IsLocalAvailable => _local.Current is not null;
    public bool IsSigningIn => _signInCts is not null;

    private bool Blocked => _policyPaused || NoticeBoardService.IsBlocked("pause-spotify");

    /// <summary>
    /// Turns Spotify on at launch: the local source, plus the saved account if
    /// there is one. A missing or dead account session only costs the account.
    /// </summary>
    public async Task<bool> TryRestoreAsync(CancellationToken cancellationToken = default)
    {
        if (NoticeBoardService.IsBlocked("pause-spotify")) { SetPolicyPaused(true); return false; }
        Enable();
        var tokens = LoadTokens();
        if (tokens is null) return true;
        // Signed in through ClypDat's own Spotify app, which is gone (it could
        // only ever take five people), or through an app the user has since
        // replaced: that session cannot be refreshed any more.
        if (tokens.ClientId is null || !string.Equals(tokens.ClientId, NormaliseClientId(ClientId), StringComparison.Ordinal))
        {
            AppLog.Info(tokens.ClientId is null
                ? "Spotify: dropping a sign-in from ClypDat's retired shared Spotify app."
                : "Spotify: dropping a sign-in from a Spotify app that is no longer set up.");
            DropAccount(tokens.ClientId is null ? SharedAppRetiredMessage : null);
            return true;
        }
        _tokens = tokens;
        try
        {
            _accountName ??= await DisplayNameAsync(await AccessTokenAsync(cancellationToken).ConfigureAwait(false), cancellationToken).ConfigureAwait(false);
            await RefreshWebAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception error) when (IsAccountLost(error))
        {
            AppLog.Error("Spotify: restoring the saved account failed.", error);
            DropAccount(error is SpotifyNotApprovedException ? NotApprovedMessage : AccountExpiredMessage);
            return true;
        }
        catch (Exception error) when (!cancellationToken.IsCancellationRequested)
        {
            // A timeout or a 5xx at launch is not a lost session: keep the
            // tokens and let the poll retry, rather than going dark until the
            // next restart.
            AppLog.Error("Spotify: first now-playing read failed; polling will retry.", error);
        }
        StartPolling();
        return true;
    }

    /// <summary>Turns Spotify on with no sign-in: the Spotify app on this PC.</summary>
    public bool Enable()
    {
        if (Blocked) { SetPolicyPaused(true); return false; }
        _enabled = true;
        _local.Start();
        Recompute();
        return true;
    }

    /// <summary>
    /// Optional Spotify sign-in through the user's own Spotify app, for cover
    /// art and other devices. Always opens Spotify's own page with the account
    /// picker, so a refused account can be swapped for another. Pressing it
    /// again restarts the attempt.
    /// </summary>
    public async Task<bool> SignInAsync(string clientId, CancellationToken cancellationToken = default)
    {
        if (Blocked) { SetPolicyPaused(true); return false; }
        var id = NormaliseClientId(clientId);
        if (id is null)
        {
            _notice = "That isn't a Spotify Client ID. It is 32 letters and numbers, shown on your Spotify app's page.";
            Recompute();
            return false;
        }

        CancelSignIn();
        using var attempt = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        lock (_gate) _signInCts = attempt;
        try
        {
            if (!_enabled) Enable();
            var (tokens, name) = await RunPkceLoginAsync(id, attempt.Token).ConfigureAwait(false);
            SaveTokens(tokens);
            ClientId = id;
            _tokens = tokens;
            _accountName = name;
            _notice = null;
            _web = null;
            AppLog.Info("Spotify: account signed in.");
            try { await RefreshWebAsync(attempt.Token).ConfigureAwait(false); }
            catch (Exception error) when (!IsAccountLost(error) && !attempt.IsCancellationRequested)
            {
                AppLog.Error("Spotify: first now-playing read after sign-in failed; polling will retry.", error);
            }
            StartPolling();
            Recompute();
            return true;
        }
        catch (OperationCanceledException) when (attempt.IsCancellationRequested)
        {
            AppLog.Info("Spotify: sign-in cancelled.");
            return false;
        }
        catch (Exception error)
        {
            AppLog.Error("Spotify: sign-in failed.", error);
            _notice = error switch
            {
                SpotifyNotApprovedException => NotApprovedMessage,
                SpotifySignInException => error.Message,
                TimeoutException => "Spotify sign-in timed out. Press Sign in again.",
                HttpRequestException { StatusCode: null } => "Couldn't reach Spotify. Check your connection and try again.",
                _ => "Spotify sign-in failed. Try again.",
            };
            Recompute();
            return false;
        }
        finally
        {
            lock (_gate) if (ReferenceEquals(_signInCts, attempt)) _signInCts = null;
        }
    }

    /// <summary>Stops waiting for the browser. The listener closes with it.</summary>
    public void CancelSignIn()
    {
        CancellationTokenSource? pending;
        lock (_gate) { pending = _signInCts; _signInCts = null; }
        try { pending?.Cancel(); } catch (ObjectDisposedException) { }
    }

    /// <summary>Forgets the Spotify account but keeps reading this PC.</summary>
    public void SignOutAccount()
    {
        DropAccount(null);
    }

    /// <summary>Turns Spotify off entirely: this PC and the account.</summary>
    public void Disconnect()
    {
        CancelSignIn();
        StopPolling();
        _tokens = null;
        _web = null;
        _accountName = null;
        _notice = null;
        TryDeleteCache();
        _enabled = false;
        _local.Stop();
        _snapshot = SpotifyNowPlaying.Disconnected;
        Current = _snapshot;
        Sampled?.Invoke(this, _snapshot);
        Changed?.Invoke(this, _snapshot);
    }

    public void SetPolicyPaused(bool paused)
    {
        if (_policyPaused == paused) return;
        _policyPaused = paused;
        if (paused)
        {
            CancelSignIn();
            StopPolling();
            _local.Stop();
            _snapshot = SpotifyNowPlaying.Disconnected with { Error = "Spotify is temporarily paused by ClypDat." };
            Current = _snapshot;
            Sampled?.Invoke(this, _snapshot);
            Changed?.Invoke(this, _snapshot);
            AppLog.Info("Policy transition: Spotify paused.");
            return;
        }

        // Reconnecting is MainWindowViewModel.ApplyRemotePolicy's job: it knows
        // whether the user still has Spotify turned on.
        AppLog.Info("Policy transition: Spotify resumed.");
    }

    // The account's tokens go, the local source stays. A notice says why when
    // it was not the user's choice.
    private void DropAccount(string? notice)
    {
        StopPolling();
        _tokens = null;
        _web = null;
        _accountName = null;
        _notice = notice;
        TryDeleteCache();
        Recompute();
    }

    private void OnLocalChanged()
    {
        if (!_enabled || Blocked) return;
        // A new song on this PC: have the account describe it now rather than
        // at its next slow poll, so the cover URL arrives with the song.
        var track = _local.Current?.Track;
        if (!string.Equals(track, _lastLocalTrack, StringComparison.Ordinal))
        {
            _lastLocalTrack = track;
            if (track is not null && _tokens is not null) WakePoll();
        }
        Recompute();
    }

    // The local source and the account poll finish on different threads.
    private readonly object _publishGate = new();

    private void Recompute()
    {
        if (Blocked) return;
        lock (_publishGate) Publish(SpotifySnapshotMerge.Merge(_enabled, _local.Current, _web, _accountName, _notice));
    }

    private void StartPolling()
    {
        if (Blocked || _tokens is null) return;
        CancellationTokenSource next;
        lock (_gate)
        {
            _pollCts?.Cancel();
            _pollCts?.Dispose();
            _pollCts = next = new CancellationTokenSource();
        }
        _ = PollAsync(next.Token);
    }

    private void StopPolling()
    {
        lock (_gate)
        {
            _pollCts?.Cancel();
            _pollCts?.Dispose();
            _pollCts = null;
        }
    }

    /// <summary>
    /// Asks for a refresh at the next opportunity without waiting out the
    /// rest of the current poll interval - what the window regaining focus
    /// asks for (<see cref="ClypDatAccountActivityService.RefreshSoon"/> does
    /// the same for the ClypDat account). No-ops if there is nothing to
    /// refresh; debounced so rapid focus toggling cannot flood the poll.
    /// </summary>
    public void RefreshSoon()
    {
        if (!_enabled) return;
        _local.Wake();
        if (_tokens is null) return;
        if (DateTimeOffset.UtcNow - _lastRefresh < TimeSpan.FromSeconds(1)) return;
        WakePoll();
    }

    private void WakePoll()
    {
        try { if (_pollWake.CurrentCount == 0) _pollWake.Release(); }
        catch (SemaphoreFullException) { }
        catch (ObjectDisposedException) { }
    }

    private async Task PollAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            if (Blocked) return;
            try
            {
                var interval = _local.Current is not null ? PollWithLocal : PollWebOnly;
                var backoff = _rateLimitedUntil - DateTimeOffset.UtcNow;
                await _pollWake.WaitAsync(backoff > interval ? backoff : interval, cancellationToken).ConfigureAwait(false);
                // A focus-triggered wake does not get to jump a rate limit.
                if (DateTimeOffset.UtcNow < _rateLimitedUntil) continue;
                await RefreshWebAsync(cancellationToken).ConfigureAwait(false);
                _lastRefresh = DateTimeOffset.UtcNow;
            }
            // Only our own cancellation ends the loop. HttpClient's timeout is
            // also an OperationCanceledException; returning on it stopped the
            // poll for good after one slow response, and every clip from then
            // on was saved without its song.
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { return; }
            catch (Exception error) when (IsAccountLost(error))
            {
                // Refused outright (not approved) or the refresh token is dead:
                // polling again cannot fix either. The PC's own session carries
                // on; the account needs a fresh sign-in.
                AppLog.Error("Spotify: the account session ended.", error);
                DropAccount(error is SpotifyNotApprovedException ? NotApprovedMessage : AccountExpiredMessage);
                return;
            }
            catch (Exception error)
            {
                // A poll failing is a network blip or a token that needs one
                // more refresh, not a reason to sign the user out - the next
                // tick retries.
                AppLog.Error("Spotify: now-playing poll failed.", error);
            }
        }
    }

    // Spotify answers a dead/revoked refresh token with 400 (invalid_grant) or
    // 401; a 403 means the account is not allowed to use this app at all.
    // Anything else (a timeout, a 5xx) is transient and worth retrying.
    private static bool IsAccountLost(Exception error) => error switch
    {
        SpotifyNotApprovedException => true,
        HttpRequestException { StatusCode: HttpStatusCode.BadRequest or HttpStatusCode.Unauthorized } => true,
        _ => false,
    };

    private async Task RefreshWebAsync(CancellationToken cancellationToken)
    {
        if (Blocked || _tokens is null) return;
        var token = await AccessTokenAsync(cancellationToken).ConfigureAwait(false);

        using var request = new HttpRequestMessage(HttpMethod.Get, CurrentlyPlayingUri);
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
        using var response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);

        // 204 is Spotify for "connected, nothing playing" - an idle player, not
        // a failure, and the overlay simply has nothing to say for that clip.
        if (response.StatusCode == HttpStatusCode.NoContent)
        {
            SetWeb(SpotifyNowPlaying.Disconnected with { IsConnected = true, UpdatedAt = DateTimeOffset.UtcNow });
            return;
        }

        if (response.StatusCode == HttpStatusCode.TooManyRequests)
        {
            var now = DateTimeOffset.UtcNow;
            var retryAfter = response.Headers.RetryAfter is { } header
                ? header.Delta ?? (header.Date is { } date ? date - now : null)
                : null;
            var delay = retryAfter is { } wait && wait > TimeSpan.Zero ? wait : TimeSpan.FromSeconds(30);
            if (delay > TimeSpan.FromMinutes(10)) delay = TimeSpan.FromMinutes(10);
            _rateLimitedUntil = now + delay;
            AppLog.Info($"Spotify: rate limited; pausing now-playing polls for {delay.TotalSeconds:0}s.");
            return;
        }

        await ThrowIfNotApprovedAsync(response, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        var payload = await response.Content.ReadFromJsonAsync<CurrentlyPlayingResponse>(cancellationToken).ConfigureAwait(false);
        var item = payload?.Item;

        SetWeb(new SpotifyNowPlaying(
            IsConnected: true,
            DisplayName: null,
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
            ArtUrl: SmallestUsableArt(item?.Album?.Images),
            TrackId: item?.Id));
    }

    private void SetWeb(SpotifyNowPlaying web)
    {
        if (Blocked || _tokens is null) return;
        _web = web;
        Recompute();
    }

    // A Development Mode app answers any account not on its allow-list with
    // 403 on every Web API call, after a sign-in that looked fine. The body
    // says "the user may not be registered".
    private static async Task ThrowIfNotApprovedAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        if (response.StatusCode != HttpStatusCode.Forbidden) return;
        var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        throw new SpotifyNotApprovedException(ApiError(body));
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

    private bool _publishedLocalAvailable;

    private void Publish(SpotifyNowPlaying snapshot)
    {
        // Ticks that change nothing still arrive every few seconds; only a real
        // change is worth waking the UI for. Progress is deliberately not
        // compared - it moves on every tick by definition.
        var unchanged = _snapshot.IsConnected == snapshot.IsConnected &&
            string.Equals(_snapshot.TrackId, snapshot.TrackId, StringComparison.Ordinal) &&
            string.Equals(_snapshot.Track, snapshot.Track, StringComparison.Ordinal) &&
            string.Equals(_snapshot.Artist, snapshot.Artist, StringComparison.Ordinal) &&
            string.Equals(_snapshot.DisplayName, snapshot.DisplayName, StringComparison.Ordinal) &&
            _snapshot.IsPlaying == snapshot.IsPlaying &&
            string.Equals(_snapshot.Error, snapshot.Error, StringComparison.Ordinal) &&
            // The settings line says whether the Spotify app is open, which
            // can change while the snapshot itself (nothing playing) does not.
            _publishedLocalAvailable == IsLocalAvailable;
        _publishedLocalAvailable = IsLocalAvailable;

        _snapshot = snapshot;
        Current = snapshot;
        Sampled?.Invoke(this, snapshot);
        if (!unchanged) Changed?.Invoke(this, snapshot);
    }

    private async Task<string?> DisplayNameAsync(string token, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, ProfileUri);
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
        using var response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        await ThrowIfNotApprovedAsync(response, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            // A name is a nicety. Losing it must not cost the connection.
            AppLog.Info($"Spotify: profile lookup returned {(int)response.StatusCode}.");
            return null;
        }
        var profile = await response.Content.ReadFromJsonAsync<ProfileResponse>(cancellationToken).ConfigureAwait(false);
        return profile?.DisplayName;
    }

    private async Task<string> AccessTokenAsync(CancellationToken cancellationToken)
    {
        var current = _tokens ?? throw new InvalidOperationException("Spotify is not signed in.");
        if (current.ExpiresAt > DateTimeOffset.UtcNow.AddMinutes(1)) return current.AccessToken;

        var refreshed = await PostTokenAsync(new Dictionary<string, string>
        {
            ["grant_type"] = "refresh_token",
            ["refresh_token"] = current.RefreshToken,
            ["client_id"] = current.ClientId ?? throw new InvalidOperationException("Spotify sign-in has no Client ID.")
        }, cancellationToken).ConfigureAwait(false);

        // A refresh response may omit refresh_token, which means "keep using
        // the one you have" rather than "you no longer have one".
        var next = new SpotifyTokens(
            refreshed.AccessToken,
            string.IsNullOrWhiteSpace(refreshed.RefreshToken) ? current.RefreshToken : refreshed.RefreshToken!,
            DateTimeOffset.UtcNow.AddSeconds(refreshed.ExpiresIn),
            current.ClientId);
        // Signed out (or swapped accounts) while the refresh was in flight.
        if (!ReferenceEquals(_tokens, current)) throw new OperationCanceledException("Spotify account changed during refresh.");
        _tokens = next;
        SaveTokens(next);
        return next.AccessToken;
    }

    private async Task<(SpotifyTokens Tokens, string? Name)> RunPkceLoginAsync(string clientId, CancellationToken cancellationToken)
    {
        using var listener = await StartListenerAsync(cancellationToken).ConfigureAwait(false);

        var verifier = Base64Url(RandomNumberGenerator.GetBytes(32));
        var challenge = Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));
        var state = Base64Url(RandomNumberGenerator.GetBytes(16));
        // show_dialog: without it Spotify silently reuses whichever account the
        // browser is signed into - including one it has already refused - and
        // there is no way to pick another.
        var query = $"client_id={Uri.EscapeDataString(clientId)}&response_type=code" +
            $"&redirect_uri={Uri.EscapeDataString(RedirectUri)}&scope={Uri.EscapeDataString(Scope)}" +
            $"&state={Uri.EscapeDataString(state)}&code_challenge_method=S256&code_challenge={Uri.EscapeDataString(challenge)}" +
            "&show_dialog=true";

        Process.Start(new ProcessStartInfo($"{AuthorizeUri}?{query}") { UseShellExecute = true });

        var deadline = DateTimeOffset.UtcNow.Add(SignInWindow);
        while (true)
        {
            var remaining = deadline - DateTimeOffset.UtcNow;
            if (remaining <= TimeSpan.Zero) throw new TimeoutException("Spotify sign-in timed out.");
            HttpListenerContext context;
            try { context = await listener.GetContextAsync().WaitAsync(remaining, cancellationToken).ConfigureAwait(false); }
            catch (TimeoutException) { throw new TimeoutException("Spotify sign-in timed out."); }

            var parameters = context.Request.QueryString;
            // The state check is what stops a page the user did not open from
            // handing us a code for an account they did not choose. Anything
            // else - a favicon request, a prefetch, a reload - is answered and
            // ignored rather than failing the attempt.
            if (context.Request.HttpMethod != "GET" || !string.Equals(parameters["state"], state, StringComparison.Ordinal))
            {
                context.Response.StatusCode = (int)HttpStatusCode.NoContent;
                context.Response.Close();
                continue;
            }

            try
            {
                var error = parameters["error"];
                if (!string.IsNullOrWhiteSpace(error))
                    throw new SpotifySignInException(error == "access_denied" ? "Spotify sign-in was cancelled." : $"Spotify sign-in failed ({error}).");
                var code = parameters["code"];
                if (string.IsNullOrWhiteSpace(code)) throw new SpotifySignInException("Spotify did not return an authorization code. Try again.");

                OAuthTokenResponse response;
                try
                {
                    response = await PostTokenAsync(new Dictionary<string, string>
                    {
                        ["grant_type"] = "authorization_code",
                        ["code"] = code,
                        ["redirect_uri"] = RedirectUri,
                        ["client_id"] = clientId,
                        ["code_verifier"] = verifier
                    }, cancellationToken).ConfigureAwait(false);
                }
                catch (SpotifyTokenException rejected) when (rejected.Code == "invalid_client")
                {
                    throw new SpotifySignInException("Spotify didn't accept that Client ID. Check it matches the one on your Spotify app's page.", rejected);
                }
                if (string.IsNullOrWhiteSpace(response.RefreshToken))
                    throw new SpotifySignInException("Spotify did not return a refresh token. Try again.");

                // Checked before anything is saved: an account outside the
                // allow-list gets this far and is refused only now. Saving it
                // first is what used to leave a refused token behind that
                // every later Connect reused.
                var name = await DisplayNameAsync(response.AccessToken, cancellationToken).ConfigureAwait(false);
                var tokens = new SpotifyTokens(response.AccessToken, response.RefreshToken!, DateTimeOffset.UtcNow.AddSeconds(response.ExpiresIn), clientId);
                await RespondAsync(context, BrowserCallbackPage.Success(BrowserCallbackService.Spotify), cancellationToken).ConfigureAwait(false);
                return (tokens, name);
            }
            catch (Exception error) when (error is not OperationCanceledException)
            {
                var message = error switch
                {
                    SpotifyNotApprovedException => "Spotify refused this account. In your Spotify app's settings, open User Management and add the Spotify account you signed in with, then sign in again from ClypDat.",
                    SpotifySignInException => error.Message,
                    _ => "Spotify sign-in could not be completed. Go back to ClypDat and try again.",
                };
                try { await RespondAsync(context, BrowserCallbackPage.Failure(BrowserCallbackService.Spotify, message), cancellationToken).ConfigureAwait(false); }
                catch (Exception pageError) { AppLog.Error("Spotify: showing the sign-in result failed.", pageError); }
                throw;
            }
            finally
            {
                context.Response.Close();
            }
        }
    }

    // The port is fixed, so a press that restarts sign-in can arrive while the
    // attempt it cancelled is still letting go of it. A second is plenty for
    // that; longer means something else really has the port.
    private static async Task<HttpListener> StartListenerAsync(CancellationToken cancellationToken)
    {
        for (var attempt = 0; ; attempt++)
        {
            var listener = new HttpListener();
            listener.Prefixes.Add($"http://127.0.0.1:{RedirectPort}/");
            try
            {
                listener.Start();
                return listener;
            }
            catch (HttpListenerException error)
            {
                ((IDisposable)listener).Dispose();
                if (attempt >= 10)
                    throw new SpotifySignInException(
                        $"Another program is using port {RedirectPort}, which Spotify sign-in needs. Close other ClypDat windows or restart ClypDat, then try again.", error);
            }
            await Task.Delay(100, cancellationToken).ConfigureAwait(false);
        }
    }

    private static async Task RespondAsync(HttpListenerContext context, byte[] page, CancellationToken cancellationToken)
    {
        context.Response.Headers["Cache-Control"] = "no-store";
        context.Response.Headers["Referrer-Policy"] = "no-referrer";
        context.Response.ContentType = "text/html; charset=utf-8";
        context.Response.ContentLength64 = page.Length;
        await context.Response.OutputStream.WriteAsync(page, cancellationToken).ConfigureAwait(false);
    }

    private async Task<OAuthTokenResponse> PostTokenAsync(Dictionary<string, string> form, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, TokenUri) { Content = new FormUrlEncodedContent(form) };
        using var response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
            throw new SpotifyTokenException(response.StatusCode, OAuthErrorCode(body), OAuthError(body));

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
                code = json.RootElement.TryGetProperty("error", out var error) && error.ValueKind == JsonValueKind.String ? error.GetString() : null;
            return string.IsNullOrWhiteSpace(code) ? "no error detail" : code;
        }
        catch (JsonException) { return "no error detail"; }
    }

    private static string? OAuthErrorCode(string body)
    {
        try
        {
            using var json = JsonDocument.Parse(body);
            return json.RootElement.TryGetProperty("error", out var error) && error.ValueKind == JsonValueKind.String ? error.GetString() : null;
        }
        catch (JsonException) { return null; }
    }

    // Web API errors are {"error":{"status":403,"message":"..."}}, unlike the
    // accounts service's flat OAuth errors.
    internal static string? ApiError(string body)
    {
        try
        {
            using var json = JsonDocument.Parse(body);
            if (json.RootElement.TryGetProperty("error", out var error))
            {
                if (error.ValueKind == JsonValueKind.Object && error.TryGetProperty("message", out var message)) return message.GetString();
                if (error.ValueKind == JsonValueKind.String) return error.GetString();
            }
        }
        catch (JsonException) { }
        return string.IsNullOrWhiteSpace(body) ? null : body.Length > 200 ? body[..200] : body;
    }

    public void Dispose()
    {
        CancelSignIn();
        StopPolling();
        _local.Dispose();
        _pollWake.Dispose();
        _http.Dispose();
    }

    // ClientId is the app the session belongs to; tokens saved before own-app
    // sign-in have none and came from ClypDat's retired shared app.
    private sealed record SpotifyTokens(string AccessToken, string RefreshToken, DateTimeOffset ExpiresAt, string? ClientId = null);

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
        [JsonPropertyName("id")] public string? Id { get; set; }
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
