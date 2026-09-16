using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using ClypDat.Core.Settings;

namespace ClypDat.App.Services;

/// <summary>Cloud Xbox activity obtained through the signed-in ClypDat account.</summary>
internal sealed class ClypDatAccountActivityService : IDisposable
{
    private const string BaseUrl = "https://www.clypdat.xyz/";
    private readonly HttpClient _http = new() { BaseAddress = new Uri(BaseUrl), Timeout = TimeSpan.FromSeconds(20) };
    private readonly string _cachePath = Path.Combine(AppDataPaths.Root, "clypdat-account.bin");
    // The watch is front-loaded: linking finishes seconds after the browser
    // opens, and every refresh while an Xbox account is linked costs the server
    // a Microsoft token refresh and a presence call. Two seconds for the first
    // half minute, then a slow tail for the user who takes their time.
    private static readonly TimeSpan LinkWatchWindow = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan LinkWatchEager = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan LinkWatchEagerInterval = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan LinkWatchTailInterval = TimeSpan.FromSeconds(8);
    private static readonly TimeSpan RefreshDebounce = TimeSpan.FromSeconds(5);
    // Returning to the window while nothing needs live Xbox activity. Each
    // refresh wakes the site's database, which then stays up for five minutes
    // - someone flicking between windows all day would keep it awake for free.
    private static readonly TimeSpan IdleRefreshDebounce = TimeSpan.FromMinutes(10);
    // Nothing playing on Xbox: each quiet refresh waits longer than the last,
    // up to three minutes - longer would leave a game just started on Xbox
    // unnamed on Desktop Capture clips for that long. A title appearing, a link, or the need changing puts
    // it straight back to the fast cadence.
    private static readonly TimeSpan[] IdleBackoff =
    {
        TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(2), TimeSpan.FromMinutes(3),
    };
    private static readonly TimeSpan DiscordProfileInterval = TimeSpan.FromMinutes(30);
    private int _idleRefreshes;
    private CancellationTokenSource? _pollCts;
    private readonly SemaphoreSlim _pollWake = new(0, 1);
    private DateTimeOffset _linkWatchUntil = DateTimeOffset.MinValue;
    private DateTimeOffset _linkWatchStarted = DateTimeOffset.MinValue;
    private DateTimeOffset _lastRefresh = DateTimeOffset.MinValue;
    private (bool Xbox, bool Google, bool Discord) _linkWatchSignature;
    private DesktopToken? _token;
    private XboxActivitySnapshot _snapshot = XboxActivitySnapshot.Disconnected;

    public XboxActivitySnapshot Snapshot => _snapshot;

    /// <summary>
    /// Whether anything is using live Xbox activity right now. While false the
    /// poll does not run at all: every refresh costs the site several database
    /// queries, and a poll every minute from any one open app kept that
    /// database awake around the clock - on Neon's free plan, roughly twice the
    /// monthly compute allowance, spent on activity nothing was reading.
    /// Unset means always needed, which is the old behaviour.
    /// </summary>
    public Func<bool>? LiveActivityNeeded { get; set; }

    /// <summary>
    /// Whether Spotify is connected in this app right now, or null while that
    /// is not known yet - the saved Spotify session is still being restored at
    /// startup. Every refresh compares this against what the site believes and
    /// reports the difference, so a connect whose report was dropped (offline,
    /// a 503, the account linked after Spotify) stops being permanent: the
    /// account page catches up on the next poll instead of waiting for the
    /// next connect or disconnect. Null means "say nothing".
    /// </summary>
    public Func<bool?>? SpotifyConnected { get; set; }

    /// <summary>Call when <see cref="LiveActivityNeeded"/> may have changed.</summary>
    public void LiveActivityNeedChanged()
    {
        _idleRefreshes = 0;
        if (IsAuthenticated) WakePoll();
    }

    private bool IsLiveActivityNeeded => LiveActivityNeeded?.Invoke() ?? true;
    private (bool Xbox, bool Google, bool Discord) LinkSignature => (_snapshot.IsConnected, _snapshot.GoogleConnected, _snapshot.DiscordConnected);
    public bool IsAuthenticated => _token is { ExpiresAt: var expiresAt } && expiresAt > DateTimeOffset.UtcNow;
    public event EventHandler<XboxActivitySnapshot>? Changed;

