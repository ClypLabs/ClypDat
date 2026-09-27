using System.Buffers.Text;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ClypDat.App.Services;

public enum ClipStatKind { Clip, AutoClip, FullSession }

// Feeds the public counters on clypdat.xyz: clips saved, and seconds of
// gameplay saved. What leaves the PC is a count and a length per kind -
// {"clip":1,"clip_seconds":60} - and nothing else: no clip, file name, game
// or install ID. The site shows totals; the kinds are kept apart there only
// so a split stays available.
//
// A save made while the app is signed in to a ClypDat account also goes with
// that account's desktop token, and the site files it against the account as
// well (a private leaderboard; the site's clip-stats.ts). It still counts in
// the public totals like any other. Which account a save belongs to is fixed
// when it is saved: a save made signed out is never sent with a token, even if
// the app signs in before it goes out, and a signed-in save still waiting when
// the app signs out or switches account is sent without one.
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

    // Seconds are whole numbers, as the site expects. Files written before
    // length tracking have no seconds fields and read back as zero.
    internal sealed class Pending
    {
        public int Clip { get; set; }
        public int AutoClip { get; set; }
        public int FullSession { get; set; }
        public long ClipSeconds { get; set; }
        public long AutoClipSeconds { get; set; }
        public long FullSessionSeconds { get; set; }

        // Saves made while signed in wait here, apart from the ones above, and
        // SignedInAccount says whose they are (AccountKey). Both are null in
        // files from before saves were counted on accounts.
        public Pending? SignedIn { get; set; }
        public string? SignedInAccount { get; set; }

        [JsonIgnore] public bool IsEmpty => Clip <= 0 && AutoClip <= 0 && FullSession <= 0;
    }

    private static HttpClient CreateClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("ClypDat-ClipStats");
        return client;
    }

    /// <summary>
    /// Counts one saved clip, auto-clip or full session, with the gameplay it
    /// holds, and sends it in the background. Seconds of 0 still count the save.
    /// </summary>
    public static void Record(ClipStatKind kind, double seconds)
    {
        // The UI preview build has no recorder; its "saves" are empty stubs.
        if (UiPreviewMode.Enabled) return;
        var whole = double.IsFinite(seconds) && seconds > 0 ? (long)Math.Round(seconds) : 0;
        // Held to the site's per-save caps (MAX_SECONDS_PER_SAVE in the site's
        // clip-stats.ts): one outlier over the cap would get its whole batch
        // rejected and dropped, taking the saves around it with it.
        whole = Math.Min(whole, kind == ClipStatKind.FullSession ? 24 * 60 * 60 : 10 * 60);
        try
        {
            var account = CurrentAccount()?.Key;
            Update(pending => Add(pending, kind, whole, account));
        }
        catch (Exception error)
        {
            // Never allowed to affect saving a clip.
            AppLog.Debug($"Clip stats: could not record {kind}: {error.Message}");
            return;
        }
        _ = FlushAsync();
    }

    /// <summary>Records a saved file, reading its length with ffprobe first. Never throws.</summary>
    public static async Task RecordFileAsync(ClipStatKind kind, string path, MediaProbeService probe)
    {
        double seconds = 0;
        try
        {
            seconds = (await probe.ProbeDurationAsync(path).ConfigureAwait(false)).Duration.TotalSeconds;
        }
        catch (Exception error)
        {
            AppLog.Debug($"Clip stats: could not read clip length: {error.Message}");
        }
        Record(kind, seconds);
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
                var account = CurrentAccount();
                var pending = Read();
                if (pending.SignedIn is not null && pending.SignedInAccount != account?.Key)
                {
                    Update(current => ReleaseSignedIn(current, account?.Key));
                    pending = Read();
                }

                // Signed-in saves go first, while the token they need is here.
                // The account is checked again: another process can sign in
                // between the release above and this read.
                var signedIn = account is { } now && pending.SignedInAccount == now.Key && pending.SignedIn is { IsEmpty: false }
                    ? pending.SignedIn
                    : null;
                var source = signedIn ?? pending;
                if (source.IsEmpty) return;
                var sentAccount = signedIn is null ? null : account!.Value.Key;

                var batch = new Pending
                {
                    Clip = Math.Min(source.Clip, MaxPerRequest),
                    AutoClip = Math.Min(source.AutoClip, MaxPerRequest),
                    FullSession = Math.Min(source.FullSession, MaxPerRequest),
                };
                // A backlog over the cap goes out in parts; each part carries
                // its share of the seconds, so no part claims more gameplay
                // per save than the site allows.
                batch.ClipSeconds = Share(source.ClipSeconds, batch.Clip, source.Clip);
                batch.AutoClipSeconds = Share(source.AutoClipSeconds, batch.AutoClip, source.AutoClip);
                batch.FullSessionSeconds = Share(source.FullSessionSeconds, batch.FullSession, source.FullSession);

                using var request = new HttpRequestMessage(HttpMethod.Post, Endpoint)
                {
                    Content = JsonContent.Create(new Dictionary<string, long>
                    {
                        ["clip"] = batch.Clip,
                        ["clip_seconds"] = batch.ClipSeconds,
                        ["auto_clip"] = batch.AutoClip,
                        ["auto_clip_seconds"] = batch.AutoClipSeconds,
                        ["full_session"] = batch.FullSession,
                        ["full_session_seconds"] = batch.FullSessionSeconds,
                    }),
                };
                if (signedIn is not null) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", account!.Value.Token);
                using var response = await Client.SendAsync(request).ConfigureAwait(false);

                // A 400 means the site will never accept this batch (a contract
                // change); dropping it beats resending it forever. Anything
                // else that isn't success - offline, 429, 5xx - is kept.
                if (!response.IsSuccessStatusCode && (int)response.StatusCode != 400)
                {
                    AppLog.Debug($"Clip stats: send deferred, status={(int)response.StatusCode}.");
                    return;
                }

                Update(current => Sent(current, batch, sentAccount));
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

    /// <summary>Files one save under the account signed in when it was made, or under none.</summary>
    internal static void Add(Pending pending, ClipStatKind kind, long seconds, string? account)
    {
        var target = pending;
        if (account is not null)
        {
            ReleaseSignedIn(pending, account);
            target = pending.SignedIn ??= new Pending();
            pending.SignedInAccount = account;
        }
        switch (kind)
        {
            case ClipStatKind.Clip: target.Clip++; target.ClipSeconds += seconds; break;
            case ClipStatKind.AutoClip: target.AutoClip++; target.AutoClipSeconds += seconds; break;
            case ClipStatKind.FullSession: target.FullSession++; target.FullSessionSeconds += seconds; break;
        }
    }

    /// <summary>
    /// Signed-in saves whose account is no longer the one signed in - signed
    /// out, or another account since - join the saves sent without one: still
    /// counted, just not towards any account.
    /// </summary>
    internal static void ReleaseSignedIn(Pending pending, string? account)
    {
        if (pending.SignedIn is not { } signedIn || pending.SignedInAccount == account) return;
        pending.Clip += signedIn.Clip;
        pending.AutoClip += signedIn.AutoClip;
        pending.FullSession += signedIn.FullSession;
        pending.ClipSeconds += signedIn.ClipSeconds;
        pending.AutoClipSeconds += signedIn.AutoClipSeconds;
        pending.FullSessionSeconds += signedIn.FullSessionSeconds;
        pending.SignedIn = null;
        pending.SignedInAccount = null;
    }

    /// <summary>Takes a sent batch off whichever part of the file now holds it.</summary>
    internal static void Sent(Pending current, Pending batch, string? account)
    {
        // Sent with a token but released since (another process saw the
        // sign-out first): those saves now sit with the ones sent without one.
        var target = account is not null && current.SignedIn is not null && current.SignedInAccount == account
            ? current.SignedIn
            : current;
        target.Clip = Math.Max(0, target.Clip - batch.Clip);
        target.AutoClip = Math.Max(0, target.AutoClip - batch.AutoClip);
        target.FullSession = Math.Max(0, target.FullSession - batch.FullSession);
        target.ClipSeconds = target.Clip == 0 ? 0 : Math.Max(0, target.ClipSeconds - batch.ClipSeconds);
        target.AutoClipSeconds = target.AutoClip == 0 ? 0 : Math.Max(0, target.AutoClipSeconds - batch.AutoClipSeconds);
        target.FullSessionSeconds = target.FullSession == 0 ? 0 : Math.Max(0, target.FullSessionSeconds - batch.FullSessionSeconds);
        if (current.SignedIn is { IsEmpty: true })
        {
            current.SignedIn = null;
            current.SignedInAccount = null;
        }
    }

    private static (string Token, string Key)? CurrentAccount()
    {
        var token = ClypDatAccountActivityService.ReadSavedAccessToken();
        return token is not null && AccountKey(token) is { } key ? (token, key) : null;
    }

    /// <summary>
    /// Which account a desktop token belongs to: a hash of the account id in
    /// its payload, so the pending file, which is not encrypted, never holds
    /// the id itself. The same across renewals of the token. Null for anything
    /// not shaped like the site's tokens.
    /// </summary>
    internal static string? AccountKey(string token)
    {
        try
        {
            using var payload = JsonDocument.Parse(Base64Url.DecodeFromChars(token.Split('.')[0]));
            return payload.RootElement.ValueKind == JsonValueKind.Object
                && payload.RootElement.TryGetProperty("sub", out var subject)
                && subject.ValueKind == JsonValueKind.String
                && subject.GetString() is { Length: > 0 } id
                    ? Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(id)))
                    : null;
        }
        catch (Exception error) when (error is FormatException or JsonException)
        {
            return null;
        }
    }

    private static long Share(long seconds, int part, int whole) =>
        whole <= 0 || part <= 0 ? 0 : part >= whole ? seconds : seconds * part / whole;

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
