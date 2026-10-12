namespace ClypDat.App.Services;

// Keeps linked CS2 match history flowing into match stats: after each match,
// and on start-up for any missed, it asks clypdat.xyz for the share codes
// newer than the last one handled, looks each match up, and turns its demo
// into a full scoreboard. Never more than one run at a time; demos are only
// downloaded for matches something in the library was recorded during, and
// only unpacked and parsed while no game is running.
internal sealed class Cs2MatchHistoryService : IDisposable
{
    private static readonly TimeSpan StartupCheckInterval = TimeSpan.FromHours(6);
    private static readonly TimeSpan MinimumGap = TimeSpan.FromMinutes(2);
    private const int MaximumPages = 10;
    private const int MaximumCodesPerRun = 40;

    private readonly Cs2MatchHistoryClient _client;
    private readonly Func<CancellationToken, Task<string>> _getToken;
    private readonly Func<MatchStatsStore?> _store;
    private readonly Func<bool> _isGameRunning;
    private readonly Func<DateTime, DateTime, bool> _hasCs2MediaBetween;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly CancellationTokenSource _lifetime = new();
    private CancellationTokenSource? _scheduled;
    private DateTime _lastRunUtc = DateTime.MinValue;
    private string _status = string.Empty;

    public event EventHandler? StatusChanged;

    public Cs2MatchHistoryService(Func<CancellationToken, Task<string>> getToken, Func<MatchStatsStore?> store,
        Func<bool> isGameRunning, Func<DateTime, DateTime, bool> hasCs2MediaBetween, Cs2MatchHistoryClient? client = null)
    {
        _getToken = getToken;
        _store = store;
        _isGameRunning = isGameRunning;
        _hasCs2MediaBetween = hasCs2MediaBetween;
        _client = client ?? new Cs2MatchHistoryClient();
    }

    /// <summary>Progress or trouble, worded for Settings. Empty when there is nothing to say.</summary>
    public string Status => _status;

    /// <summary>Dismisses the current Settings message without changing the saved link or retry pause.</summary>
    public void ClearStatus() => SetStatus(string.Empty);

    public Cs2MatchHistoryLink? Link => Cs2MatchHistoryLinkStore.Load();

    public void Start()
    {
        _ = Task.Run(Cs2DemoDownloader.CleanStale);
        var link = Link;
        if (link is null || link.NeedsAttention) return;
        if (link.LastCheckedUtc is not { } last || DateTime.UtcNow - last > StartupCheckInterval || link.StartCode is not null)
            Schedule(TimeSpan.FromMinutes(2));
    }

    /// <summary>Saves a new link (all three values already validated) and starts on it.</summary>
    public void SaveLink(string steamId, string authCode, string startCode)
    {
        Cs2MatchHistoryLinkStore.Save(new Cs2MatchHistoryLink { SteamId = steamId, AuthCode = authCode, KnownCode = startCode, StartCode = startCode });
        AppLog.Info($"CS2 match history linked (steam {Cs2MatchHistoryFormats.Fingerprint(steamId)}).");
        SetStatus("Linked. Looking for your matches…");
        Schedule(TimeSpan.FromSeconds(2), force: true);
    }

    /// <summary>The user asked to check now: drop any back-off and run straight away.</summary>
    public void RetryNow()
    {
        if (Link is not { NeedsAttention: false } link) return;
        link.RetryAfterUtc = null;
        Save(link);
        SetStatus("Looking for your matches…");
        Schedule(TimeSpan.Zero, force: true);
    }

    /// <summary>Waiting out a back-off, so a manual retry makes sense.</summary>
    public bool CanRetry => Link is { NeedsAttention: false, RetryAfterUtc: not null };

    public void Unlink()
    {
        CancelScheduled();
        Cs2MatchHistoryLinkStore.Delete();
        AppLog.Info("CS2 match history unlinked.");
        SetStatus(string.Empty);
    }

    /// <summary>A GSI match just finished: keep it, and look for its demo once Valve has the share code.</summary>
    public void OnLiveMatchCompleted(MatchStats match)
    {
        _ = Task.Run(() =>
        {
            try { _store()?.Save(match); }
            catch (Exception error) { AppLog.Error("Match stats could not be saved", error); }
        });
        if (Link is { NeedsAttention: false }) Schedule(TimeSpan.FromMinutes(4), force: true);
    }

