using ClypDat.App.Services;
using ClypDat.App.ViewModels;
using Xunit;

namespace ClypDat.App.Tests;

// A library card that is not hydrated yet (0:00, no streams) is opened by
// probing it on the click, whatever the library sweep is doing - it defers
// itself while a game runs - and the reasons a card cannot open are kept
// apart: missing metadata, an unreadable file, a recording still being
// written, and the Spotify step.
public sealed class ClipMetadataHydrationTests
{
    private static readonly MediaTrackInfo[] Tracks = [new(0, "video", "h264", "Video"), new(1, "audio", "aac", "Game")];

    private static string NewPath() => Path.Combine(Path.GetTempPath(), $"clypdat-hydration-{Guid.NewGuid()}.mp4");

    private static MediaFileInfo Stub(string path) => new(Path.GetFileNameWithoutExtension(path), path, DateTimeOffset.UtcNow, TimeSpan.Zero, 1000, "", [], 0, 0, 0);

    private static MediaFileInfo Probed(string path) => Stub(path) with { Duration = TimeSpan.FromSeconds(30), Tracks = Tracks, Width = 2560, Height = 1440, Fps = 90 };

    private static ClipCardViewModel Card(string path, bool hydrated = false) =>
        new(hydrated ? Probed(path) : Stub(path), Path.GetDirectoryName(path)!);

    // A scripted ffprobe: each call takes the next answer, and every call is counted.
    private sealed class FakeProbe
    {
        private readonly Queue<Func<string, bool, Task<MediaMetadataProbe>>> _answers = new();
        public int Calls;
        public readonly List<(bool Foreground, bool BypassCache)> Requests = [];

        public FakeProbe Then(Func<string, bool, Task<MediaMetadataProbe>> answer) { _answers.Enqueue(answer); return this; }
        public FakeProbe ThenReadable() => Then((path, _) => Task.FromResult(new MediaMetadataProbe(Probed(path), null, false)));
        public FakeProbe ThenUnreadable(string error = "ffprobe exited with code 1: moov atom not found") =>
            Then((path, _) => Task.FromResult(new MediaMetadataProbe(Stub(path), error, false)));

        public Task<MediaMetadataProbe> Run(string path, bool foreground, bool bypassCache)
        {
            Interlocked.Increment(ref Calls);
            lock (Requests) Requests.Add((foreground, bypassCache));
            return _answers.Dequeue()(path, bypassCache);
        }
    }

    private static ClipMetadataHydrator Hydrator(FakeProbe probe) =>
        new(probe.Run, recordingActive: _ => false, spotifyProcessing: _ => false);

    [Fact]
    public async Task UnhydratedCardClicked_IsProbedAndOpensWithoutASecondClick()
    {
        var path = NewPath();
        var probe = new FakeProbe().ThenReadable();
        var flow = new ClipOpenFlow(Hydrator(probe));
        var card = Card(path);
        var messages = new List<string>();

        var opened = await flow.ResolveAsync(card, messages.Add);

        Assert.NotNull(opened);
        Assert.Equal(TimeSpan.FromSeconds(30), opened.Duration);
        Assert.True(card.IsOpenable);
        Assert.Equal(ClipMetadataState.Ready, card.MetadataState);
        Assert.Equal([ClipOpenMessages.Loading], messages);
        Assert.Equal([(true, false)], probe.Requests); // Foreground, cache allowed.
        Assert.Equal(string.Empty, card.BusyOverlayText); // The tile's loading overlay came back off.
    }

    [Fact]
    public async Task ClickedCardOpensWithNoLibrarySweepRunning()
    {
        // While a game runs, HydrateLibraryClipsAsync returns without probing
        // anything. The click path does not go through it at all: nothing
        // but the clicked clip's own probe runs, and the card opens.
        var path = NewPath();
        var probe = new FakeProbe().ThenReadable();
        var flow = new ClipOpenFlow(Hydrator(probe));
        var card = Card(path);

        Assert.False(EditorForegroundWork.IsActive);
        var pending = flow.ResolveAsync(card, _ => { });
        var opened = await pending;

        Assert.NotNull(opened);
        Assert.Equal(1, probe.Calls);
        Assert.False(EditorForegroundWork.IsActive); // The scope that parked background work was released.
    }

