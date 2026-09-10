using System.Net.Http.Json;
using System.Text.Json;

namespace ClypDat.App.Services;

public enum ClipStatKind { Clip, AutoClip, FullSession }

// Feeds the public "clips saved" counter on clypdat.xyz. What leaves the PC is
// a number per kind - {"clip":1} - and nothing else: no clip, file name, game,
// account or install ID. The site shows the total of all three; the kinds are
// kept apart there only so a split stays available.
//
// Saves are recorded to a small pending file first and sent from there, so a
// clip saved offline, or while the site is down, is still counted the next
// time a send succeeds. The file is shared by the UI process and the capture
// worker (full sessions finalize in the worker), hence the named mutex around
// every read-modify-write of it.
public static class ClipStatsReporter
{
    // The same host the updater's release mirror uses. CLYPDAT_STATS_URL points
    // a dev build at a local site instead.
    private static readonly string Endpoint =
        Environment.GetEnvironmentVariable("CLYPDAT_STATS_URL") is { Length: > 0 } overrideUrl
            ? overrideUrl
            : "https://www.clypdat.xyz/api/stats/clips";

    // Must match MAX_PER_REQUEST in the site's app/lib/clip-stats.ts; a larger
    // backlog is sent over several requests.
    private const int MaxPerRequest = 100;
    private const string MutexName = @"Local\ClypDat.ClipStats";

    private static string PendingPath => Path.Combine(ClypDat.Core.Settings.AppDataPaths.Root, "clip-stats-pending.json");
    private static readonly SemaphoreSlim FlushGate = new(1, 1);
    private static readonly HttpClient Client = CreateClient();

    private sealed class Pending
    {
        public int Clip { get; set; }
        public int AutoClip { get; set; }
        public int FullSession { get; set; }
        public bool IsEmpty => Clip <= 0 && AutoClip <= 0 && FullSession <= 0;
    }

    private static HttpClient CreateClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("ClypDat-ClipStats");
        return client;
    }

    /// <summary>Counts one saved clip, auto-clip or full session and sends it in the background.</summary>
    public static void Record(ClipStatKind kind)
    {
        // The UI preview build has no recorder; its "saves" are empty stubs.
        if (UiPreviewMode.Enabled) return;
        try
        {
            Update(pending =>
            {
                switch (kind)
                {
                    case ClipStatKind.Clip: pending.Clip++; break;
                    case ClipStatKind.AutoClip: pending.AutoClip++; break;
                    case ClipStatKind.FullSession: pending.FullSession++; break;
                }
            });
        }
        catch (Exception error)
        {
            // Never allowed to affect saving a clip.
            AppLog.Debug($"Clip stats: could not record {kind}: {error.Message}");
            return;
        }
        _ = FlushAsync();
    }

    /// <summary>Sends whatever is pending. Safe to call at any time; a failed send is kept for the next one.</summary>
    public static async Task FlushAsync()
    {
        if (!await FlushGate.WaitAsync(0).ConfigureAwait(false)) return;
        try
        {
            // Bounded, so a site that keeps accepting cannot pin this in a loop.
            for (var round = 0; round < 20; round++)
            {
                var pending = Read();
                if (pending.IsEmpty) return;

                var batch = new Pending
                {
                    Clip = Math.Min(pending.Clip, MaxPerRequest),
                    AutoClip = Math.Min(pending.AutoClip, MaxPerRequest),
                    FullSession = Math.Min(pending.FullSession, MaxPerRequest),
                };
                using var response = await Client.PostAsJsonAsync(Endpoint, new Dictionary<string, int>
                {
                    ["clip"] = batch.Clip,
                    ["auto_clip"] = batch.AutoClip,
                    ["full_session"] = batch.FullSession,
                }).ConfigureAwait(false);

                // A 400 means the site will never accept this batch (a contract
                // change); dropping it beats resending it forever. Anything
                // else that isn't success - offline, 429, 5xx - is kept.
                if (!response.IsSuccessStatusCode && (int)response.StatusCode != 400)
                {
                    AppLog.Debug($"Clip stats: send deferred, status={(int)response.StatusCode}.");
                    return;
                }

                Update(current =>
                {
                    current.Clip = Math.Max(0, current.Clip - batch.Clip);
                    current.AutoClip = Math.Max(0, current.AutoClip - batch.AutoClip);
                    current.FullSession = Math.Max(0, current.FullSession - batch.FullSession);
                });
            }
        }
        catch (Exception error)
        {
            AppLog.Debug($"Clip stats: send deferred: {error.Message}");
        }
        finally
        {
            FlushGate.Release();
        }
    }

    private static Pending Read()
    {
        Pending result = new();
        WithFileLock(() => result = Load());
        return result;
    }

    private static void Update(Action<Pending> change) => WithFileLock(() =>
    {
        var pending = Load();
        change(pending);
        var path = PendingPath;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temp = path + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(pending));
        File.Move(temp, path, overwrite: true);
    });

    private static Pending Load()
    {
        try
        {
            return File.Exists(PendingPath)
                ? JsonSerializer.Deserialize<Pending>(File.ReadAllText(PendingPath)) ?? new Pending()
                : new Pending();
        }
        catch (Exception error) when (error is IOException or JsonException or UnauthorizedAccessException)
        {
            return new Pending();
        }
    }

    private static void WithFileLock(Action action)
    {
        using var mutex = new Mutex(false, MutexName);
        var owned = false;
        try
        {
            try { owned = mutex.WaitOne(TimeSpan.FromSeconds(2)); }
            catch (AbandonedMutexException) { owned = true; }
            if (!owned) throw new TimeoutException("clip stats file is busy");
            action();
        }
        finally
        {
            if (owned) mutex.ReleaseMutex();
        }
    }
}
