using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;

namespace ClypDat.App.Services;

internal static class DiagnosticUploadService
{
    internal const int MaximumBytes = 3 * 1024 * 1024;
    private static readonly HttpClient Client = new(new HttpClientHandler { AllowAutoRedirect = false })
    {
        BaseAddress = new Uri("https://www.clypdat.xyz/"), Timeout = TimeSpan.FromMinutes(2),
        MaxResponseContentBufferSize = 16 * 1024
    };

    public static Task<string> SendAsync(string token, string path, string message, Guid reportId,
        IProgress<double>? progress, CancellationToken cancellationToken) =>
        SendAsync(Client, token, path, message, reportId, progress, cancellationToken);

    internal static async Task<string> SendAsync(HttpClient client, string token, string path, string message,
        Guid reportId, IProgress<double>? progress, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(token)) throw new InvalidOperationException("Link your ClypDat account before sending diagnostics.");
        if (message.Trim().Length < 10 || message.Length > 2000) throw new InvalidOperationException("Describe the issue in 10 to 2000 characters.");
        var length = new FileInfo(path).Length;
        if (length > MaximumBytes) throw new InvalidOperationException("The bundle is too large to send. Use Export ZIP to save it locally.");
        var revision = System.Reflection.CustomAttributeExtensions.GetCustomAttribute<System.Reflection.AssemblyInformationalVersionAttribute>(
            typeof(DiagnosticUploadService).Assembly)?.InformationalVersion ?? "unknown";
        using var body = new MultipartFormDataContent();
        body.Add(new StringContent(reportId.ToString()), "id");
        body.Add(new StringContent(message.Trim()), "message");
        body.Add(new StringContent(AppUpdateService.CurrentVersion.ToString()), "version");
        body.Add(new StringContent(revision), "build");
        body.Add(new UploadContent(path, length, progress), "bundle", "diagnostics.zip");
        using var request = new HttpRequestMessage(HttpMethod.Post, "api/desktop/support") { Content = body };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var response = await client.SendAsync(request, cancellationToken).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.Unauthorized)
            throw new InvalidOperationException("Your sign-in has expired. Reconnect your ClypDat account in Settings, then try again.");
        if ((int)response.StatusCode == 429)
            throw new InvalidOperationException("Too many reports today. Try tomorrow, or use Export ZIP to save a local bundle.");
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException("The report could not be received. Try again shortly, or use Export ZIP.");
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false));
        if (!json.RootElement.TryGetProperty("id", out var id) || !Guid.TryParse(id.GetString(), out var received) || received != reportId)
            throw new InvalidOperationException("The server did not confirm the report. Please try again.");
        return received.ToString();
    }

    private sealed class UploadContent(string path, long length, IProgress<double>? progress) : HttpContent
    {
        protected override bool TryComputeLength(out long value) { value = length; return true; }
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) =>
            SerializeToStreamAsync(stream, context, CancellationToken.None);

        protected override async Task SerializeToStreamAsync(Stream stream, TransportContext? context, CancellationToken token)
        {
            await using var source = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, useAsync: true);
            var buffer = new byte[65536];
            long sent = 0;
            while (sent < length)
            {
                var read = await source.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, length - sent)), token).ConfigureAwait(false);
                if (read == 0) throw new IOException("Diagnostic bundle changed during upload.");
                await stream.WriteAsync(buffer.AsMemory(0, read), token).ConfigureAwait(false);
                sent += read;
                progress?.Report(sent * 100d / Math.Max(1, length));
            }
        }
    }
}