    [Fact]
    public async Task HydratedCard_OpensWithoutAnyProbe()
    {
        var probe = new FakeProbe();
        var flow = new ClipOpenFlow(Hydrator(probe));
        var card = Card(NewPath(), hydrated: true);
        var messages = new List<string>();

        Assert.NotNull(await flow.ResolveAsync(card, messages.Add));
        Assert.Equal(0, probe.Calls);
        Assert.Empty(messages);
    }

    [Fact]
    public async Task UnreadableClip_IsMarkedFailed_NotLeftLoading()
    {
        var probe = new FakeProbe().ThenUnreadable();
        var flow = new ClipOpenFlow(Hydrator(probe));
        var card = Card(NewPath());
        var messages = new List<string>();

        Assert.Null(await flow.ResolveAsync(card, messages.Add));

        Assert.Equal(ClipMetadataState.Failed, card.MetadataState);
        Assert.Equal(ClipOpenBlocker.MetadataFailed, card.OpenBlocker);
        Assert.Contains("moov atom", card.MetadataError); // Detail kept for the log...
        Assert.Equal([ClipOpenMessages.Loading, ClipOpenMessages.Failed], messages); // ...not shown on the tile.
        Assert.Equal(string.Empty, card.BusyOverlayText);
    }

    [Fact]
    public async Task TransientFailure_TheNextClickProbesAgainAndOpens()
    {
        var probe = new FakeProbe().ThenUnreadable("ffprobe exited with code 1: Permission denied").ThenReadable();
        var flow = new ClipOpenFlow(Hydrator(probe));
        var card = Card(NewPath());

        Assert.Null(await flow.ResolveAsync(card, _ => { }));
        Assert.Equal(ClipMetadataState.Failed, card.MetadataState);

        Assert.NotNull(await flow.ResolveAsync(card, _ => { }));
        Assert.Equal(ClipMetadataState.Ready, card.MetadataState);
        Assert.Null(card.MetadataError);
        Assert.Equal(2, probe.Calls);
    }

