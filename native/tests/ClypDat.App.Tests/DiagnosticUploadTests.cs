using System.IO.Compression;
using System.Net;
using System.Text;
using ClypDat.App.Services;
using ClypDat.Core.Settings;
using Xunit;

namespace ClypDat.App.Tests;

public sealed class DiagnosticUploadTests
{
    [Fact]
    public void UploadBundleCapsRecentLogs_AndRedactsCredentials()
    {
        var previous = AppDataPaths.ProductFolderName;
        AppDataPaths.ConfigureProductFolder("ClypDat-DiagnosticUploadTest-" + Guid.NewGuid().ToString("N"));
        var root = AppDataPaths.Root;
        try
        {
            Directory.CreateDirectory(root);
            for (var i = 0; i < 6; i++)
            {
                var file = Path.Combine(root, $"clypdat-{i}.log");
                File.WriteAllText(file, new string('x', 600_000) + "\nBearer fixture-secret-token\naccess_token=fixture-access-token\nLatest useful event\n");
                File.SetLastWriteTimeUtc(file, DateTime.UtcNow.AddMinutes(i - 10));
            }
            var path = CaptureDiagnosticBundle.Create(null, null, root, DateTime.Now, recentOnly: true);
            using var archive = ZipFile.OpenRead(path);
            var logs = archive.Entries.Where(entry => entry.FullName.StartsWith("logs/")).ToArray();
            Assert.Equal(4, logs.Length);
            Assert.DoesNotContain(logs, entry => entry.Name == "clypdat-0.log" || entry.Name == "clypdat-1.log");
            Assert.NotNull(archive.GetEntry("bundle-scope.json"));
            foreach (var entry in logs)
            {
                using var reader = new StreamReader(entry.Open());
                var text = reader.ReadToEnd();
                Assert.Contains("Latest useful event", text);
                Assert.Contains("[Earlier log content omitted", text);
                Assert.DoesNotContain("fixture-secret-token", text);
                Assert.DoesNotContain("fixture-access-token", text);
                Assert.InRange(entry.Length, 1, 513 * 1024);
            }
            Assert.True(new FileInfo(path).Length < DiagnosticUploadService.MaximumBytes);
        }
        finally
        {
            AppDataPaths.ConfigureProductFolder(previous);
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task UploadSendsAuthenticatedMultipart_ReportsProgress_AndRequiresMatchingReceipt()
    {
        var path = Path.Combine(AppContext.BaseDirectory, $"diagnostic-upload-{Guid.NewGuid():N}.zip");
        var id = Guid.NewGuid();
        var progress = new InlineProgress();
        await File.WriteAllBytesAsync(path, new byte[100_000]);
        try
        {
            using var client = new HttpClient(new Handler(async (request, token) =>
            {
                Assert.Equal("https://clypdat.test/api/desktop/support", request.RequestUri!.ToString());
                Assert.Equal("Bearer", request.Headers.Authorization!.Scheme);
                Assert.Equal("fixture-token", request.Headers.Authorization.Parameter);
                Assert.Equal("multipart/form-data", request.Content!.Headers.ContentType!.MediaType);
                var body = Encoding.UTF8.GetString(await request.Content.ReadAsByteArrayAsync(token));
                Assert.Contains(id.ToString(), body);
                Assert.Contains("The recorder stopped after a match.", body);
                Assert.Contains("diagnostics.zip", body);
                Assert.DoesNotContain(path, body);
                return new HttpResponseMessage(HttpStatusCode.Created) { Content = new StringContent($"{{\"id\":\"{id}\"}}") };
            })) { BaseAddress = new Uri("https://clypdat.test/") };
            Assert.Equal(id.ToString(), await DiagnosticUploadService.SendAsync(client, "fixture-token", path,
                "The recorder stopped after a match.", id, progress, CancellationToken.None));
            Assert.Equal(100, progress.Values[^1]);
            Assert.True(progress.Values.SequenceEqual(progress.Values.Order()));

            using var wrongReceipt = new HttpClient(new Handler((_, _) => Task.FromResult(
                new HttpResponseMessage(HttpStatusCode.Created) { Content = new StringContent($"{{\"id\":\"{Guid.NewGuid()}\"}}") })))
                { BaseAddress = client.BaseAddress };
            await Assert.ThrowsAsync<InvalidOperationException>(() => DiagnosticUploadService.SendAsync(wrongReceipt,
                "fixture-token", path, "The recorder stopped after a match.", id, null, CancellationToken.None));
        }
        finally { File.Delete(path); }
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized, "sign-in has expired")]
    [InlineData(HttpStatusCode.TooManyRequests, "Too many reports")]
    [InlineData(HttpStatusCode.ServiceUnavailable, "could not be received")]
    public async Task FailedUploadHasAnActionableError(HttpStatusCode code, string expected)
    {
        var path = Path.Combine(AppContext.BaseDirectory, $"diagnostic-upload-{Guid.NewGuid():N}.zip");
        await File.WriteAllBytesAsync(path, new byte[32]);
        try
        {
            using var client = new HttpClient(new Handler((_, _) => Task.FromResult(new HttpResponseMessage(code))))
                { BaseAddress = new Uri("https://clypdat.test/") };
            var error = await Assert.ThrowsAsync<InvalidOperationException>(() => DiagnosticUploadService.SendAsync(client,
                "fixture-token", path, "The recorder stopped after a match.", Guid.NewGuid(), null, CancellationToken.None));
            Assert.Contains(expected, error.Message);
        }
        finally { File.Delete(path); }
    }

    private sealed class InlineProgress : IProgress<double>
    {
        public List<double> Values { get; } = [];
        public void Report(double value) => Values.Add(value);
    }

    [Theory]
    [InlineData(null, false)]
    [InlineData("", false)]
    [InlineData("invalid", false)]
    [InlineData("a@b", false)]
    [InlineData("a@b..com", false)]
    [InlineData("Name <a@example.com>", false)]
    [InlineData("a@example.com\nb@example.com", false)]
    [InlineData(" Guest@Example.com ", true)]
    [InlineData("guest+support@example.com.au", true)]
    public void ContactEmailValidation(string? email, bool valid) =>
        Assert.Equal(valid, DiagnosticUploadService.IsValidContactEmail(email));

    [Fact]
    public async Task GuestUploadRequiresEmail_AndSendsNoAccountCredentials()
    {
        var path = Path.Combine(AppContext.BaseDirectory, $"diagnostic-upload-{Guid.NewGuid():N}.zip");
        var id = Guid.NewGuid();
        await File.WriteAllBytesAsync(path, new byte[32]);
        var calls = 0;
        try
        {
            using var client = new HttpClient(new Handler(async (request, token) =>
            {
                calls++;
                Assert.Null(request.Headers.Authorization);
                var body = await request.Content!.ReadAsStringAsync(token);
                Assert.Contains("guest@example.com", body);
                return new HttpResponseMessage(HttpStatusCode.Created) { Content = new StringContent($"{{\"id\":\"{id}\"}}") };
            })) { BaseAddress = new Uri("https://clypdat.test/") };
            await Assert.ThrowsAsync<InvalidOperationException>(() => DiagnosticUploadService.SendAsync(client, null,
                path, "A useful description of the issue.", id, null, CancellationToken.None));
            Assert.Equal(0, calls);
            Assert.Equal(id.ToString(), await DiagnosticUploadService.SendAsync(client, null, path,
                "A useful description of the issue.", id, null, CancellationToken.None, " Guest@Example.com "));
            Assert.Equal(1, calls);
        }
        finally { File.Delete(path); }
    }

    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) => send(request, token);
    }
}
