using System.Net;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;
using ClypDat.App.Services;
using ClypDat.App.ViewModels;
using ClypDat.Core.Settings;
using Xunit;

namespace ClypDat.App.Tests;

public sealed class Cs2MatchHistoryStatusTests
{
    private sealed class Handler : HttpMessageHandler
    {
        public int Requests;
        public TaskCompletionSource<HttpResponseMessage>? Pending;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref Requests);
            return Pending?.Task ?? Task.FromResult(Rejection());
        }
        public static HttpResponseMessage Rejection() => new((HttpStatusCode)422)
        {
            Content = new StringContent("""{"code":"steam_rejected","reason":"match_code"}""", Encoding.UTF8, "application/json")
        };
    }

    private sealed class Fixture : IDisposable
    {
        private readonly string _previous = AppDataPaths.ProductFolderName;
        private readonly string _root;
        public Handler Http { get; } = new();
        public Cs2MatchHistoryService Service { get; }

        public Fixture()
        {
            AppDataPaths.ConfigureProductFolder("ClypDat-Cs2StatusTest-" + Guid.NewGuid().ToString("N"));
            _root = AppDataPaths.Root;
            Cs2MatchHistoryLinkStore.Save(new()
            {
                SteamId = "76561198000000001", AuthCode = "ABCD-EFGH2-JKLM",
                KnownCode = "CSGO-L9spZ-ihuov-cyhtE-kxbqa-FkBAA"
            });
            Service = NewService();
        }

        public Cs2MatchHistoryService NewService() => new(_ => Task.FromResult("fixture-token"), () => null,
            () => false, (_, _) => false, new Cs2MatchHistoryClient(Http, new Uri("https://fixture.test/")));

        public void Dispose()
        {
            Service.Dispose();
            AppDataPaths.ConfigureProductFolder(_previous);
            Directory.Delete(_root, recursive: true);
        }
    }

    private static void Field(object target, string name, object? value) => target.GetType()
        .GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(target, value);

    private static (MainWindowViewModel Model, ClypDatAccountActivityService Account) Model(Cs2MatchHistoryService service)
    {
        var model = (MainWindowViewModel)RuntimeHelpers.GetUninitializedObject(typeof(MainWindowViewModel));
        var account = new ClypDatAccountActivityService();
        var tokenType = typeof(ClypDatAccountActivityService).GetNestedType("DesktopToken", BindingFlags.NonPublic)!;
        Field(account, "_token", Activator.CreateInstance(tokenType, "fixture-token", DateTimeOffset.UtcNow.AddDays(1)));
        Field(model, "_clypDatAccount", account);
        Field(model, "_cs2MatchHistory", service);
        return (model, account);
    }

    private static void Settings(MainWindowViewModel model, bool visible) => typeof(MainWindowViewModel)
        .GetProperty(nameof(MainWindowViewModel.IsSettingsVisible))!.SetValue(model, visible);

    [Fact]
    public async Task ClearingWarningPreservesCredentialsAndStopsRejectedCodeRetries()
    {
        using var fixture = new Fixture();
        await fixture.Service.SyncAsync(force: true, CancellationToken.None);
        Assert.Contains("Steam didn't accept", fixture.Service.Status);
        var before = File.ReadAllBytes(Cs2MatchHistoryLinkStore.DefaultPath);
        var statusChanges = 0;
        fixture.Service.StatusChanged += (_, _) => statusChanges++;

        fixture.Service.ClearStatus();
        fixture.Service.ClearStatus();
        fixture.Service.RetryNow();
        await fixture.Service.SyncAsync(force: true, CancellationToken.None);

        Assert.Equal(string.Empty, fixture.Service.Status);
        Assert.Equal(1, statusChanges);
        Assert.Equal(1, fixture.Http.Requests);
        Assert.Equal(before, File.ReadAllBytes(Cs2MatchHistoryLinkStore.DefaultPath));
        Assert.True(fixture.Service.Link!.NeedsAttention);
        Assert.Equal("match_code", fixture.Service.Link.RejectedField);
        Assert.False(fixture.Service.CanRetry);
    }

    [Fact]
    public void RestartDoesNotRecreateWarningFromPersistedRejection()
    {
        AvaloniaTestThread.Run(() =>
        {
            using var fixture = new Fixture();
            fixture.Service.SyncAsync(force: true, CancellationToken.None).GetAwaiter().GetResult();
            using var restarted = fixture.NewService();
            var (model, account) = Model(restarted);
            using (account)
            {
                Assert.True(model.Cs2MatchHistoryNeedsAttention);
                Assert.True(model.IsCs2MatchHistoryFormVisible);
                Assert.Equal(string.Empty, model.Cs2MatchHistoryStatus);
                Assert.False(model.HasCs2MatchHistoryStatus);
                restarted.RetryNow();
                Assert.Equal(1, fixture.Http.Requests);
            }
        }, TimeSpan.FromSeconds(15), "Restart warning test did not finish.");
    }

    [Fact]
    public void SettingsCloseAndReopenClearWarningIncludingLateResponse()
    {
        AvaloniaTestThread.Run(() =>
        {
            using var fixture = new Fixture();
            var (model, account) = Model(fixture.Service);
            using (account)
            {
                Settings(model, true);
                fixture.Service.SyncAsync(force: true, CancellationToken.None).GetAwaiter().GetResult();
                Assert.True(model.HasCs2MatchHistoryStatus);
                Settings(model, false);
                Assert.False(model.HasCs2MatchHistoryStatus);

                // Simulate another check whose HTTP answer arrives only
                // after Settings has closed. Opening clears that old answer.
                var link = fixture.Service.Link!;
                link.NeedsAttention = false;
                Cs2MatchHistoryLinkStore.Save(link);
                Settings(model, true);
                fixture.Http.Pending = new(TaskCreationOptions.RunContinuationsAsynchronously);
                var pending = fixture.Service.SyncAsync(force: true, CancellationToken.None);
                Settings(model, false);
                fixture.Http.Pending.SetResult(Handler.Rejection());
                pending.GetAwaiter().GetResult();
                Assert.True(fixture.Service.Link!.NeedsAttention);
                Assert.NotEmpty(fixture.Service.Status);

                var notifications = new List<string?>();
                model.PropertyChanged += (_, args) => notifications.Add(args.PropertyName);
                Settings(model, true);
                Assert.Equal(string.Empty, model.Cs2MatchHistoryStatus);
                Assert.False(model.HasCs2MatchHistoryStatus);
                Assert.Contains(nameof(model.Cs2MatchHistoryStatus), notifications);
                Assert.Contains(nameof(model.HasCs2MatchHistoryStatus), notifications);
                Assert.True(fixture.Service.Link.NeedsAttention);
                Assert.Equal(2, fixture.Http.Requests);
            }
        }, TimeSpan.FromSeconds(15), "Settings warning test did not finish.");
    }
}