    public void Schedule(TimeSpan delay, bool force = false)
    {
        CancelScheduled();
        var scheduled = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        _scheduled = scheduled;
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(delay, scheduled.Token).ConfigureAwait(false);
                await SyncAsync(force, scheduled.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { }
            catch (Exception error) { AppLog.Error("CS2 match history sync failed", error); }
        });
    }

    private void CancelScheduled()
    {
        var previous = Interlocked.Exchange(ref _scheduled, null);
        try { previous?.Cancel(); } catch (ObjectDisposedException) { }
    }

    internal async Task SyncAsync(bool force, CancellationToken cancellationToken)
    {
        if (!await _gate.WaitAsync(0, cancellationToken).ConfigureAwait(false)) return;
        try
        {
            if (!force && DateTime.UtcNow - _lastRunUtc < MinimumGap) return;
            _lastRunUtc = DateTime.UtcNow;
            var link = Link;
            if (link is null) return;
            if (link.NeedsAttention) return;
            if (link.RetryAfterUtc is { } retry && retry > DateTime.UtcNow)
            {
                Schedule(retry - DateTime.UtcNow);
                return;
            }

            string token;
            try { token = await _getToken(cancellationToken).ConfigureAwait(false); }
            catch (InvalidOperationException) { SetStatus("Sign in to ClypDat to download full match stats."); return; }

            try
            {
                await RunAsync(link, token, cancellationToken).ConfigureAwait(false);
                link.LastCheckedUtc = DateTime.UtcNow;
                link.RetryAfterUtc = null;
                Save(link);
                SetStatus(string.Empty);
            }
            catch (Cs2MatchHistoryException error)
            {
                HandleFailure(link, error);
            }
            catch (Exception error) when (error is HttpRequestException or TimeoutException or IOException && !cancellationToken.IsCancellationRequested)
            {
                AppLog.Info($"CS2 match history: network trouble ({error.GetType().Name}); trying again in 30 minutes.");
                link.RetryAfterUtc = DateTime.UtcNow.AddMinutes(30);
                Save(link);
                SetStatus("Couldn't reach Steam's match history. ClypDat will try again later.");
                Schedule(TimeSpan.FromMinutes(30));
            }
        }
        finally { _gate.Release(); }
    }

    private const string AttentionStatus = "Steam didn't accept your authentication code or match code. Enter them again to keep getting match stats.";

    /// <summary>What to tell the user when Steam refused the codes, naming the value when it's known.</summary>
    internal static string AttentionMessage(string? rejectedField) => rejectedField switch
    {
        "auth_code" => "Steam didn't accept the authentication code for this Steam ID. Check the Steam ID and code both come from the account you play CS2 on, then enter them again.",
        "match_code" => "Steam didn't accept this match code. Use a match from within the last month, played on the same account as the Steam ID. Older saved demos can still be imported locally.",
        _ => AttentionStatus
    };

    private void HandleFailure(Cs2MatchHistoryLink link, Cs2MatchHistoryException error)
    {
        AppLog.Info($"CS2 match history: {error.Failure}.");
        switch (error.Failure)
        {
            case Cs2MatchHistoryFailure.SteamRejected:
            case Cs2MatchHistoryFailure.InvalidInput:
                // No automatic retries: the same codes would be refused again.
                link.NeedsAttention = true;
                link.RejectedField = error.RejectedField;
                Save(link);
                SetStatus(AttentionMessage(link.RejectedField));
                break;
            case Cs2MatchHistoryFailure.SignedOut:
                SetStatus("Sign in to ClypDat to download full match stats.");
                break;
            default:
                var wait = error.RetryAfter is { } after
                    ? TimeSpan.FromSeconds(Math.Clamp(after.TotalSeconds, 60, 6 * 3600))
                    : error.Failure switch
                    {
                        Cs2MatchHistoryFailure.RateLimited => TimeSpan.FromMinutes(15),
                        Cs2MatchHistoryFailure.NotConfigured => TimeSpan.FromHours(6),
                        _ => TimeSpan.FromMinutes(30)
                    };
                link.RetryAfterUtc = DateTime.UtcNow + wait;
                Save(link);
                SetStatus(error.Failure switch
                {
                    Cs2MatchHistoryFailure.NotConfigured => "Full match stats aren't available from ClypDat's server yet. Your codes are saved, and ClypDat will check again in a few hours.",
                    Cs2MatchHistoryFailure.RateLimited => "Too many match history requests right now. ClypDat will try again in a few minutes.",
                    _ => "Couldn't reach Steam's match history. ClypDat will try again in about half an hour."
                });
                Schedule(wait);
                break;
        }
    }

    private async Task RunAsync(Cs2MatchHistoryLink link, string token, CancellationToken cancellationToken)
    {
        var handled = 0;
        if (link.StartCode is { } start)
        {
            await ProcessAsync(link, token, start, cancellationToken).ConfigureAwait(false);
            link.StartCode = null;
            Save(link);
            handled++;
        }

        for (var page = 0; page < MaximumPages && handled < MaximumCodesPerRun; page++)
        {
            var result = await _client.GetShareCodesAsync(token, link.SteamId, link.AuthCode, link.KnownCode, cancellationToken).ConfigureAwait(false);
            var fresh = result.Codes.Where(code => code != link.KnownCode).ToList();
            foreach (var code in fresh)
            {
                if (handled++ >= MaximumCodesPerRun) return;
                await ProcessAsync(link, token, code, cancellationToken).ConfigureAwait(false);
                // Only now: a code that failed part-way is looked at again next time.
                link.KnownCode = code;
                Save(link);
            }
            if (result.Complete || fresh.Count == 0) return;
        }
    }

    // Returns once the code is handled for good (stats saved, demo expired,
    // match unknown, demo unreadable). Throws for anything worth retrying.
    private async Task ProcessAsync(Cs2MatchHistoryLink link, string token, string code, CancellationToken cancellationToken)
    {
        var tag = Cs2MatchHistoryFormats.Fingerprint(code);
        var info = await _client.GetMatchAsync(token, code, cancellationToken).ConfigureAwait(false);
        if (info is null) { AppLog.Info($"CS2 match history: match {tag} is unknown to Steam; skipped."); return; }

        var store = _store();
        var known = store?.LoadAll() ?? [];
        if (info.MatchId.Length > 0 && known.Any(match => match.MatchId == info.MatchId && match.HasDemoData)) return;
        var live = FindLiveCandidate(known, info);
        if (string.IsNullOrEmpty(info.DemoUrl))
        {
            MarkDemo(store, live, "expired", tag);
            return;
        }
        if (!Cs2DemoDownloader.IsAllowedUrl(info.DemoUrl))
        {
            AppLog.Info($"CS2 match history: match {tag} came with a replay address that isn't Valve's; not downloaded.");
            MarkDemo(store, live, "failed", tag);
            return;
        }
        // Only matches something was recorded in are worth ~100 MB each.
        if (live is null && (info.MatchTime is not { } time || !_hasCs2MediaBetween(time.UtcDateTime - TimeSpan.FromMinutes(30), time.UtcDateTime + TimeSpan.FromHours(3))))
        {
            AppLog.Info($"CS2 match history: nothing recorded during match {tag}; demo not downloaded.");
            return;
        }

        await WaitForIdleAsync(cancellationToken).ConfigureAwait(false);
        SetStatus("Downloading a match demo…");
        var download = await Cs2DemoDownloader.DownloadAsync(info.DemoUrl, cancellationToken).ConfigureAwait(false);
        if (download.Outcome != Cs2DemoDownloadOutcome.Ready)
        {
            AppLog.Info($"CS2 match history: demo for match {tag}: {download.Outcome}.");
            MarkDemo(store, live, download.Outcome == Cs2DemoDownloadOutcome.Expired ? "expired" : "failed", tag);
            return;
        }

        await WaitForIdleAsync(cancellationToken).ConfigureAwait(false);
        SetStatus("Reading a match demo…");
        MatchStats parsed;
        string? demoPath = null;
        try
        {
            parsed = await RunLowPriority(() =>
            {
                var unpacked = Cs2DemoDownloader.Unpack(download.DemoPath!, cancellationToken);
                if (unpacked.Outcome != Cs2DemoDownloadOutcome.Ready) return null;
                demoPath = unpacked.DemoPath;
                return Cs2DemoStatsParser.ParseAsync(demoPath!, link.SteamId, cancellationToken).GetAwaiter().GetResult();
            }).ConfigureAwait(false) ?? throw new InvalidDataException("The demo was larger than ClypDat reads.");
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            // A malformed or unsupported demo will not parse next time either.
            AppLog.Info($"CS2 match history: demo for match {tag} could not be read ({error.GetType().Name}).");
            MarkDemo(store, live, "failed", tag);
            return;
        }
        finally
        {
            Cs2DemoDownloader.TryDelete(download.DemoPath);
            Cs2DemoDownloader.TryDelete(demoPath);
            MemoryTrimmer.RequestTrim("match demo parsed");
        }

        if (store is null || parsed.Rounds.Count == 0) return;
        parsed = parsed with { MatchId = info.MatchId, Map = parsed.Map ?? info.Map };
        // The live candidate was picked by time and map; the rounds decide.
        if (live is not null && MatchClipMapping.IsSameMatch(live, parsed, info.MatchTime))
        {
            store.Save(MatchClipMapping.Merge(live, parsed));
        }
        else if (info.MatchTime is { } matchTime)
        {
            var start = matchTime.UtcDateTime;
            store.Save(parsed with
            {
                Id = $"cs2-demo-{new string(info.MatchId.Where(char.IsDigit).Take(20).ToArray())}",
                StartedUtc = start,
                EndedUtc = start.AddSeconds(parsed.DemoDurationSeconds ?? 0)
            });
        }
        AppLog.Info($"CS2 match history: stats saved for match {tag}.");
    }

    internal static MatchStats? FindLiveCandidate(IEnumerable<MatchStats> matches, Cs2MatchInfo info)
    {
        if (info.MatchTime is not { } time) return null;
        var utc = time.UtcDateTime;
        return matches
            .Where(match => match.HasLiveData && !match.HasDemoData && match.Game == "cs2")
            .Where(match => info.Map is null || match.Map is null || string.Equals(match.Map, info.Map, StringComparison.OrdinalIgnoreCase))
            .Where(match => utc >= match.StartedUtc - TimeSpan.FromHours(3) && utc <= match.EndedUtc + TimeSpan.FromHours(3))
            .OrderBy(match => Math.Abs((match.StartedUtc - utc).TotalSeconds))
            .FirstOrDefault();
    }

    private static void MarkDemo(MatchStatsStore? store, MatchStats? live, string status, string tag)
    {
        if (store is null || live is null) return;
        try { store.Save(live with { DemoStatus = status }); }
        catch (Exception error) { AppLog.Error($"Match stats could not be updated for match {tag}", error); }
    }

    private async Task WaitForIdleAsync(CancellationToken cancellationToken)
    {
        var said = false;
        while (_isGameRunning())
        {
            if (!said) { SetStatus("Waiting for your game to close before reading match demos."); said = true; }
            await Task.Delay(TimeSpan.FromSeconds(30), cancellationToken).ConfigureAwait(false);
        }
    }

    // Unpacking and parsing are seconds of solid CPU. A below-normal thread
    // keeps them out of the way of anything the user is doing.
    private static Task<T?> RunLowPriority<T>(Func<T?> work) where T : class
    {
        var completion = new TaskCompletionSource<T?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try { completion.SetResult(work()); }
            catch (Exception error) { completion.SetException(error); }
        })
        { IsBackground = true, Priority = ThreadPriority.BelowNormal, Name = "CS2 demo reader" };
        thread.Start();
        return completion.Task;
    }

    private static void Save(Cs2MatchHistoryLink link)
    {
        // Unlinked or re-linked meanwhile: do not bring the old link back.
        if (Cs2MatchHistoryLinkStore.Load() is not { } current || current.SteamId != link.SteamId || current.AuthCode != link.AuthCode) return;
        Cs2MatchHistoryLinkStore.Save(link);
    }

    private void SetStatus(string status)
    {
        if (_status == status) return;
        _status = status;
        StatusChanged?.Invoke(this, EventArgs.Empty);
    }

    public void Dispose()
    {
        CancelScheduled();
        _lifetime.Cancel();
        _lifetime.Dispose();
    }
}
