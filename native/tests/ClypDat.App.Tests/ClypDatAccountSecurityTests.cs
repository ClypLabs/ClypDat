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
    public async Task SignOutQueuesTheRevokeBeforeLocalCredentialsAreDeleted()
    {
        using var fixture = new AccountFixture(HttpStatusCode.OK);
        Assert.True(await fixture.Service.TryRestoreAsync());
        Assert.True(fixture.Service.IsAuthenticated);
        Assert.True(await fixture.Service.SignOutAsync());
        Assert.Equal(1, fixture.RevokeRequests);
        // A crash between the two steps must still leave the token recorded
        // somewhere it will be revoked from.
        Assert.True(fixture.RevokeQueuedAtRevoke);
        Assert.False(fixture.Service.IsAuthenticated);
        Assert.False(File.Exists(fixture.CachePath));
        Assert.False(File.Exists(fixture.RevokePath));
    }

    [Fact]
    public async Task AlreadyRevokedTokenCanBeRemovedLocally()
    {
        using var fixture = new AccountFixture(HttpStatusCode.Unauthorized);
        Assert.True(await fixture.Service.TryRestoreAsync());
        Assert.True(await fixture.Service.SignOutAsync());
        Assert.False(fixture.Service.IsAuthenticated);
        Assert.False(File.Exists(fixture.CachePath));
        Assert.False(File.Exists(fixture.RevokePath));
    }

    [Fact]
    public async Task SignOutDuringAnOutageSignsOutHereAndRetriesTheRevokeLater()
    {
        using var fixture = new AccountFixture(HttpStatusCode.ServiceUnavailable);
        Assert.True(await fixture.Service.TryRestoreAsync());
        Assert.False(await fixture.Service.SignOutAsync(), "the site has not confirmed");
        Assert.Equal(1, fixture.RevokeRequests);
        // Signed out here regardless: Sign out used to do nothing at all while
        // clypdat.xyz answered 503.
        Assert.False(fixture.Service.IsAuthenticated);
        Assert.False(File.Exists(fixture.CachePath));
        Assert.True(File.Exists(fixture.RevokePath), "the token waits, encrypted, for a retry");
        Assert.DoesNotContain(fixture.Token, File.ReadAllText(fixture.RevokePath));

        fixture.RevokeStatus = HttpStatusCode.NoContent;
        Assert.True(await fixture.Service.FlushPendingRevokesAsync());
        Assert.Equal(2, fixture.RevokeRequests);
        Assert.False(File.Exists(fixture.RevokePath));
    }

    [Fact]
    public async Task SiteRejectingTheTokenSignsOutCleanly()
    {
        using var fixture = new AccountFixture(HttpStatusCode.OK) { ActivityStatus = HttpStatusCode.Unauthorized };
        Assert.False(await fixture.Service.TryRestoreAsync());
        Assert.False(fixture.Service.IsAuthenticated);
        Assert.False(File.Exists(fixture.CachePath));
        Assert.Contains("signed out", fixture.Service.Snapshot.Error, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(1, fixture.ActivityRequests);
    }

    [Fact]
    public async Task ServerErrorAtRestoreKeepsTheSignIn()
    {
        using var fixture = new AccountFixture(HttpStatusCode.OK) { ActivityStatus = HttpStatusCode.ServiceUnavailable };
        var original = File.ReadAllBytes(fixture.CachePath);
        Assert.True(await fixture.Service.TryRestoreAsync());
        Assert.True(fixture.Service.IsAuthenticated);
        Assert.Equal(original, File.ReadAllBytes(fixture.CachePath));
        Assert.True(fixture.Service.Snapshot.ServerUnavailable);
    }

    [Fact]
    public async Task SignInInItsLastWeekIsRenewed()
    {
        using var fixture = new AccountFixture(HttpStatusCode.OK, TimeSpan.FromDays(2));
        Assert.True(await fixture.Service.TryRestoreAsync());
        Assert.Equal(1, fixture.RenewRequests);
        using var saved = JsonDocument.Parse(ProtectedData.Unprotect(File.ReadAllBytes(fixture.CachePath), null, DataProtectionScope.CurrentUser));
        Assert.Equal(AccountFixture.RenewedToken, saved.RootElement.GetProperty("AccessToken").GetString());
        Assert.True(saved.RootElement.GetProperty("ExpiresAt").GetDateTimeOffset() > DateTimeOffset.UtcNow.AddDays(29));
    }

    [Fact]
    public async Task SignInWithTimeLeftIsNotRenewed()
    {
        using var fixture = new AccountFixture(HttpStatusCode.OK, TimeSpan.FromDays(20));
        Assert.True(await fixture.Service.TryRestoreAsync());
        Assert.Equal(0, fixture.RenewRequests);
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
        public const string RenewedToken = "account-security-renewed-token";
        private readonly string _allowedRoot;
        public string Token { get; } = "account-security-test-token";
        public string Root { get; }
        public string CachePath { get; }
        public string RevokePath => CachePath + ".revoke";
        public ClypDatAccountActivityService Service { get; }
        public HttpStatusCode RevokeStatus { get; set; }
        public HttpStatusCode ActivityStatus { get; init; } = HttpStatusCode.OK;
        // What the site sent while it still tracked Spotify: the fields are ignored now.
        public string ActivityBody { get; init; } = "{\"connected\":true,\"providers\":[\"google\"],\"spotify\":false,\"spotifyDisconnect\":\"2026-09-23T01:02:03Z\"}";
        public int RevokeRequests { get; private set; }
        public int ActivityRequests { get; private set; }
        public int RenewRequests { get; private set; }
        public bool RevokeQueuedAtRevoke { get; private set; }

        public AccountFixture(HttpStatusCode revokeStatus, TimeSpan? lifetime = null)
        {
            RevokeStatus = revokeStatus;
            var directory = new DirectoryInfo(AppContext.BaseDirectory);
            while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "native", "ClypDat.Native.sln")))
                directory = directory.Parent;
            if (directory is null) throw new InvalidOperationException("Could not locate the repository test artifact root.");
            _allowedRoot = Path.GetFullPath(Path.Combine(directory.FullName, ".local", "account-security-tests"));
            Root = Path.Combine(_allowedRoot, Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Root);
            CachePath = Path.Combine(Root, "account.bin");
            // Thirty days by default: far enough from expiry that no test
            // renews unless it asks to.
            File.WriteAllBytes(CachePath, ProtectedData.Protect(JsonSerializer.SerializeToUtf8Bytes(new
            {
                AccessToken = Token, ExpiresAt = DateTimeOffset.UtcNow.Add(lifetime ?? TimeSpan.FromDays(30))
            }), null, DataProtectionScope.CurrentUser));
            var client = new HttpClient(new FixtureHandler(request =>
            {
                Assert.Equal("Bearer", request.Headers.Authorization?.Scheme);
                Assert.Equal(Token, request.Headers.Authorization?.Parameter);
                var path = request.RequestUri?.AbsolutePath;
                if (request.Method == HttpMethod.Get && path == "/api/desktop/xbox/activity")
                {
                    ActivityRequests++;
                    return new HttpResponseMessage(ActivityStatus) { Content = new StringContent(ActivityStatus == HttpStatusCode.OK ? ActivityBody : "{\"error\":\"fixture\"}") };
                }
                Assert.Equal(HttpMethod.Post, request.Method);
                if (path == "/api/desktop/token/renew")
                {
                    RenewRequests++;
                    return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent($"{{\"token\":\"{RenewedToken}\",\"expires_in\":2592000}}") };
                }
                // Anything else - including the retired api/desktop/spotify
                // report - fails the test here: the site keeps no Spotify data.
                Assert.Equal("/api/desktop/revoke", path);
                RevokeRequests++;
                RevokeQueuedAtRevoke = File.Exists(RevokePath);
                return new HttpResponseMessage(RevokeStatus);
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