    public async Task<bool> TryRestoreAsync(CancellationToken cancellationToken = default)
    {
        _token = LoadToken();
        if (_token is null || _token.ExpiresAt <= DateTimeOffset.UtcNow) return false;
        try
        {
            await RefreshAsync(cancellationToken).ConfigureAwait(false);
            StartPolling();
            return true;
        }
        catch (Exception error) when (IsServerProblem(error))
        {
            // The site being down, or the network not up yet at Windows
            // startup, is no reason to sign the user out. Keep the token and
            // let the poll retry.
            AppLog.Error("ClypDat account: restore could not reach clypdat.xyz.", error);
            ReportServerProblem();
            StartPolling();
            return true;
        }
        catch
        {
            _token = null;
            TryDeleteCache();
            return false;
        }
    }

    public async Task<bool> ConnectAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            _token = await RunBrowserHandoffAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (BrowserHandoffTimedOutException timedOut)
        {
            // The local listener gave up without ever seeing a matching
            // request - most often because the browser detoured through a
            // sign-in that took longer than the window. The webapp already
            // minted the token server-side when the session was found; claim
            // it instead of making the user notice and click Link again.
            try { _token = await PollClaimAsync(timedOut.State, cancellationToken).ConfigureAwait(false); }
            catch (Exception error) { return Fail(error); }
        }
        catch (Exception error)
        {
            return Fail(error);
        }

