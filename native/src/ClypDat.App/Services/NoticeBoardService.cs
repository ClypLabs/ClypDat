using System.Net;
using System.Text;

namespace ClypDat.App.Services;

/// <summary>
/// Fetches, verifies and caches the Notice Board feed. Notices are written at
/// www.clypdat.xyz/admin and reach every install without an update - mainly for
/// big features, and for the rare security or severe-bug notice.
///
/// Only a feed that verifies against NoticeSigning's pinned keys is ever used or
/// cached, and the cache is re-verified when read back, so neither the network
/// nor a file on disk can put words in ClypDat's mouth. A failed fetch keeps the
/// last good feed.
/// </summary>
internal static class NoticeBoardService
{
    // www first - installed apps already call it for everything else. api is the
    // same deployment under a second name, used only if www does not answer.
    private static readonly string[] FeedUrls =
    {
        "https://www.clypdat.xyz/api/notices",
        "https://api.clypdat.xyz/v1/notices",
    };
    private const string CacheFileName = "notice-board.json";
    private static string CachePath => Path.Combine(ClypDat.Core.Settings.AppDataPaths.Root, CacheFileName);

    private static readonly object Gate = new();
    private static NoticeFeedPolicy? _current;
    private static bool _cacheLoaded;

    public static NoticeFeed? Current
    {
        get
        {
            EnsureCacheLoaded();
            lock (Gate) return _current is { } policy ? new NoticeFeed(policy.IssuedAt, policy.Notices) : null;
        }
    }

    public static event EventHandler? PolicyChanged;
    public static NoticeFeedPolicy? CurrentPolicy { get { EnsureCacheLoaded(); lock (Gate) return _current; } }
    public static IReadOnlyList<KillSwitch> ActiveSwitches => NoticeBoardRules.ActiveSwitches(CurrentPolicy, AppUpdateService.CurrentVersion, DateTimeOffset.UtcNow);
    public static bool IsBlocked(string control, string? target = null) => NoticeBoardRules.IsBlocked(ActiveSwitches, control, target);

    private static void EnsureCacheLoaded()
    {
        lock (Gate)
        {
            if (_cacheLoaded) return;
            _cacheLoaded = true;
            try
            {
                if (!File.Exists(CachePath)) return;
                _current = NoticeBoardRules.ParsePolicy(File.ReadAllText(CachePath), NoticeSigning.PinnedPublicKeys);
            }
            catch (Exception error)
            {
                AppLog.Error("Notice board: cached feed did not verify; ignoring it", error);
            }
        }
    }

    /// <summary>
    /// Fetches the feed. Returns true when a newer verified feed replaced the one held.
    /// Never throws; failures are logged and leave the current feed in place.
    /// </summary>
    public static async Task<bool> RefreshAsync(CancellationToken cancellationToken = default)
    {
        EnsureCacheLoaded();
        foreach (var url in FeedUrls)
        {
            try
            {
                var json = await FetchAsync(url, cancellationToken);
                if (json is null) continue;
                var feed = NoticeBoardRules.ParsePolicy(json, NoticeSigning.PinnedPublicKeys);
                lock (Gate)
                {
                    if (!NoticeBoardRules.ShouldReplace(_current, feed))
                    {
                        AppLog.Info($"Notice board: ignored a feed issued {feed.IssuedAt:O}, older than the one held ({_current!.IssuedAt:O}).");
                        return false;
                    }
                    // The site re-signs the same content whenever its cache
                    // rebuilds, so a new issuedAt alone is not news. Only a new
                    // revision (bumped on every notice or switch write) is.
                    var revised = _current is null || feed.Revision != _current.Revision;
                    var reissued = revised || feed.IssuedAt != _current!.IssuedAt;
                    _current = feed;
                    if (reissued)
                    {
                        Directory.CreateDirectory(Path.GetDirectoryName(CachePath)!);
                        File.WriteAllText(CachePath, json);
                    }
                    if (revised)
                    {
                        AppLog.Info($"Notice board: verified feed, {feed.Notices.Count} notice(s), {feed.Switches.Count} switch(es), revision {feed.Revision}.");
                        PolicyChanged?.Invoke(null, EventArgs.Empty);
                    }
                    return revised;
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return false;
            }
            catch (Exception error)
            {
                // A feed that fails verification is not retried from the other
                // host - both are the same deployment - but a network failure is.
                AppLog.Error($"Notice board: refresh from {url} failed (non-fatal)", error);
                if (error is System.Security.Cryptography.CryptographicException or InvalidDataException) return false;
            }
        }
        return false;
    }

    private static async Task<string?> FetchAsync(string url, CancellationToken cancellationToken)
    {
        using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(10), MaxResponseContentBufferSize = NoticeBoardRules.MaxEnvelopeBytes };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("ClypDat-NoticeBoard");
        using var response = await client.GetAsync(url, cancellationToken);
        if (response.StatusCode is HttpStatusCode.ServiceUnavailable or HttpStatusCode.NotFound)
        {
            AppLog.Info($"Notice board: {url} answered {(int)response.StatusCode}.");
            return null;
        }
        response.EnsureSuccessStatusCode();
        var bytes = await response.Content.ReadAsByteArrayAsync(cancellationToken);
        return Encoding.UTF8.GetString(bytes);
    }
}
