using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ClypDat.App.Services;

/// <summary>
/// The account's clip-link plan, as the activity poll reports it. The poll
/// sends none at all while plans are not offered to the account, so a null
/// <see cref="XboxActivitySnapshot.Plan"/> means "hide everything about links".
/// </summary>
// ClipsDeleteAt: with no plan, when the site deletes the account's shared clips.
internal sealed record ClipPlan(string? PlanId, string? PlanName, long StorageBytes, long UsedBytes, string? Source,
    DateTimeOffset? RenewsAt, DateTimeOffset? EndsAt, DateTimeOffset? ClipsDeleteAt = null)
{
    public bool IsActive => PlanId is not null && StorageBytes > 0;
}

/// <summary>A clip shared as a link, as clypdat.xyz describes it.</summary>
internal sealed record HostedClip(string Id, string Url, string VideoUrl, string? ThumbnailUrl, long Bytes, string? Title,
    long? DurationMs, int? Width, int? Height, DateTimeOffset CreatedAt);

/// <summary>A plan on offer. Prices are already formatted for the buyer's currency; null when the site has none.</summary>
internal sealed record ClipPlanOffer(string Id, string Name, long StorageBytes, string MonthlySlug, string YearlySlug,
    string? MonthlyPrice, string? YearlyPrice);

internal sealed record HostedClipList(IReadOnlyList<HostedClip> Clips, long UsedBytes, long LimitBytes, string? PlanId);

/// <summary>A refusal from clypdat.xyz, worded for the user. Code is the site's machine-readable reason, if it gave one.</summary>
internal sealed class ClipHostingException(string message, string? code = null, HttpStatusCode? status = null) : Exception(message)
{
    public string? Code { get; } = code;
    public HttpStatusCode? Status { get; } = status;
}

/// <summary>
/// Shares clips as links. The site hands out upload links signed for one
/// file's exact size and type; the files go straight to clip storage, and the
/// site checks what arrived before the link goes live. Everything about who
/// may upload and how much is decided by the site, not here.
/// </summary>
internal static class ClipHostingService
{
    private static readonly HttpClient Api = new(new HttpClientHandler { AllowAutoRedirect = false })
    {
        BaseAddress = new Uri("https://www.clypdat.xyz/"), Timeout = TimeSpan.FromSeconds(30),
        MaxResponseContentBufferSize = 4 * 1024 * 1024
    };
    // Uploads run as long as they need to; the caller's token cancels them.
    private static readonly HttpClient Storage = new(new HttpClientHandler { AllowAutoRedirect = false })
    {
        Timeout = Timeout.InfiniteTimeSpan, MaxResponseContentBufferSize = 64 * 1024
    };

    /// <summary>
    /// Uploads a finished MP4 (and, optionally, a JPEG thumbnail) and returns
    /// the live clip. Progress runs 0-1 over the video; the thumbnail and the
    /// site's check are quick enough to sit at the end of it.
    /// </summary>
    public static async Task<HostedClip> UploadAsync(string token, string videoPath, string? thumbnailPath, string? title,
        TimeSpan duration, int width, int height, IProgress<double>? progress, CancellationToken cancellationToken)
    {
        var videoBytes = new FileInfo(videoPath).Length;
        long? thumbnailBytes = thumbnailPath is not null && File.Exists(thumbnailPath) ? new FileInfo(thumbnailPath).Length : null;
        var ticket = await StartAsync(token, new StartRequest
        {
            Bytes = videoBytes,
            ThumbnailBytes = thumbnailBytes,
            Title = string.IsNullOrWhiteSpace(title) ? null : title,
            DurationMs = duration > TimeSpan.Zero ? (long)duration.TotalMilliseconds : null,
            Width = width > 0 ? width : null,
            Height = height > 0 ? height : null,
        }, cancellationToken).ConfigureAwait(false);

        await PutAsync(ticket.UploadUrl, videoPath, videoBytes, "video/mp4", progress, cancellationToken).ConfigureAwait(false);
        // A thumbnail that fails to arrive costs the clip its preview, not the link.
        if (ticket.ThumbnailUploadUrl is { } thumbnailUrl && thumbnailPath is not null && thumbnailBytes is { } thumbBytes)
        {
            try { await PutAsync(thumbnailUrl, thumbnailPath, thumbBytes, "image/jpeg", null, cancellationToken).ConfigureAwait(false); }
            catch (Exception error) when (!cancellationToken.IsCancellationRequested)
            {
                AppLog.Error("Clip links: thumbnail upload failed; the clip goes up without one.", error);
            }
        }
        return await CompleteAsync(token, ticket.ClipId, cancellationToken).ConfigureAwait(false);
    }