        try
        {
            SaveToken(_token);
            await RefreshAsync(cancellationToken).ConfigureAwait(false);
            StartPolling();
            return true;
        }
        catch (Exception error)
        {
            return Fail(error);
        }
    }

    private bool Fail(Exception error)
    {
        AppLog.Error("ClypDat account: connection failed.", error);
        _token = null;
        var serverProblem = IsServerProblem(error);
        var message = serverProblem
            ? ServerProblemMessage
            : error is InvalidOperationException invalid && invalid.Message.StartsWith("ClypDat sign-in required.", StringComparison.Ordinal)
                ? invalid.Message
                : "ClypDat account connection failed. Sign in through the browser and try again.";
        _snapshot = new XboxActivitySnapshot(false, null, null, null, null, message, ServerUnavailable: serverProblem);
        Changed?.Invoke(this, _snapshot);
        return false;
    }

    public void Disconnect()
    {
        _pollCts?.Cancel();
        _pollCts?.Dispose();
        _pollCts = null;
        _token = null;
        TryDeleteCache();
        _snapshot = XboxActivitySnapshot.Disconnected;
        Changed?.Invoke(this, _snapshot);
    }

    public async Task<bool> DisconnectXboxAsync(CancellationToken cancellationToken = default)
    {
        if (!IsAuthenticated) return false;
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Delete, "api/desktop/xbox/activity");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _token!.AccessToken);
            using var response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
            if (response.StatusCode == HttpStatusCode.Unauthorized)
            {
                Disconnect();
                return false;
            }
            if (!response.IsSuccessStatusCode) throw new HttpRequestException($"ClypDat Xbox unlink failed ({(int)response.StatusCode}).", null, response.StatusCode);
            await RefreshAsync(cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch (Exception error)
        {
            AppLog.Error("ClypDat account: Xbox unlink failed.", error);
            if (IsServerProblem(error)) { ReportServerProblem(); return false; }
            _snapshot = _snapshot with { Error = "ClypDat Xbox unlink failed. Try again.", ServerUnavailable = false };
            Changed?.Invoke(this, _snapshot);
            return false;
        }
    }

    /// <summary>
    /// Removes a linked social provider through the ClypDat account. The site
    /// refuses to remove an account's only remaining sign-in method and says so
    /// in the response body, so that message is surfaced rather than replaced
    /// with a generic failure - it is the one the user can act on.
    /// </summary>
    public async Task<bool> UnlinkSocialAsync(string provider, CancellationToken cancellationToken = default)
    {
        if (!IsAuthenticated) return false;
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Delete, $"api/desktop/social?provider={Uri.EscapeDataString(provider)}");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _token!.AccessToken);
            using var response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
            if (response.StatusCode == HttpStatusCode.Unauthorized)
            {
                Disconnect();
                return false;
            }
            var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                throw new HttpRequestException(TryReadError(body) ?? $"ClypDat unlink failed ({(int)response.StatusCode}).", null, response.StatusCode);
            }
            await RefreshAsync(cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch (Exception error)
        {
            AppLog.Error($"ClypDat account: {provider} unlink failed.", error);
            if (IsServerProblem(error)) { ReportServerProblem(); return false; }
            var message = error is HttpRequestException ? error.Message : $"{provider} could not be disconnected. Try again.";
            _snapshot = _snapshot with { Error = message, ServerUnavailable = false };
            Changed?.Invoke(this, _snapshot);
            return false;
        }
    }

    /// <summary>
    /// Tells clypdat.xyz whether Spotify is connected in this app, so the
    /// account page can show it too. Spotify's own OAuth never touches the
    /// server - this is purely a status flag - so a failure here must never
    /// disrupt whatever caused the report (connecting/disconnecting Spotify,
    /// or linking this ClypDat account while Spotify was already connected).
    /// </summary>
    public async Task ReportSpotifyStatusAsync(bool connected, CancellationToken cancellationToken = default)
    {
        if (!IsAuthenticated) return;
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, "api/desktop/spotify")
            {
                Content = new StringContent(JsonSerializer.Serialize(new { connected }), Encoding.UTF8, "application/json"),
            };
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _token!.AccessToken);
            using var response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
            if (response.StatusCode == HttpStatusCode.Unauthorized) Disconnect();
        }
        catch (Exception error)
        {
            AppLog.Error("ClypDat account: reporting Spotify status failed.", error);
        }
    }

    /// <summary>
    /// Called when a provider is about to change outside the app - the user has
    /// just been sent to clypdat.xyz to link something, or has come back to the
    /// window afterwards. Linking finishes in the browser, so nothing tells the
    /// app it happened; on the idle cadence "Connected" could be a minute late.
    /// This refreshes now and then watches at <see cref="LinkWatchInterval"/>
    /// until the set of linked providers actually changes, or the window closes.
    /// </summary>
    public void ExpectLinkChange()
    {
        if (!IsAuthenticated) return;
        _linkWatchSignature = LinkSignature;
        _linkWatchStarted = DateTimeOffset.UtcNow;
        _linkWatchUntil = _linkWatchStarted.Add(LinkWatchWindow);
        _idleRefreshes = 0;
        WakePoll();
    }

    /// <summary>
    /// Refreshes at the next opportunity without opening a watch window. This is
    /// what returning to the window asks for: someone who alt-tabs often would
    /// otherwise keep a fast poll running indefinitely.
    /// </summary>
    public void RefreshSoon()
    {
        if (!IsAuthenticated) return;
        var debounce = IsLiveActivityNeeded ? RefreshDebounce : IdleRefreshDebounce;
        if (DateTimeOffset.UtcNow - _lastRefresh < debounce) return;
        WakePoll();
    }

    private void WakePoll()
    {
        // The loop parks on this semaphore instead of a bare delay, so releasing
        // it cuts whatever wait is in progress short. Capped at one permit: a
        // second release while a refresh is already queued would only make the
        // loop go round again for nothing.
        try { if (_pollWake.CurrentCount == 0) _pollWake.Release(); }
        catch (SemaphoreFullException) { }
        catch (ObjectDisposedException) { }
    }

    private TimeSpan NextPollDelay()
    {
        var now = DateTimeOffset.UtcNow;
        if (now < _linkWatchUntil)
            return now - _linkWatchStarted < LinkWatchEager ? LinkWatchEagerInterval : LinkWatchTailInterval;
        // The site was unreachable: keep trying until it answers, so the notice
        // clears by itself once the outage is over.
        if (_snapshot.ServerUnavailable) return TimeSpan.FromMinutes(1);
        // Nothing to watch for: park until woken (a link, the window coming
        // back, or LiveActivityNeedChanged) instead of refreshing on a timer -
        // except that a Discord account is asked about every 30 minutes, which
        // is when the site rechecks the Discord name and picture. The site
        // answers that from its cache unless something changed.
        if (!IsLiveActivityNeeded) return _snapshot.DiscordConnected ? DiscordProfileInterval : Timeout.InfiniteTimeSpan;
        if (_snapshot.CurrentTitle is not null) return TimeSpan.FromSeconds(15);
        return IdleBackoff[Math.Min(_idleRefreshes, IdleBackoff.Length - 1)];
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
                await _pollWake.WaitAsync(NextPollDelay(), cancellationToken).ConfigureAwait(false);
                await RefreshAsync(cancellationToken).ConfigureAwait(false);
                _idleRefreshes = _snapshot.CurrentTitle is null ? _idleRefreshes + 1 : 0;
                // Whatever the user went to the browser to do, they have done it.
                if (LinkSignature != _linkWatchSignature) _linkWatchUntil = DateTimeOffset.MinValue;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
            catch (Exception error)
            {
                AppLog.Error("ClypDat account: activity refresh failed.", error);
                if (IsServerProblem(error)) ReportServerProblem();
                else
                {
                    _snapshot = _snapshot with { Error = "ClypDat Xbox activity is temporarily unavailable.", ServerUnavailable = false };
                    Changed?.Invoke(this, _snapshot);
                }
                try { await Task.Delay(TimeSpan.FromSeconds(60), cancellationToken).ConfigureAwait(false); }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
            }
        }
    }

    /// <param name="refreshProfile">
    /// Ask the site to re-read the Discord name and picture now rather than at
    /// its next 30-minute check - the Refresh button. The change is saved to the
    /// account, so the website shows it too.
    /// </param>
    public async Task RefreshAsync(CancellationToken cancellationToken = default, bool refreshProfile = false)
    {
        if (_token is null) throw new InvalidOperationException("ClypDat account is not authenticated.");
        using var request = new HttpRequestMessage(HttpMethod.Get, refreshProfile ? "api/desktop/xbox/activity?profile=refresh" : "api/desktop/xbox/activity");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _token.AccessToken);
        using var response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.Unauthorized)
        {
            _token = null;
            TryDeleteCache();
            throw new InvalidOperationException("ClypDat account sign-in expired.");
        }
        var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            var message = TryReadError(body) ?? $"ClypDat activity endpoint rejected the request ({(int)response.StatusCode}).";
            throw new HttpRequestException(message, null, response.StatusCode);
        }
        _lastRefresh = DateTimeOffset.UtcNow;
        var result = JsonSerializer.Deserialize<ActivityResponse>(body) ?? throw new InvalidOperationException("ClypDat activity returned no data.");
        var activity = result.Activity;
        var providers = result.Providers ?? Array.Empty<string>();
        _snapshot = new XboxActivitySnapshot(result.Connected, null, activity?.Title, activity?.ConsoleName, activity is null ? DateTimeOffset.UtcNow : ParseTimestamp(activity.UpdatedAt), null,
            providers.Contains("google", StringComparer.OrdinalIgnoreCase), providers.Contains("discord", StringComparer.OrdinalIgnoreCase),
            ProfileName: result.Profile?.Name, ProfileImage: result.Profile?.Image);
        ReconcileSpotifyStatus(result.Spotify, cancellationToken);
        Changed?.Invoke(this, _snapshot);
    }

    // Older builds of the site do not send the flag at all; nothing to compare
    // against, so nothing to do. Re-sending is rate limited because a report
    // that keeps failing would otherwise post on every poll for as long as the
    // app is open.
    private static readonly TimeSpan SpotifyResendInterval = TimeSpan.FromMinutes(5);
    private DateTimeOffset _lastSpotifyReport = DateTimeOffset.MinValue;

    private void ReconcileSpotifyStatus(bool? reported, CancellationToken cancellationToken)
    {
        if (reported is not { } onSite) return;
        if (SpotifyConnected?.Invoke() is not { } inApp || inApp == onSite) return;
        if (DateTimeOffset.UtcNow - _lastSpotifyReport < SpotifyResendInterval) return;
        _lastSpotifyReport = DateTimeOffset.UtcNow;
        AppLog.Info($"ClypDat account: Spotify reads {inApp} here and {onSite} on clypdat.xyz; reporting again.");
        _ = ReportSpotifyStatusAsync(inApp, cancellationToken);
    }

    // Was 5 minutes. A round trip that detours through signing in (Discord's
    // consent screen, 2FA) can take longer, and the loop below now absorbs a
    // stray request instead of failing on one - so the window can afford to
    // be generous; PollClaimAsync below is the real backstop past this anyway.
    private static readonly TimeSpan HandoffWindow = TimeSpan.FromMinutes(10);

    private async Task<DesktopToken> RunBrowserHandoffAsync(CancellationToken cancellationToken)
    {
        var port = GetFreePort();
        var redirectUri = $"http://127.0.0.1:{port}/callback/";
        var state = Base64Url(RandomNumberGenerator.GetBytes(24));
        using var listener = new HttpListener();
        listener.Prefixes.Add(redirectUri);
        listener.Start();
        var url = $"{BaseUrl}api/desktop/connect?redirect_uri={Uri.EscapeDataString(redirectUri)}&state={Uri.EscapeDataString(state)}";
        Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });

        var deadline = DateTimeOffset.UtcNow.Add(HandoffWindow);
        // A single GetContextAsync used to be enough, but a request that does
        // not carry this attempt's state - a duplicated redirect, a reload, a
        // stray probe - must not fail the whole attempt. Answer it and keep
        // waiting for the one that matches, until the window runs out.
        while (true)
        {
            var remaining = deadline - DateTimeOffset.UtcNow;
            if (remaining <= TimeSpan.Zero) throw new BrowserHandoffTimedOutException(state);

            HttpListenerContext context;
            try { context = await listener.GetContextAsync().WaitAsync(remaining, cancellationToken).ConfigureAwait(false); }
            catch (TimeoutException) { throw new BrowserHandoffTimedOutException(state); }

            if (!string.Equals(context.Request.QueryString["state"], state, StringComparison.Ordinal))
            {
                context.Response.StatusCode = (int)HttpStatusCode.NoContent;
                context.Response.Close();
                continue;
            }

            try
            {
                var error = context.Request.QueryString["error"];
                if (string.Equals(error, "login-required", StringComparison.Ordinal))
                    throw new InvalidOperationException("ClypDat sign-in required. Open clypdat.xyz/account, sign in, then retry here.");
                if (!string.IsNullOrWhiteSpace(error)) throw new InvalidOperationException("ClypDat sign-in was not completed.");
                var accessToken = context.Request.QueryString["token"];
                if (string.IsNullOrWhiteSpace(accessToken)) throw new InvalidOperationException("ClypDat sign-in returned no token.");
                var expiresIn = int.TryParse(context.Request.QueryString["expires_in"], out var seconds) ? seconds : 60 * 60 * 24 * 30;
                var body = BrowserCallbackPage.Success(BrowserCallbackService.ClypDat);
                context.Response.ContentType = "text/html; charset=utf-8";
                context.Response.ContentLength64 = body.Length;
                await context.Response.OutputStream.WriteAsync(body, cancellationToken).ConfigureAwait(false);
                return new DesktopToken(accessToken, DateTimeOffset.UtcNow.AddSeconds(Math.Max(60, expiresIn)));
            }
            finally
            {
                context.Response.Close();
            }
        }
    }

    private static readonly TimeSpan ClaimPollWindow = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan ClaimPollInterval = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Falls back to asking the server directly for the token it minted for
    /// this attempt's <paramref name="state"/>, for when the local listener's
    /// window ran out before the browser ever made it back to localhost -
    /// /api/desktop/connect stashes the token there the moment a session is
    /// found, specifically so a slow or interrupted round trip is not lost.
    /// </summary>
    private async Task<DesktopToken> PollClaimAsync(string state, CancellationToken cancellationToken)
    {
        var deadline = DateTimeOffset.UtcNow.Add(ClaimPollWindow);
        while (DateTimeOffset.UtcNow < deadline)
        {
            await Task.Delay(ClaimPollInterval, cancellationToken).ConfigureAwait(false);
            using var request = new HttpRequestMessage(HttpMethod.Get, $"api/desktop/connect/claim?state={Uri.EscapeDataString(state)}");
            using var response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
            if (response.StatusCode == HttpStatusCode.NotFound) continue;
            if (!response.IsSuccessStatusCode) throw new HttpRequestException($"ClypDat connect claim rejected the request ({(int)response.StatusCode}).", null, response.StatusCode);
            var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            var claim = JsonSerializer.Deserialize<ClaimResponse>(body) ?? throw new InvalidOperationException("ClypDat connect claim returned no data.");
            return new DesktopToken(claim.Token, DateTimeOffset.UtcNow.AddSeconds(Math.Max(60, claim.ExpiresIn)));
        }
        throw new InvalidOperationException("ClypDat sign-in was not completed in time. Sign in through the browser and try again.");
    }

    private sealed class BrowserHandoffTimedOutException(string state) : Exception
    {
        public string State { get; } = state;
    }

    private sealed class ClaimResponse
    {
        [JsonPropertyName("token")] public string Token { get; set; } = string.Empty;
        [JsonPropertyName("expires_in")] public int ExpiresIn { get; set; }
    }

    private static int GetFreePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }

    private DesktopToken? LoadToken()
    {
        try
        {
            if (!File.Exists(_cachePath)) return null;
            var protectedBytes = File.ReadAllBytes(_cachePath);
            var bytes = ProtectedData.Unprotect(protectedBytes, null, DataProtectionScope.CurrentUser);
            return JsonSerializer.Deserialize<DesktopToken>(bytes);
        }
        catch { return null; }
    }

    private void SaveToken(DesktopToken token)
    {
        Directory.CreateDirectory(AppDataPaths.Root);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(token);
        File.WriteAllBytes(_cachePath, ProtectedData.Protect(bytes, null, DataProtectionScope.CurrentUser));
    }

    private const string ServerProblemMessage = "Couldn't reach clypdat.xyz. Check your connection, or the status page for an outage.";

    /// <summary>
    /// The request never got an answer (no network, DNS, a timeout) or the site
    /// answered with a server error. Rejections the site means - 4xx, a refused
    /// unlink - are not this: they carry a message the user can act on.
    /// </summary>
    private static bool IsServerProblem(Exception error) => error switch
    {
        HttpRequestException { StatusCode: null } => true,
        HttpRequestException { StatusCode: var status } => (int)status >= 500,
        TaskCanceledException { InnerException: TimeoutException } => true,
        _ => false,
    };

    // Keeps whatever the last good refresh knew about linked providers, so an
    // outage does not make every row look unlinked.
    private void ReportServerProblem()
    {
        _snapshot = _snapshot with { Error = ServerProblemMessage, ServerUnavailable = true };
        Changed?.Invoke(this, _snapshot);
    }

    private void TryDeleteCache() { try { if (File.Exists(_cachePath)) File.Delete(_cachePath); } catch { } }
    private static string? TryReadError(string body)
    {
        try
        {
            using var json = JsonDocument.Parse(body);
            return json.RootElement.TryGetProperty("error", out var error) ? error.GetString() : null;
        }
        catch (JsonException) { return null; }
    }
    private static DateTimeOffset ParseTimestamp(string? value) => DateTimeOffset.TryParse(value, out var parsed) ? parsed : DateTimeOffset.UtcNow;
    private static string Base64Url(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    public void Dispose() { _pollCts?.Cancel(); _pollCts?.Dispose(); _pollWake.Dispose(); _http.Dispose(); }

    private sealed record DesktopToken(string AccessToken, DateTimeOffset ExpiresAt);
    private sealed class ActivityResponse
    {
        [JsonPropertyName("connected")] public bool Connected { get; set; }
        [JsonPropertyName("activity")] public Activity? Activity { get; set; }
        [JsonPropertyName("providers")] public string[]? Providers { get; set; }
        // Present only when the account has Discord linked.
        [JsonPropertyName("profile")] public Profile? Profile { get; set; }
        [JsonPropertyName("spotify")] public bool? Spotify { get; set; }
    }
    private sealed class Profile
    {
        [JsonPropertyName("name")] public string? Name { get; set; }
        [JsonPropertyName("image")] public string? Image { get; set; }
    }
    private sealed class Activity
    {
        [JsonPropertyName("title")] public string? Title { get; set; }
        [JsonPropertyName("consoleName")] public string? ConsoleName { get; set; }
        [JsonPropertyName("updatedAt")] public string? UpdatedAt { get; set; }
    }
}
