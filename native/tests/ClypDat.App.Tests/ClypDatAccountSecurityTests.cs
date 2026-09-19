using System.Net;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using ClypDat.App.Services;
using Xunit;

namespace ClypDat.App.Tests;

public sealed class ClypDatAccountSecurityTests
{
    [Fact]
    public void CodeChallengeMatchesRfc7636Vector() =>
        Assert.Equal("E9Melhoa2OwvFrEMTJguCHaoeK1t8URWbuGJSstw-cM",
            ClypDatAccountActivityService.CodeChallenge("dBjftJeZ4CVP-mB92K27uhbUJU1p1r_wW1gFWFOEjXk"));

    // Fixtures generated with the website's Node SHA-256/UTF-8 pairing algorithm.
    [Theory]
    [InlineData("test-state-0123456789", "UV3-UX3")]
    [InlineData("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", "9RE-TQA")]
    [InlineData("f3f5b8f73b524f7e91c765c4054e31ab", "A3J-Z53")]
    public void PairingCodeMatchesWebsite(string state, string expected) =>
        Assert.Equal(expected, ClypDatAccountActivityService.PairingCode(state));

    [Fact]
    public async Task SuccessfulRevokeHappensBeforeLocalCredentialsAreDeleted()
    {
        using var fixture = new AccountFixture(HttpStatusCode.OK);
        Assert.True(await fixture.Service.TryRestoreAsync());
        Assert.True(fixture.Service.IsAuthenticated);
        Assert.True(await fixture.Service.RevokeAndDisconnectAsync());
        Assert.Equal(1, fixture.RevokeRequests);
        Assert.True(fixture.CacheExistedAtRevoke);
        Assert.False(fixture.Service.IsAuthenticated);
        Assert.False(File.Exists(fixture.CachePath));
    }

    [Fact]
    public async Task AlreadyRevokedTokenCanBeRemovedLocally()
    {
        using var fixture = new AccountFixture(HttpStatusCode.Unauthorized);
        Assert.True(await fixture.Service.TryRestoreAsync());
        Assert.True(await fixture.Service.RevokeAndDisconnectAsync());
        Assert.False(fixture.Service.IsAuthenticated);
        Assert.False(File.Exists(fixture.CachePath));
    }

    [Fact]
    public async Task FailedRevokePreservesCredentialsAndReportsFailure()
    {
        using var fixture = new AccountFixture(HttpStatusCode.ServiceUnavailable);
        var original = File.ReadAllBytes(fixture.CachePath);
        Assert.True(await fixture.Service.TryRestoreAsync());
        Assert.False(await fixture.Service.RevokeAndDisconnectAsync());
        Assert.Equal(1, fixture.RevokeRequests);
        Assert.True(fixture.Service.IsAuthenticated);
        Assert.Equal(original, File.ReadAllBytes(fixture.CachePath));
        Assert.Contains("Couldn't sign out", fixture.Service.Snapshot.Error);
    }

    [Fact]
    public void FailedAtomicCacheReplacementPreservesOriginalAndRemovesTemporaryFile()
    {
        using var fixture = new AccountFixture(HttpStatusCode.OK);
        var original = File.ReadAllBytes(fixture.CachePath);
        var tokenType = typeof(ClypDatAccountActivityService).GetNestedType("DesktopToken", BindingFlags.NonPublic)!;
        var replacement = Activator.CreateInstance(tokenType, "replacement-test-token", DateTimeOffset.UtcNow.AddHours(1));
        var save = typeof(ClypDatAccountActivityService).GetMethod("SaveToken", BindingFlags.Instance | BindingFlags.NonPublic)!;
        using (var locked = new FileStream(fixture.CachePath, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            var error = Assert.Throws<TargetInvocationException>(() => save.Invoke(fixture.Service, new[] { replacement }));
            Assert.True(error.InnerException is IOException or UnauthorizedAccessException, error.InnerException?.ToString());
        }
        Assert.Equal(original, File.ReadAllBytes(fixture.CachePath));
        Assert.Empty(Directory.GetFiles(fixture.Root, "*.tmp-*"));
    }

    private sealed class AccountFixture : IDisposable
    {
        private const string Token = "account-security-test-token";
        private readonly string _allowedRoot;
        public string Root { get; }
        public string CachePath { get; }
        public ClypDatAccountActivityService Service { get; }
        public int RevokeRequests { get; private set; }
        public bool CacheExistedAtRevoke { get; private set; }

        public AccountFixture(HttpStatusCode revokeStatus)
        {
            var directory = new DirectoryInfo(AppContext.BaseDirectory);
            while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "native", "ClypDat.Native.sln")))
                directory = directory.Parent;
            if (directory is null) throw new InvalidOperationException("Could not locate the repository test artifact root.");
            _allowedRoot = Path.GetFullPath(Path.Combine(directory.FullName, ".local", "account-security-tests"));
            Root = Path.Combine(_allowedRoot, Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Root);
            CachePath = Path.Combine(Root, "account.bin");
            File.WriteAllBytes(CachePath, ProtectedData.Protect(JsonSerializer.SerializeToUtf8Bytes(new
            {
                AccessToken = Token, ExpiresAt = DateTimeOffset.UtcNow.AddHours(1)
            }), null, DataProtectionScope.CurrentUser));
            var client = new HttpClient(new FixtureHandler(request =>
            {
                Assert.Equal("Bearer", request.Headers.Authorization?.Scheme);
                Assert.Equal(Token, request.Headers.Authorization?.Parameter);
                if (request.Method == HttpMethod.Get && request.RequestUri?.AbsolutePath == "/api/desktop/xbox/activity")
                    return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{\"connected\":true,\"providers\":[\"google\"]}") };
                Assert.Equal(HttpMethod.Post, request.Method);
                Assert.Equal("/api/desktop/revoke", request.RequestUri?.AbsolutePath);
                RevokeRequests++;
                CacheExistedAtRevoke = File.Exists(CachePath);
                return new HttpResponseMessage(revokeStatus);
            })) { BaseAddress = new Uri("https://account-security.invalid/") };
            Service = new ClypDatAccountActivityService(client, CachePath) { LiveActivityNeeded = () => false };
        }

        public void Dispose()
        {
            Service.Dispose();
            var resolved = Path.GetFullPath(Root);
            if (!resolved.StartsWith(_allowedRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Test cleanup escaped its artifact root.");
            Directory.Delete(resolved, recursive: true);
        }
    }

    private sealed class FixtureHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(respond(request));
    }
}