    public static async Task<HostedClipList> ListAsync(string token, CancellationToken cancellationToken)
    {
        using var request = Authorized(HttpMethod.Get, "api/desktop/clips", token);
        using var response = await Api.SendAsync(request, cancellationToken).ConfigureAwait(false);
        await ThrowIfFailedAsync(response, "Your shared clips are unavailable right now.", cancellationToken).ConfigureAwait(false);
        var body = await response.Content.ReadFromJsonAsync<ListResponse>(cancellationToken).ConfigureAwait(false)
            ?? throw new ClipHostingException("Your shared clips are unavailable right now.");
        return new HostedClipList((body.Clips ?? []).Select(ToClip).ToArray(), body.UsedBytes, body.LimitBytes, body.Plan);
    }

    public static async Task DeleteAsync(string token, string clipId, CancellationToken cancellationToken)
    {
        using var request = Authorized(HttpMethod.Delete, $"api/desktop/clips/{Uri.EscapeDataString(clipId)}", token);
        using var response = await Api.SendAsync(request, cancellationToken).ConfigureAwait(false);
        // Already gone is what was asked for.
        if (response.StatusCode == HttpStatusCode.NotFound) return;
        await ThrowIfFailedAsync(response, "The clip could not be deleted. Try again shortly.", cancellationToken).ConfigureAwait(false);
    }

    /// <summary>The plans on offer, priced in the currency of the country this PC is in.</summary>
    public static async Task<IReadOnlyList<ClipPlanOffer>> GetPlansAsync(string token, CancellationToken cancellationToken)
    {
        using var request = Authorized(HttpMethod.Get, "api/desktop/billing/plans", token);
        using var response = await Api.SendAsync(request, cancellationToken).ConfigureAwait(false);
        await ThrowIfFailedAsync(response, "Plans are unavailable right now.", cancellationToken).ConfigureAwait(false);
        var body = await response.Content.ReadFromJsonAsync<PlansResponse>(cancellationToken).ConfigureAwait(false);
        return (body?.Plans ?? []).Where(plan => plan.MonthlySlug is not null && plan.YearlySlug is not null)
            .Select(plan => new ClipPlanOffer(plan.Id ?? string.Empty, plan.Name ?? plan.Id ?? "Plan", plan.StorageBytes,
                plan.MonthlySlug!, plan.YearlySlug!, FormatMoney(plan.MonthlyPrice), FormatMoney(plan.YearlyPrice)))
            .ToArray();
    }

    /// <summary>
    /// Polar's checkout page for one plan, for the signed-in account. The app
    /// opens it in the browser; the plan arrives through the site's webhook
    /// and the next poll.
    /// </summary>
    public static async Task<string> StartCheckoutAsync(string token, string slug, CancellationToken cancellationToken)
    {
        using var request = Authorized(HttpMethod.Post, "api/desktop/billing/checkout", token);
        request.Content = JsonContent.Create(new CheckoutRequest { Slug = slug });
        using var response = await Api.SendAsync(request, cancellationToken).ConfigureAwait(false);
        await ThrowIfFailedAsync(response, "Checkout could not start. Try again shortly.", cancellationToken).ConfigureAwait(false);
        return await ReadBrowserUrlAsync(response, "Checkout could not start. Try again shortly.", cancellationToken).ConfigureAwait(false);
    }

