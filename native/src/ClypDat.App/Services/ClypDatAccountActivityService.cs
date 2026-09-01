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
    private CancellationTokenSource? _pollCts;
    private readonly SemaphoreSlim _pollWake = new(0, 1);
    private DateTimeOffset _linkWatchUntil = DateTimeOffset.MinValue;
    private DateTimeOffset _linkWatchStarted = DateTimeOffset.MinValue;
    private DateTimeOffset _lastRefresh = DateTimeOffset.MinValue;
    private (bool Xbox, bool Google, bool Discord) _linkWatchSignature;
    private DesktopToken? _token;
    private XboxActivitySnapshot _snapshot = XboxActivitySnapshot.Disconnected;

    public XboxActivitySnapshot Snapshot => _snapshot;
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
            SaveToken(_token);
            await RefreshAsync(cancellationToken).ConfigureAwait(false);
            StartPolling();
            return true;
        }
        catch (Exception error)
        {
            AppLog.Error("ClypDat account: connection failed.", error);
            _token = null;
            var message = error is InvalidOperationException invalid && invalid.Message.StartsWith("ClypDat sign-in required.", StringComparison.Ordinal)
                ? invalid.Message
                : "ClypDat account connection failed. Sign in through the browser and try again.";
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
            if (!response.IsSuccessStatusCode) throw new HttpRequestException($"ClypDat Xbox unlink failed ({(int)response.StatusCode}).");
            await RefreshAsync(cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch (Exception error)
        {
            AppLog.Error("ClypDat account: Xbox unlink failed.", error);
            _snapshot = _snapshot with { Error = "ClypDat Xbox unlink failed. Try again." };
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
                throw new HttpRequestException(TryReadError(body) ?? $"ClypDat unlink failed ({(int)response.StatusCode}).");
            }
            await RefreshAsync(cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch (Exception error)
        {
            AppLog.Error($"ClypDat account: {provider} unlink failed.", error);
            var message = error is HttpRequestException ? error.Message : $"{provider} could not be disconnected. Try again.";
            _snapshot = _snapshot with { Error = message };
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
        if (DateTimeOffset.UtcNow - _lastRefresh < RefreshDebounce) return;
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
        if (now >= _linkWatchUntil) return TimeSpan.FromSeconds(_snapshot.CurrentTitle is null ? 60 : 15);
        return now - _linkWatchStarted < LinkWatchEager ? LinkWatchEagerInterval : LinkWatchTailInterval;
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
                // Whatever the user went to the browser to do, they have done it.
                if (LinkSignature != _linkWatchSignature) _linkWatchUntil = DateTimeOffset.MinValue;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
            catch (Exception error)
            {
                AppLog.Error("ClypDat account: activity refresh failed.", error);
                _snapshot = _snapshot with { Error = "ClypDat Xbox activity is temporarily unavailable." };
                Changed?.Invoke(this, _snapshot);
                try { await Task.Delay(TimeSpan.FromSeconds(60), cancellationToken).ConfigureAwait(false); }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
            }
        }
    }

    public async Task RefreshAsync(CancellationToken cancellationToken = default)
    {
        if (_token is null) throw new InvalidOperationException("ClypDat account is not authenticated.");
        using var request = new HttpRequestMessage(HttpMethod.Get, "api/desktop/xbox/activity");
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
            throw new HttpRequestException(message);
        }
        _lastRefresh = DateTimeOffset.UtcNow;
        var result = JsonSerializer.Deserialize<ActivityResponse>(body) ?? throw new InvalidOperationException("ClypDat activity returned no data.");
        var activity = result.Activity;
        var providers = result.Providers ?? Array.Empty<string>();
        _snapshot = new XboxActivitySnapshot(result.Connected, null, activity?.Title, activity?.ConsoleName, activity is null ? DateTimeOffset.UtcNow : ParseTimestamp(activity.UpdatedAt), null,
            providers.Contains("google", StringComparer.OrdinalIgnoreCase), providers.Contains("discord", StringComparer.OrdinalIgnoreCase));
        Changed?.Invoke(this, _snapshot);
    }

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
        var context = await listener.GetContextAsync().WaitAsync(TimeSpan.FromMinutes(5), cancellationToken).ConfigureAwait(false);
        try
        {
            if (!string.Equals(context.Request.QueryString["state"], state, StringComparison.Ordinal)) throw new InvalidOperationException("ClypDat sign-in returned an invalid response.");
            var error = context.Request.QueryString["error"];
            if (string.Equals(error, "login-required", StringComparison.Ordinal))
                throw new InvalidOperationException("ClypDat sign-in required. Open clypdat.xyz/account, sign in, then retry here.");
            if (!string.IsNullOrWhiteSpace(error)) throw new InvalidOperationException("ClypDat sign-in was not completed.");
            var accessToken = context.Request.QueryString["token"];
            if (string.IsNullOrWhiteSpace(accessToken)) throw new InvalidOperationException("ClypDat sign-in returned no token.");
            var expiresIn = int.TryParse(context.Request.QueryString["expires_in"], out var seconds) ? seconds : 60 * 60 * 24 * 30;
            var body = BrowserCallbackPage.Success();
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
    }
    private sealed class Activity
    {
        [JsonPropertyName("title")] public string? Title { get; set; }
        [JsonPropertyName("consoleName")] public string? ConsoleName { get; set; }
        [JsonPropertyName("updatedAt")] public string? UpdatedAt { get; set; }
    }
}
