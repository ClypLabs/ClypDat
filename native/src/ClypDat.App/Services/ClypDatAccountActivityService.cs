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
    private CancellationTokenSource? _pollCts;
    private DesktopToken? _token;
    private XboxActivitySnapshot _snapshot = XboxActivitySnapshot.Disconnected;

    public XboxActivitySnapshot Snapshot => _snapshot;
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
            var message = error is InvalidOperationException invalid && invalid.Message.StartsWith("No Xbox account linked.", StringComparison.Ordinal)
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
                await RefreshAsync(cancellationToken).ConfigureAwait(false);
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

    private async Task RefreshAsync(CancellationToken cancellationToken)
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
        var result = JsonSerializer.Deserialize<ActivityResponse>(body) ?? throw new InvalidOperationException("ClypDat activity returned no data.");
        var activity = result.Activity;
        _snapshot = new XboxActivitySnapshot(result.Connected, null, activity?.Title, activity?.ConsoleName, activity is null ? DateTimeOffset.UtcNow : ParseTimestamp(activity.UpdatedAt), null);
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
            if (string.Equals(error, "xbox-not-linked", StringComparison.Ordinal))
                throw new InvalidOperationException("No Xbox account linked. Open clypdat.xyz/account, link Xbox, then retry here.");
            if (!string.IsNullOrWhiteSpace(error)) throw new InvalidOperationException("ClypDat sign-in was not completed.");
            var accessToken = context.Request.QueryString["token"];
            if (string.IsNullOrWhiteSpace(accessToken)) throw new InvalidOperationException("ClypDat sign-in returned no token.");
            var expiresIn = int.TryParse(context.Request.QueryString["expires_in"], out var seconds) ? seconds : 60 * 60 * 24 * 30;
            var body = Encoding.UTF8.GetBytes("You may close this window and return to ClypDat.");
            context.Response.ContentType = "text/plain; charset=utf-8";
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
    public void Dispose() { _pollCts?.Cancel(); _pollCts?.Dispose(); _http.Dispose(); }

    private sealed record DesktopToken(string AccessToken, DateTimeOffset ExpiresAt);
    private sealed class ActivityResponse
    {
        [JsonPropertyName("connected")] public bool Connected { get; set; }
        [JsonPropertyName("activity")] public Activity? Activity { get; set; }
    }
    private sealed class Activity
    {
        [JsonPropertyName("title")] public string? Title { get; set; }
        [JsonPropertyName("consoleName")] public string? ConsoleName { get; set; }
        [JsonPropertyName("updatedAt")] public string? UpdatedAt { get; set; }
    }
}