    /// <summary>A one-time link into the subscription portal (card, invoices, changing or cancelling the plan).</summary>
    public static async Task<string> PortalAsync(string token, CancellationToken cancellationToken)
    {
        using var request = Authorized(HttpMethod.Post, "api/desktop/billing/portal", token);
        using var response = await Api.SendAsync(request, cancellationToken).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.NotFound) throw new ClipHostingException("There is no subscription to manage yet.");
        await ThrowIfFailedAsync(response, "The subscription page could not open. Try again shortly.", cancellationToken).ConfigureAwait(false);
        return await ReadBrowserUrlAsync(response, "The subscription page could not open. Try again shortly.", cancellationToken).ConfigureAwait(false);
    }

    // Opened with the shell, so only ever an https page.
    private static async Task<string> ReadBrowserUrlAsync(HttpResponseMessage response, string fallback, CancellationToken cancellationToken)
    {
        var body = await response.Content.ReadFromJsonAsync<UrlResponse>(cancellationToken).ConfigureAwait(false);
        return body?.Url is { } url && IsHttps(url) ? url : throw new ClipHostingException(fallback);
    }

    // The same short forms the website shows (en): A$3.99, US prices as $2.49.
    internal static string? FormatMoney(MoneyResponse? money)
    {
        if (money is null || string.IsNullOrWhiteSpace(money.Currency)) return null;
        var amount = (money.Amount / 100m).ToString("0.00", System.Globalization.CultureInfo.InvariantCulture);
        return money.Currency.ToLowerInvariant() switch
        {
            "usd" => $"${amount}",
            "aud" => $"A${amount}",
            "nzd" => $"NZ${amount}",
            "cad" => $"CA${amount}",
            "mxn" => $"MX${amount}",
            "brl" => $"R${amount}",
            "eur" => $"€{amount}",
            "gbp" => $"£{amount}",
            "inr" => $"₹{amount}",
            var other => $"{other.ToUpperInvariant()} {amount}",
        };
    }

    private static async Task<StartResponse> StartAsync(string token, StartRequest body, CancellationToken cancellationToken)
    {
        using var request = Authorized(HttpMethod.Post, "api/desktop/clips", token);
        request.Content = JsonContent.Create(body);
        using var response = await Api.SendAsync(request, cancellationToken).ConfigureAwait(false);
        await ThrowIfFailedAsync(response, "The upload could not start. Try again shortly.", cancellationToken).ConfigureAwait(false);
        var ticket = await response.Content.ReadFromJsonAsync<StartResponse>(cancellationToken).ConfigureAwait(false);
        if (ticket is not { ClipId.Length: > 0, UploadUrl.Length: > 0 }) throw new ClipHostingException("The upload could not start. Try again shortly.");
        // Only ever upload to clip storage over HTTPS, whatever the answer says.
        if (!IsHttps(ticket.UploadUrl) || (ticket.ThumbnailUploadUrl is { } thumb && !IsHttps(thumb)))
            throw new ClipHostingException("The upload could not start. Try again shortly.");
        return ticket;
    }

    private static async Task<HostedClip> CompleteAsync(string token, string clipId, CancellationToken cancellationToken)
    {
        using var request = Authorized(HttpMethod.Post, $"api/desktop/clips/{Uri.EscapeDataString(clipId)}/complete", token);
        using var response = await Api.SendAsync(request, cancellationToken).ConfigureAwait(false);
        await ThrowIfFailedAsync(response, "The upload could not be checked. Try again shortly.", cancellationToken).ConfigureAwait(false);
        var body = await response.Content.ReadFromJsonAsync<CompleteResponse>(cancellationToken).ConfigureAwait(false);
        return body?.Clip is { } clip ? ToClip(clip) : throw new ClipHostingException("The upload could not be checked. Try again shortly.");
    }

    // The upload link is signed for this exact Content-Type and Content-Length,
    // so nothing else may be added to or changed in either.
    private static async Task PutAsync(string url, string path, long length, string contentType, IProgress<double>? progress,
        CancellationToken cancellationToken)
    {
        using var content = new FileContent(path, length, progress);
        content.Headers.ContentType = new MediaTypeHeaderValue(contentType);
        using var request = new HttpRequestMessage(HttpMethod.Put, url) { Content = content };
        using var response = await Storage.SendAsync(request, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            AppLog.Info($"Clip links: storage refused the upload ({(int)response.StatusCode}).");
            throw new ClipHostingException("The upload did not go through. Try again shortly.", status: response.StatusCode);
        }
    }

    private static HttpRequestMessage Authorized(HttpMethod method, string path, string token)
    {
        var request = new HttpRequestMessage(method, path);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return request;
    }

    private static async Task ThrowIfFailedAsync(HttpResponseMessage response, string fallback, CancellationToken cancellationToken)
    {
        if (response.IsSuccessStatusCode) return;
        if (response.StatusCode == HttpStatusCode.Unauthorized)
            throw new ClipHostingException("Your ClypDat sign-in has expired. Sign in again in Settings.", status: response.StatusCode);
        string? message = null, code = null;
        try
        {
            using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false));
            if (json.RootElement.ValueKind == JsonValueKind.Object)
            {
                if (json.RootElement.TryGetProperty("error", out var error) && error.ValueKind == JsonValueKind.String) message = error.GetString();
                if (json.RootElement.TryGetProperty("code", out var codeValue) && codeValue.ValueKind == JsonValueKind.String) code = codeValue.GetString();
            }
        }
        catch (JsonException) { }
        // 404 is the site not offering links to this account (or at all yet);
        // its bare "Not found" means nothing to the user.
        if (response.StatusCode == HttpStatusCode.NotFound) message = null;
        throw new ClipHostingException(string.IsNullOrWhiteSpace(message) ? fallback : message, code, response.StatusCode);
    }

    private static bool IsHttps(string url) => Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps;

    private static HostedClip ToClip(ClipResponse clip) => new(clip.Id ?? string.Empty, clip.Url ?? string.Empty, clip.VideoUrl ?? string.Empty,
        clip.ThumbnailUrl, clip.Bytes, clip.Title, clip.DurationMs, clip.Width, clip.Height,
        DateTimeOffset.TryParse(clip.CreatedAt, out var created) ? created : DateTimeOffset.UtcNow);

    private sealed class FileContent(string path, long length, IProgress<double>? progress) : HttpContent
    {
        protected override bool TryComputeLength(out long value) { value = length; return true; }
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) =>
            SerializeToStreamAsync(stream, context, CancellationToken.None);

        protected override async Task SerializeToStreamAsync(Stream stream, TransportContext? context, CancellationToken token)
        {
            await using var source = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20, useAsync: true);
            var buffer = new byte[1 << 20];
            long sent = 0;
            while (sent < length)
            {
                var read = await source.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, length - sent)), token).ConfigureAwait(false);
                if (read == 0) throw new IOException("The clip changed while it was uploading.");
                await stream.WriteAsync(buffer.AsMemory(0, read), token).ConfigureAwait(false);
                sent += read;
                progress?.Report(sent / (double)Math.Max(1, length));
            }
        }
    }

    private sealed class StartRequest
    {
        [JsonPropertyName("contentType")] public string ContentType { get; init; } = "video/mp4";
        [JsonPropertyName("bytes")] public long Bytes { get; init; }
        [JsonPropertyName("thumbnailBytes"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public long? ThumbnailBytes { get; init; }
        [JsonPropertyName("title"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string? Title { get; init; }
        [JsonPropertyName("durationMs"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public long? DurationMs { get; init; }
        [JsonPropertyName("width"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public int? Width { get; init; }
        [JsonPropertyName("height"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public int? Height { get; init; }
    }

    private sealed class CheckoutRequest
    {
        [JsonPropertyName("slug")] public string Slug { get; init; } = string.Empty;
    }

    private sealed class UrlResponse
    {
        [JsonPropertyName("url")] public string? Url { get; set; }
    }

    private sealed class PlansResponse
    {
        [JsonPropertyName("plans")] public PlanOfferResponse[]? Plans { get; set; }
    }

    private sealed class PlanOfferResponse
    {
        [JsonPropertyName("id")] public string? Id { get; set; }
        [JsonPropertyName("name")] public string? Name { get; set; }
        [JsonPropertyName("storageBytes")] public long StorageBytes { get; set; }
        [JsonPropertyName("monthlySlug")] public string? MonthlySlug { get; set; }
        [JsonPropertyName("yearlySlug")] public string? YearlySlug { get; set; }
        [JsonPropertyName("monthlyPrice")] public MoneyResponse? MonthlyPrice { get; set; }
        [JsonPropertyName("yearlyPrice")] public MoneyResponse? YearlyPrice { get; set; }
    }

    internal sealed class MoneyResponse
    {
        [JsonPropertyName("amount")] public long Amount { get; set; }
        [JsonPropertyName("currency")] public string? Currency { get; set; }
    }

    private sealed class StartResponse
    {
        [JsonPropertyName("clipId")] public string ClipId { get; set; } = string.Empty;
        [JsonPropertyName("uploadUrl")] public string UploadUrl { get; set; } = string.Empty;
        [JsonPropertyName("thumbnailUploadUrl")] public string? ThumbnailUploadUrl { get; set; }
        [JsonPropertyName("expiresAt")] public string? ExpiresAt { get; set; }
    }

    private sealed class CompleteResponse
    {
        [JsonPropertyName("clip")] public ClipResponse? Clip { get; set; }
    }

    private sealed class ListResponse
    {
        [JsonPropertyName("clips")] public ClipResponse[]? Clips { get; set; }
        [JsonPropertyName("usedBytes")] public long UsedBytes { get; set; }
        [JsonPropertyName("limitBytes")] public long LimitBytes { get; set; }
        [JsonPropertyName("plan")] public string? Plan { get; set; }
    }

    private sealed class ClipResponse
    {
        [JsonPropertyName("id")] public string? Id { get; set; }
        [JsonPropertyName("url")] public string? Url { get; set; }
        [JsonPropertyName("videoUrl")] public string? VideoUrl { get; set; }
        [JsonPropertyName("thumbnailUrl")] public string? ThumbnailUrl { get; set; }
        [JsonPropertyName("bytes")] public long Bytes { get; set; }
        [JsonPropertyName("title")] public string? Title { get; set; }
        [JsonPropertyName("durationMs")] public long? DurationMs { get; set; }
        [JsonPropertyName("width")] public int? Width { get; set; }
        [JsonPropertyName("height")] public int? Height { get; set; }
        [JsonPropertyName("createdAt")] public string? CreatedAt { get; set; }
    }
}
