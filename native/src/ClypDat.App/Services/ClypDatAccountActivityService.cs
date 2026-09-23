using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Net.Http.Headers;
using System.Net.Http.Json;
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
    private readonly HttpClient _http;
    private readonly string _cachePath;
    // Desktop tokens signed out on this PC whose server-side revocation has not
    // gone through yet. See SignOutAsync.
    private readonly string _revokePath;
    private readonly CancellationTokenSource _lifetime = new();

    public ClypDatAccountActivityService() : this(
        new HttpClient { BaseAddress = new Uri(BaseUrl), Timeout = TimeSpan.FromSeconds(20) },
        Path.Combine(AppDataPaths.Root, "clypdat-account.bin")) { }

    internal ClypDatAccountActivityService(HttpClient http, string cachePath)
    {
        _http = http;
        _cachePath = cachePath;
        _revokePath = cachePath + ".revoke";
    }

    public string? ConnectionCode { get; private set; }
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
    // Nothing at all to watch. Still asks now and then, so a sign-in that is
    // about to run out gets renewed (MaybeRenewAsync) in an app left open for
    // days. The site answers these from its cache.
    private static readonly TimeSpan ParkedInterval = TimeSpan.FromHours(6);
    private int _idleRefreshes;
    private CancellationTokenSource? _pollCts;
    private CancellationTokenSource? _connectCts;
    private readonly object _connectGate = new();
    private readonly SemaphoreSlim _pollWake = new(0, 1);
    private DateTimeOffset _linkWatchUntil = DateTimeOffset.MinValue;
    private DateTimeOffset _linkWatchStarted = DateTimeOffset.MinValue;
    private DateTimeOffset _lastRefresh = DateTimeOffset.MinValue;
    private (bool Xbox, bool Google, bool Discord) _linkWatchSignature;
    private DesktopToken? _token;
    private XboxActivitySnapshot _snapshot = XboxActivitySnapshot.Disconnected;
    private bool _policyPaused;

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

    /// <summary>Call when <see cref="LiveActivityNeeded"/> may have changed.</summary>
    public void LiveActivityNeedChanged()
    {
        if (_policyPaused) return;
        _idleRefreshes = 0;
        if (IsAuthenticated) WakePoll();
    }

    private bool IsLiveActivityNeeded => LiveActivityNeeded?.Invoke() ?? true;
    private (bool Xbox, bool Google, bool Discord) LinkSignature => (_snapshot.IsConnected, _snapshot.GoogleConnected, _snapshot.DiscordConnected);
    public bool IsAuthenticated => _token is { ExpiresAt: var expiresAt } && expiresAt > DateTimeOffset.UtcNow;
    internal async Task<string> GetSupportTokenAsync(CancellationToken cancellationToken)
    {
        await MaybeRenewAsync(cancellationToken).ConfigureAwait(false);
        return IsAuthenticated && _token is { } token
            ? token.AccessToken
            : throw new InvalidOperationException("Link your ClypDat account before sending diagnostics.");
    }
    public bool IsConnecting => _connectCts is not null;
    public event EventHandler<XboxActivitySnapshot>? Changed;

    public async Task<bool> TryRestoreAsync(CancellationToken cancellationToken = default)
    {
        // A sign-out that could not reach the site last time gets another go.
        _ = FlushPendingRevokesAsync(_lifetime.Token);
        if (NoticeBoardService.IsBlocked("pause-xbox-activity")) { SetPolicyPaused(true); return false; }
        _token = LoadToken();
        if (_token is null) return false;
        if (_token.ExpiresAt <= DateTimeOffset.UtcNow)
        {
            _token = null;
            TryDeleteCache();
            SetSignedOutSnapshot(SignInExpiredMessage);
            return false;
        }
        try
        {
            await RefreshAsync(cancellationToken).ConfigureAwait(false);
            StartPolling();
            return true;
        }
        catch (ClypDatSignedOutException)
        {
            return false;
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
        catch (Exception error) when (!cancellationToken.IsCancellationRequested)
        {
            // Only the site saying the token is no good (401, above) signs the
            // app out. Anything else - an unreadable response, a 4xx from a
            // half-deployed site - used to delete a perfectly good sign-in.
            AppLog.Error("ClypDat account: first refresh after restore failed; polling will retry.", error);
            StartPolling();
            return true;
        }
    }

    /// <summary>
    /// Signs in through the browser: clypdat.xyz asks the person to sign in
    /// there if they are not already, then to confirm the pairing code.
    /// Pressing it again, or <see cref="CancelConnect"/>, abandons the wait.
    /// </summary>
    public async Task<bool> ConnectAsync(CancellationToken cancellationToken = default)
    {
        if (NoticeBoardService.IsBlocked("pause-xbox-activity")) { SetPolicyPaused(true); return false; }
        CancelConnect();
        using var attempt = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetime.Token);
        lock (_connectGate) _connectCts = attempt;
        DesktopToken token;
        try
        {
            if (_snapshot.Error is not null) _snapshot = _snapshot with { Error = null, ServerUnavailable = false };
            token = await RunBrowserHandoffAsync(attempt.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (attempt.IsCancellationRequested)
        {
            AppLog.Info("ClypDat account: sign-in cancelled.");
            return false;
        }
        catch (Exception error)
        {
            return Fail(error);
        }
        finally
        {
            lock (_connectGate) if (ReferenceEquals(_connectCts, attempt)) _connectCts = null;
            ConnectionCode = null;
            Changed?.Invoke(this, _snapshot);
        }

        try
        {
            _token = token;
            SaveToken(token);
            await RefreshAsync(cancellationToken).ConfigureAwait(false);
            StartPolling();
            return true;
        }
        catch (Exception error) when (IsServerProblem(error))
        {
            // Linked; the site just did not answer the first refresh. Keep the
            // new sign-in rather than making the person do it all again.
            AppLog.Error("ClypDat account: first refresh after sign-in could not reach clypdat.xyz.", error);
            ReportServerProblem();
            StartPolling();
            return true;
        }
        catch (Exception error)
        {
            return Fail(error);
        }
    }

    /// <summary>Stops waiting for the browser. The loopback listener closes with it.</summary>
    public void CancelConnect()
    {
        CancellationTokenSource? pending;
        lock (_connectGate) { pending = _connectCts; _connectCts = null; }
        try { pending?.Cancel(); } catch (ObjectDisposedException) { }
    }

    private bool Fail(Exception error)
    {
        AppLog.Error("ClypDat account: connection failed.", error);
        _token = null;
        var serverProblem = IsServerProblem(error);
        var message = serverProblem
            ? ServerProblemMessage
            : error switch
            {
                ClypDatSignInException => error.Message,
                TimeoutException => "ClypDat sign-in timed out. Press Sign in to try again.",
                _ => "ClypDat sign-in didn't finish. Press Sign in to try again.",
            };
        _snapshot = new XboxActivitySnapshot(false, null, null, null, null, message, ServerUnavailable: serverProblem);
        Changed?.Invoke(this, _snapshot);
        return false;
    }

    public void Disconnect()
    {
        StopPolling();
        _token = null;
        TryDeleteCache();
        _snapshot = XboxActivitySnapshot.Disconnected;
        Changed?.Invoke(this, _snapshot);
    }

    private void StopPolling()
    {
        _pollCts?.Cancel();
        _pollCts?.Dispose();
        _pollCts = null;
    }

    public void SetPolicyPaused(bool paused)
    {
        if (_policyPaused == paused) return;
        _policyPaused = paused;
        if (paused)
        {
            StopPolling();
            _snapshot = XboxActivitySnapshot.Disconnected with { Error = "Xbox activity is temporarily paused by ClypDat." };
            Changed?.Invoke(this, _snapshot);
            AppLog.Info("Policy transition: account-backed Xbox activity paused.");
            return;
        }
        // Reconnecting (TryRestoreAsync) is MainWindowViewModel.ApplyRemotePolicy's
        // job, so a switch that was active at launch - before any token was
        // loaded - still comes back.
        AppLog.Info("Policy transition: account-backed Xbox activity resumed.");
    }

    /// <summary>
    /// Signs this PC out. The local sign-in is gone before the first await, so
    /// the app reads as signed out at once whatever the network does. Telling
    /// the site the token is dead is queued: the token is written to a
    /// DPAPI-protected list first, then sent to api/desktop/revoke, and stays
    /// listed - retried in the background and at every launch - until the site
    /// confirms. Sign-out used to wait on that request and refuse to sign out
    /// at all when it failed, which a site outage turned into a Sign out button
    /// that did nothing.
    /// </summary>
    /// <returns>Whether the site has confirmed the sign-out.</returns>
    public async Task<bool> SignOutAsync(CancellationToken cancellationToken = default)
    {
        CancelConnect();
        var token = _token;
        if (token is not null)
        {
            try { QueueRevoke(token); }
            catch (Exception error) { AppLog.Error("ClypDat account: queueing the server sign-out failed.", error); }
        }
        Disconnect();
        return await FlushPendingRevokesAsync(cancellationToken).ConfigureAwait(false);
    }

    private readonly SemaphoreSlim _revokeGate = new(1, 1);
    private static readonly TimeSpan[] RevokeRetryBackoff =
    {
        TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(15), TimeSpan.FromHours(1),
    };
    private int _revokeRetries;
    private int _revokeRetryScheduled;

    /// <summary>Sends every queued sign-out. True when none are left.</summary>
    internal async Task<bool> FlushPendingRevokesAsync(CancellationToken cancellationToken = default)
    {
        try { await _revokeGate.WaitAsync(cancellationToken).ConfigureAwait(false); }
        catch (Exception error) when (error is OperationCanceledException or ObjectDisposedException) { return false; }
        try
        {
            var pending = LoadPendingRevokes();
            if (pending.Count == 0) return true;
            var remaining = new List<DesktopToken>();
            foreach (var token in pending)
            {
                // Expired on its own: nothing left to take back.
                if (token.ExpiresAt <= DateTimeOffset.UtcNow) continue;
                try
                {
                    using var request = new HttpRequestMessage(HttpMethod.Post, "api/desktop/revoke");
                    request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token.AccessToken);
                    using var response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
                    // 401: already expired or signed out from the site, which is
                    // the outcome wanted.
                    if (response.IsSuccessStatusCode || response.StatusCode == HttpStatusCode.Unauthorized) continue;
                    AppLog.Info($"ClypDat account: server sign-out returned {(int)response.StatusCode}; will retry.");
                    remaining.Add(token);
                }
                catch (Exception error) when (!cancellationToken.IsCancellationRequested)
                {
                    AppLog.Error("ClypDat account: server sign-out failed; will retry.", error);
                    remaining.Add(token);
                }
                catch (OperationCanceledException)
                {
                    remaining.Add(token);
                }
            }
            SavePendingRevokes(remaining);
            if (remaining.Count == 0)
            {
                _revokeRetries = 0;
                AppLog.Info("ClypDat account: server sign-out confirmed.");
                return true;
            }
            ScheduleRevokeRetry();
            return false;
        }
        catch (Exception error)
        {
            AppLog.Error("ClypDat account: sending queued sign-outs failed.", error);
            return false;
        }
        finally
        {
            try { _revokeGate.Release(); } catch (ObjectDisposedException) { }
        }
    }

    private void ScheduleRevokeRetry()
    {
        if (Interlocked.Exchange(ref _revokeRetryScheduled, 1) == 1) return;
        var delay = RevokeRetryBackoff[Math.Min(_revokeRetries++, RevokeRetryBackoff.Length - 1)];
        _ = Task.Run(async () =>
        {
            try { await Task.Delay(delay, _lifetime.Token).ConfigureAwait(false); }
            catch (OperationCanceledException) { return; }
            finally { Interlocked.Exchange(ref _revokeRetryScheduled, 0); }
            await FlushPendingRevokesAsync(_lifetime.Token).ConfigureAwait(false);
        });
    }

    private void QueueRevoke(DesktopToken token)
    {
        var pending = LoadPendingRevokes();
        if (pending.All(item => item.AccessToken != token.AccessToken)) pending.Add(token);
        SavePendingRevokes(pending);
    }

    private List<DesktopToken> LoadPendingRevokes()
    {
        try
        {
            if (!File.Exists(_revokePath)) return new();
            var bytes = ProtectedData.Unprotect(File.ReadAllBytes(_revokePath), null, DataProtectionScope.CurrentUser);
            return JsonSerializer.Deserialize<List<DesktopToken>>(bytes)?.Where(item => !string.IsNullOrWhiteSpace(item.AccessToken)).ToList() ?? new();
        }
        catch { return new(); }
    }

    private void SavePendingRevokes(List<DesktopToken> pending)
    {
        var live = pending.Where(item => item.ExpiresAt > DateTimeOffset.UtcNow).ToList();
        if (live.Count == 0)
        {
            try { if (File.Exists(_revokePath)) File.Delete(_revokePath); } catch { }
            return;
        }
        WriteProtected(_revokePath, live);
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
                SignedOutBySite();
                return false;
            }
            if (!response.IsSuccessStatusCode) throw new HttpRequestException($"ClypDat Xbox unlink failed ({(int)response.StatusCode}).", null, response.StatusCode);
            await RefreshAsync(cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch (ClypDatSignedOutException) { return false; }
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
                SignedOutBySite();
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
        catch (ClypDatSignedOutException) { return false; }
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
        if (!IsLiveActivityNeeded)
        {
            // Nothing live to watch. A Discord account is asked about every 30
            // minutes, which is when the site rechecks the Discord name and
            // picture. The site answers these from its cache.
            return _snapshot.DiscordConnected ? DiscordProfileInterval : ParkedInterval;
        }
        if (_snapshot.CurrentTitle is not null) return TimeSpan.FromSeconds(15);
        return IdleBackoff[Math.Min(_idleRefreshes, IdleBackoff.Length - 1)];
    }

    private void StartPolling()
    {
        if (_policyPaused || NoticeBoardService.IsBlocked("pause-xbox-activity")) return;
        _pollCts?.Cancel();
        _pollCts = new CancellationTokenSource();
        _ = PollAsync(_pollCts.Token);
    }

    private async Task PollAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            if (_policyPaused || NoticeBoardService.IsBlocked("pause-xbox-activity")) return;
            try
            {
                await _pollWake.WaitAsync(NextPollDelay(), cancellationToken).ConfigureAwait(false);
                await RefreshAsync(cancellationToken).ConfigureAwait(false);
                _idleRefreshes = _snapshot.CurrentTitle is null ? _idleRefreshes + 1 : 0;
                // Whatever the user went to the browser to do, they have done it.
                if (LinkSignature != _linkWatchSignature) _linkWatchUntil = DateTimeOffset.MinValue;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
            // Signed out from the site, or the sign-in expired: nothing left to
            // poll with. This used to loop on a null token every minute while
            // the card said the service was "temporarily unavailable".
            catch (ClypDatSignedOutException) { return; }
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
        if (_policyPaused || NoticeBoardService.IsBlocked("pause-xbox-activity")) return;
        var token = _token ?? throw new ClypDatSignedOutException();
        using var request = new HttpRequestMessage(HttpMethod.Get, refreshProfile ? "api/desktop/xbox/activity?profile=refresh" : "api/desktop/xbox/activity");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token.AccessToken);
        using var response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.Unauthorized)
        {
            SignedOutBySite();
            throw new ClypDatSignedOutException();
        }
        var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            var message = TryReadError(body) ?? $"ClypDat activity endpoint rejected the request ({(int)response.StatusCode}).";
            throw new HttpRequestException(message, null, response.StatusCode);
        }
        _lastRefresh = DateTimeOffset.UtcNow;
        var result = JsonSerializer.Deserialize<ActivityResponse>(body) ?? throw new InvalidOperationException("ClypDat activity returned no data.");
        if (_policyPaused || NoticeBoardService.IsBlocked("pause-xbox-activity")) return;
        // Signed out (or in again) while this was in flight.
        if (!ReferenceEquals(_token, token)) return;
        var activity = result.Activity;
        var providers = result.Providers ?? Array.Empty<string>();
        _snapshot = new XboxActivitySnapshot(result.Connected, null, activity?.Title, activity?.ConsoleName, activity is null ? DateTimeOffset.UtcNow : ParseTimestamp(activity.UpdatedAt), null,
            providers.Contains("google", StringComparer.OrdinalIgnoreCase), providers.Contains("discord", StringComparer.OrdinalIgnoreCase),
            ProfileName: result.Profile?.Name, ProfileImage: result.Profile?.Image);
        Changed?.Invoke(this, _snapshot);
        await MaybeRenewAsync(cancellationToken).ConfigureAwait(false);
    }

    // Desktop sign-ins last 30 days. In the last week of one, each refresh
    // swaps it for a fresh one (api/desktop/token/renew), so an app in daily
    // use stays signed in instead of dropping out a month after linking.
    private static readonly TimeSpan RenewWithin = TimeSpan.FromDays(7);
    private static readonly TimeSpan RenewRetry = TimeSpan.FromHours(1);
    private DateTimeOffset _lastRenewAttempt = DateTimeOffset.MinValue;

    private async Task MaybeRenewAsync(CancellationToken cancellationToken)
    {
        var token = _token;
        var now = DateTimeOffset.UtcNow;
        if (token is null || token.ExpiresAt - now > RenewWithin || now - _lastRenewAttempt < RenewRetry) return;
        _lastRenewAttempt = now;
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, "api/desktop/token/renew");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token.AccessToken);
            using var response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
            // 404 is a site from before renewal existed; the token simply runs
            // its course. Nothing here signs anyone out: the next refresh is
            // what notices a token the site no longer accepts.
            if (!response.IsSuccessStatusCode)
            {
                AppLog.Info($"ClypDat account: sign-in renewal returned {(int)response.StatusCode}.");
                return;
            }
            var renewed = ReadToken(await response.Content.ReadFromJsonAsync<TokenResponse>(cancellationToken).ConfigureAwait(false));
            if (!ReferenceEquals(_token, token)) return;
            _token = renewed;
            SaveToken(renewed);
            AppLog.Info("ClypDat account: sign-in renewed.");
        }
        catch (Exception error) when (!cancellationToken.IsCancellationRequested)
        {
            AppLog.Error("ClypDat account: sign-in renewal failed; will retry.", error);
        }
    }

    // Keep the loopback listener open through sign-in and the consent click.
    private static readonly TimeSpan HandoffWindow = TimeSpan.FromMinutes(10);

    internal static string PairingCode(string state)
    {
        const string alphabet = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789";
        var digest = SHA256.HashData(Encoding.UTF8.GetBytes($"clypdat-desktop-connect:{state}"));
        return string.Concat(digest.Take(3).Select(value => alphabet[value % alphabet.Length])) + "-"
            + string.Concat(digest.Skip(3).Take(3).Select(value => alphabet[value % alphabet.Length]));
    }

    internal static string CodeChallenge(string verifier) => Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));

    private async Task<DesktopToken> RunBrowserHandoffAsync(CancellationToken cancellationToken)
    {
        var port = GetFreePort();
        var redirectUri = $"http://127.0.0.1:{port}/callback/";
        var state = Base64Url(RandomNumberGenerator.GetBytes(24));
        var verifier = Base64Url(RandomNumberGenerator.GetBytes(32));
        using var listener = new HttpListener();
        listener.Prefixes.Add(redirectUri);
        listener.Start();
        ConnectionCode = PairingCode(state);
        Changed?.Invoke(this, _snapshot);
        var url = $"{BaseUrl}api/desktop/connect?redirect_uri={Uri.EscapeDataString(redirectUri)}&state={Uri.EscapeDataString(state)}&code_challenge={CodeChallenge(verifier)}&code_challenge_method=S256";
        Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });

        var deadline = DateTimeOffset.UtcNow.Add(HandoffWindow);
        // A single GetContextAsync used to be enough, but a request that does
        // not carry this attempt's state - a duplicated redirect, a reload, a
        // stray probe - must not fail the whole attempt. Answer it and keep
        // waiting for the one that matches, until the window runs out.
        while (true)
        {
            var remaining = deadline - DateTimeOffset.UtcNow;
            if (remaining <= TimeSpan.Zero) throw new TimeoutException("ClypDat sign-in timed out.");

            HttpListenerContext context;
            try { context = await listener.GetContextAsync().WaitAsync(remaining, cancellationToken).ConfigureAwait(false); }
            catch (TimeoutException) { throw new TimeoutException("ClypDat sign-in timed out."); }

            if (context.Request.HttpMethod != "GET" || !string.Equals(context.Request.QueryString["state"], state, StringComparison.Ordinal))
            {
                context.Response.StatusCode = (int)HttpStatusCode.NoContent;
                context.Response.Close();
                continue;
            }

            try
            {
                var error = context.Request.QueryString["error"];
                if (string.Equals(error, "access_denied", StringComparison.Ordinal))
                    throw new ClypDatSignInException("Linking was cancelled in the browser.");
                if (string.Equals(error, "login-required", StringComparison.Ordinal))
                    throw new ClypDatSignInException("Sign in on clypdat.xyz, then press Sign in here again.");
                if (!string.IsNullOrWhiteSpace(error)) throw new ClypDatSignInException("ClypDat sign-in was not completed. Press Sign in to try again.");
                var code = context.Request.QueryString["code"];
                if (string.IsNullOrWhiteSpace(code)) throw new ClypDatSignInException("ClypDat sign-in returned no link code. Press Sign in to try again.");
                var token = await ExchangeCodeAsync(code, verifier, cancellationToken).ConfigureAwait(false);
                await RespondAsync(context, BrowserCallbackPage.Success(BrowserCallbackService.ClypDat), cancellationToken).ConfigureAwait(false);
                return token;
            }
            catch (Exception error) when (error is not OperationCanceledException)
            {
                var message = error is ClypDatSignInException ? error.Message
                    : IsServerProblem(error) ? "clypdat.xyz didn't answer. Go back to ClypDat and try again."
                    : "ClypDat sign-in could not be completed. Go back to ClypDat and try again.";
                try { await RespondAsync(context, BrowserCallbackPage.Failure(BrowserCallbackService.ClypDat, message), cancellationToken).ConfigureAwait(false); }
                catch (Exception pageError) { AppLog.Error("ClypDat account: showing the sign-in result failed.", pageError); }
                throw;
            }
            finally
            {
                context.Response.Close();
            }
        }
    }

    private static async Task RespondAsync(HttpListenerContext context, byte[] body, CancellationToken cancellationToken)
    {
        context.Response.Headers["Cache-Control"] = "no-store";
        context.Response.Headers["Referrer-Policy"] = "no-referrer";
        context.Response.ContentType = "text/html; charset=utf-8";
        context.Response.ContentLength64 = body.Length;
        await context.Response.OutputStream.WriteAsync(body, cancellationToken).ConfigureAwait(false);
    }

    private async Task<DesktopToken> ExchangeCodeAsync(string code, string verifier, CancellationToken cancellationToken)
    {
        using var response = await _http.PostAsJsonAsync("api/desktop/token", new { code, code_verifier = verifier }, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            if ((int)response.StatusCode >= 500) throw new HttpRequestException($"ClypDat sign-in failed ({(int)response.StatusCode}).", null, response.StatusCode);
            // The site words these for the user ("The link code is invalid or
            // has expired. Press Link again.").
            throw new ClypDatSignInException(TryReadError(body) ?? $"ClypDat sign-in was refused ({(int)response.StatusCode}). Press Sign in to try again.");
        }
        return ReadToken(await response.Content.ReadFromJsonAsync<TokenResponse>(cancellationToken).ConfigureAwait(false));
    }

    private static DesktopToken ReadToken(TokenResponse? token)
    {
        if (token is null || string.IsNullOrWhiteSpace(token.Token) || token.ExpiresIn <= 0 || token.ExpiresIn > 60 * 60 * 24 * 30)
            throw new ClypDatSignInException("ClypDat sign-in returned an invalid token. Press Sign in to try again.");
        return new DesktopToken(token.Token, DateTimeOffset.UtcNow.AddSeconds(token.ExpiresIn));
    }

    private sealed class TokenResponse
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

    private void SaveToken(DesktopToken token) => WriteProtected(_cachePath, token);

    private static void WriteProtected<T>(string path, T value)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = path + ".tmp-" + Guid.NewGuid().ToString("N");
        var bytes = JsonSerializer.SerializeToUtf8Bytes(value);
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

    private const string ServerProblemMessage = "Couldn't reach clypdat.xyz. Check your connection, or the status page for an outage.";
    private const string SignedOutMessage = "This PC was signed out of ClypDat - from your account page, or because the sign-in ended. Sign in again to reconnect.";
    private const string SignInExpiredMessage = "Your ClypDat sign-in on this PC expired. Sign in again to reconnect.";

    // The site answered 401: this token is revoked, expired, or its account is
    // gone. Clean signed-out state, with a line saying why.
    private void SignedOutBySite()
    {
        AppLog.Info("ClypDat account: the site no longer accepts this PC's sign-in; signed out.");
        StopPolling();
        _token = null;
        TryDeleteCache();
        SetSignedOutSnapshot(SignedOutMessage);
    }

    private void SetSignedOutSnapshot(string message)
    {
        _snapshot = XboxActivitySnapshot.Disconnected with { Error = message };
        Changed?.Invoke(this, _snapshot);
    }

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
            return json.RootElement.TryGetProperty("error", out var error) && error.ValueKind == JsonValueKind.String ? error.GetString() : null;
        }
        catch (JsonException) { return null; }
    }
    private static DateTimeOffset ParseTimestamp(string? value) => DateTimeOffset.TryParse(value, out var parsed) ? parsed : DateTimeOffset.UtcNow;
    private static string Base64Url(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    public void Dispose() { _lifetime.Cancel(); _lifetime.Dispose(); CancelConnect(); _pollCts?.Cancel(); _pollCts?.Dispose(); _pollWake.Dispose(); _revokeGate.Dispose(); _http.Dispose(); }

    private sealed record DesktopToken(string AccessToken, DateTimeOffset ExpiresAt);

    /// <summary>The site no longer accepts this PC's sign-in; the service is already signed out.</summary>
    private sealed class ClypDatSignedOutException() : Exception("ClypDat account is signed out.");

    /// <summary>A sign-in that stopped for a reason worded for the user.</summary>
    private sealed class ClypDatSignInException(string message) : Exception(message);

    private sealed class ActivityResponse
    {
        [JsonPropertyName("connected")] public bool Connected { get; set; }
        [JsonPropertyName("activity")] public Activity? Activity { get; set; }
        [JsonPropertyName("providers")] public string[]? Providers { get; set; }
        // Present only when the account has Discord linked.
        [JsonPropertyName("profile")] public Profile? Profile { get; set; }
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