    [Fact]
    public async Task RecordingStillBeingWritten_IsNotProbed()
    {
        var path = NewPath();
        File.WriteAllBytes(path, [0]);
        try
        {
            using var recording = RecordingFileOwnership.Acquire(path);
            var probe = new FakeProbe();
            var flow = new ClipOpenFlow(new ClipMetadataHydrator(probe.Run, spotifyProcessing: _ => false));
            var card = Card(path);
            var messages = new List<string>();

            Assert.Equal(ClipOpenBlocker.Finalizing, card.OpenBlocker);
            Assert.Null(await flow.ResolveAsync(card, messages.Add));
            Assert.Equal([ClipOpenMessages.Finalizing], messages);
            Assert.Equal(0, probe.Calls);

            // The same holds for a background caller.
            var background = await new ClipMetadataHydrator(probe.Run, spotifyProcessing: _ => false).EnsureAsync(path, foreground: false);
            Assert.Equal(ClipOpenBlocker.Finalizing, background.Blocker);
            Assert.Equal(0, probe.Calls);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task SpotifyProcessingClip_SaysSoAndIsNotProbed()
    {
        var path = NewPath();
        SpotifyProcessingPaths.Reserve(path);
        try
        {
            var probe = new FakeProbe();
            var flow = new ClipOpenFlow(new ClipMetadataHydrator(probe.Run, recordingActive: _ => false));
            var card = Card(path);
            var messages = new List<string>();

            Assert.Equal(ClipOpenBlocker.SpotifyProcessing, card.OpenBlocker);
            Assert.Null(await flow.ResolveAsync(card, messages.Add));
            Assert.Equal([ClipOpenMessages.SpotifyProcessing], messages);
            Assert.Equal(0, probe.Calls);
        }
        finally
        {
            SpotifyProcessingPaths.Finish(path, SpotifyOverlayOutcome.Skipped);
        }
    }

    [Fact]
    public async Task SpotifyWriter_CanProbeItsOwnClip_OthersWait()
    {
        // Post-save processing hydrates the clip from inside the Spotify
        // writer's scope; the reservation only keeps other callers out.
        var path = NewPath();
        var writerProbe = new FakeProbe().ThenReadable();
        var otherProbe = new FakeProbe();
        var writer = new ClipMetadataHydrator(writerProbe.Run, recordingActive: _ => false);
        var other = new ClipMetadataHydrator(otherProbe.Run, recordingActive: _ => false);
        var finish = new TaskCompletionSource();
        var hydrated = new TaskCompletionSource<ClipMetadataResult>();
        var job = new SpotifyPostSaveCoordinator().RunAsync(path, Guid.NewGuid().ToString(), false, () => Task.CompletedTask, async _ =>
        {
            hydrated.SetResult(await writer.EnsureAsync(path, foreground: false));
            await finish.Task;
            return SpotifyOverlayOutcome.Completed;
        });

        Assert.True((await hydrated.Task).IsReady);
        Assert.Equal(ClipOpenBlocker.SpotifyProcessing, (await other.EnsureAsync(path, foreground: true)).Blocker);
        Assert.Equal(0, otherProbe.Calls);
        finish.SetResult();
        await job;
    }

    public static TheoryData<string, int, bool, bool> SaveOrigins => new()
    {
        // origin, Spotify step outcome, Spotify step throws, releasing readers throws
        { "worker hotkey save, Spotify disabled", (int)SpotifyOverlayOutcome.Skipped, false, false },
        { "worker hotkey save, Spotify on but nothing playing", (int)SpotifyOverlayOutcome.Skipped, false, false },
        { "UI-owned save, Spotify playing", (int)SpotifyOverlayOutcome.Completed, false, false },
        { "UI-owned save, Spotify step failed", (int)SpotifyOverlayOutcome.Failed, false, false },
        { "auto-clip save, Spotify step threw", (int)SpotifyOverlayOutcome.Failed, true, false },
        { "recovered save, readers could not be released", (int)SpotifyOverlayOutcome.Skipped, false, true },
    };

    [Theory]
    [MemberData(nameof(SaveOrigins))]
    public async Task EverySaveEndsWithAnOpenableCard(string origin, int spotify, bool spotifyThrows, bool releaseThrows)
    {
        // As ProcessSavedClipAsync: the stub card goes in straight away, the
        // Spotify step runs through the coordinator, and PostSaveMetadata
        // hydrates the card once that job is over, however it ended. Here the
        // Spotify step never reaches the library update at all.
        _ = origin;
        var path = NewPath();
        var card = Card(path);
        var probe = new FakeProbe().ThenReadable();
        var hydrator = Hydrator(probe);
        var coordinator = new SpotifyPostSaveCoordinator();
        var job = coordinator.RunAsync(path, Guid.NewGuid().ToString(), false,
            () => releaseThrows ? throw new IOException("playback would not unload") : Task.CompletedTask,
            _ => spotifyThrows ? throw new HttpRequestException("cover art unavailable") : Task.FromResult((SpotifyOverlayOutcome)spotify));

        await PostSaveMetadata.EnsureAfterAsync(job, () => card.IsHydrated,
            async () => card.ApplyMetadata(await hydrator.EnsureAsync(path, foreground: false), reloadSidecars: false));

        Assert.True(card.IsOpenable);
        Assert.Equal(1, probe.Calls);
        Assert.False(SpotifyProcessingPaths.IsProcessing(path));
    }

    [Fact]
    public async Task DuplicateSaveCompletion_StillEndsWithAnOpenableCard()
    {
        // A second completion for the same save joins the first job without
        // running its body again.
        var path = NewPath();
        var saveId = Guid.NewGuid().ToString();
        var card = Card(path);
        var probe = new FakeProbe().ThenReadable();
        var hydrator = Hydrator(probe);
        var coordinator = new SpotifyPostSaveCoordinator();
        var first = coordinator.RunAsync(path, saveId, false, () => Task.CompletedTask, _ => Task.FromResult(SpotifyOverlayOutcome.Skipped));
        var duplicate = coordinator.RunAsync(NewPath(), saveId, false, () => Task.CompletedTask, _ => throw new InvalidOperationException("must not run"));
        Assert.Same(first, duplicate);

        await PostSaveMetadata.EnsureAfterAsync(duplicate, () => card.IsHydrated,
            async () => card.ApplyMetadata(await hydrator.EnsureAsync(path, foreground: false), reloadSidecars: false));

        Assert.True(card.IsOpenable);
    }

    [Fact]
    public async Task HydratedByThePostSaveJob_IsNotProbedAgain()
    {
        var path = NewPath();
        var card = Card(path);
        var probe = new FakeProbe().ThenReadable();
        var hydrator = Hydrator(probe);
        var job = new SpotifyPostSaveCoordinator().RunAsync(path, Guid.NewGuid().ToString(), false, () => Task.CompletedTask, async _ =>
        {
            card.ApplyMetadata(await hydrator.EnsureAsync(path, foreground: false), reloadSidecars: false);
            return SpotifyOverlayOutcome.Skipped;
        });

        await PostSaveMetadata.EnsureAfterAsync(job, () => card.IsHydrated,
            async () => card.ApplyMetadata(await hydrator.EnsureAsync(path, foreground: false), reloadSidecars: false));

        Assert.True(card.IsOpenable);
        Assert.Equal(1, probe.Calls);
    }

    [Fact]
    public async Task ConcurrentSweepAndClick_ShareOneProbe()
    {
        var path = NewPath();
        var release = new TaskCompletionSource();
        var probe = new FakeProbe().Then(async (p, _) =>
        {
            await release.Task;
            return new MediaMetadataProbe(Probed(p), null, false);
        });
        var hydrator = Hydrator(probe);
        var flow = new ClipOpenFlow(hydrator);
        var card = Card(path);

        var sweep = hydrator.EnsureAsync(path, foreground: false, new CancellationTokenSource().Token);
        var click = flow.ResolveAsync(card, _ => { });
        var watcher = hydrator.EnsureAsync(path.ToUpperInvariant(), foreground: false);
        release.SetResult();

        Assert.NotNull(await click);
        Assert.True((await sweep).IsReady);
        Assert.True((await watcher).IsReady);
        Assert.Equal(1, probe.Calls);
    }

    [Fact]
    public async Task SweepGivingUp_DoesNotCancelTheProbeAClickJoined()
    {
        var path = NewPath();
        var release = new TaskCompletionSource();
        var probe = new FakeProbe().Then(async (p, _) => { await release.Task; return new MediaMetadataProbe(Probed(p), null, false); });
        var hydrator = Hydrator(probe);
        var flow = new ClipOpenFlow(hydrator);
        using var sweepCancelled = new CancellationTokenSource();

        var sweep = hydrator.EnsureAsync(path, foreground: false, sweepCancelled.Token);
        var click = flow.ResolveAsync(Card(path), _ => { });
        sweepCancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => sweep);
        release.SetResult();

        Assert.NotNull(await click);
        Assert.Equal(1, probe.Calls);
    }

    [Fact]
    public async Task RepeatedClicksWhileLoading_OpenOnce()
    {
        var path = NewPath();
        var release = new TaskCompletionSource();
        var probe = new FakeProbe().Then(async (p, _) => { await release.Task; return new MediaMetadataProbe(Probed(p), null, false); });
        var flow = new ClipOpenFlow(Hydrator(probe));
        var card = Card(path);

        var first = flow.ResolveAsync(card, _ => { });
        var second = flow.ResolveAsync(card, _ => { });
        Assert.True(second.IsCompleted);
        Assert.Null(await second);
        release.SetResult();

        Assert.NotNull(await first);
        Assert.Equal(1, probe.Calls);
    }

    [Fact]
    public async Task AnotherCardClickedWhileLoading_Wins()
    {
        var slowPath = NewPath();
        var release = new TaskCompletionSource();
        var probe = new FakeProbe().Then(async (p, _) => { await release.Task; return new MediaMetadataProbe(Probed(p), null, false); });
        var flow = new ClipOpenFlow(Hydrator(probe));
        var slow = Card(slowPath);

        var pending = flow.ResolveAsync(slow, _ => { });
        Assert.NotNull(await flow.ResolveAsync(Card(NewPath(), hydrated: true), _ => { }));
        release.SetResult();

        Assert.Null(await pending); // Hydrated, but the user has moved on.
        Assert.True(slow.IsOpenable);
    }

    [Fact]
    public async Task CachedAnswerThatCannotOpen_IsReadAgainFromTheFile()
    {
        var path = NewPath();
        var probe = new FakeProbe()
            .Then((p, _) => Task.FromResult(new MediaMetadataProbe(Stub(p), null, true)))
            .ThenReadable();
        var flow = new ClipOpenFlow(Hydrator(probe));
        var card = Card(path);

        Assert.NotNull(await flow.ResolveAsync(card, _ => { }));
        Assert.Equal([(true, false), (true, true)], probe.Requests);
    }

    [Fact]
    public async Task StaleZeroDurationLibraryCache_IsRepairedByTheClickAndStaysRepaired()
    {
        // The library cache saved every card at 0:00 (as a build with other
        // probe-cache rules left it); the clip itself is fine.
        SQLitePCL.raw.SetProvider(new SQLitePCL.SQLite3Provider_winsqlite3());
        var root = Path.Combine(Path.GetTempPath(), $"clypdat-library-cache-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        var database = Path.Combine(root, "library-cache.db");
        var path = Path.Combine(root, "Fortnite - Sep-26-2026 - 14-35-07.mp4");
        try
        {
            var store = new LibraryCacheStore(database);
            store.Save(root, [new CachedClipState(Stub(path), null, null)]);
            var restored = new ClipCardViewModel(Assert.Single(store.Load(root)), root);
            Assert.False(restored.IsOpenable);
            Assert.Equal(ClipOpenBlocker.MetadataMissing, restored.OpenBlocker);

            var probe = new FakeProbe().ThenReadable();
            Assert.NotNull(await new ClipOpenFlow(Hydrator(probe)).ResolveAsync(restored, _ => { }));

            store.Save(root, [restored.ToCachedState()]);
            var next = new ClipCardViewModel(Assert.Single(store.Load(root)), root);
            Assert.True(next.IsOpenable);
            Assert.Equal(TimeSpan.FromSeconds(30), next.Duration);
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public async Task RealProbe_ReadsAClipAndExplainsAnUnreadableOne()
    {
        FfmpegPathResolver.EnsureBundledFfmpeg();
        Assert.True(FfmpegPathResolver.IsAvailable);
        var folder = Path.Combine(Path.GetTempPath(), $"clypdat-hydration-{Guid.NewGuid()}");
        Directory.CreateDirectory(folder);
        try
        {
            var good = Path.Combine(folder, "good.mp4");
            var broken = Path.Combine(folder, "broken.mp4");
            await SpotifyOverlayBurnerTests.Run("-f", "lavfi", "-i", "color=black:s=160x90:r=30:d=1", "-f", "lavfi", "-i", "anullsrc=r=48000:cl=stereo",
                "-t", "1", "-c:v", "libx264", "-c:a", "aac", good);
            File.WriteAllBytes(broken, new byte[4096]);
            var service = new MediaProbeService(Path.Combine(folder, "media-cache")); // Not the real app cache.
            var hydrator = new ClipMetadataHydrator(service.ProbeMetadataDetailedAsync);

            var goodCard = Card(good);
            Assert.NotNull(await new ClipOpenFlow(hydrator).ResolveAsync(goodCard, _ => { }));
            Assert.True(goodCard.Duration > TimeSpan.Zero);
            Assert.Contains(goodCard.Media.Tracks, track => track.Type == "audio");

            var brokenCard = Card(broken);
            Assert.Null(await new ClipOpenFlow(hydrator).ResolveAsync(brokenCard, _ => { }));
            Assert.Equal(ClipMetadataState.Failed, brokenCard.MetadataState);
            Assert.Contains("ffprobe", brokenCard.MetadataError);
            // Nothing cached for the broken file: fixing it on disk is picked up.
            Assert.False((await service.ProbeMetadataDetailedAsync(broken)).FromCache);
        }
        finally
        {
            Directory.Delete(folder, true);
        }
    }
}
