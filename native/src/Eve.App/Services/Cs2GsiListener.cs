using System.Net;
using System.Text.Json;
using Eve.Core.Settings;

namespace Eve.App.Services;

// CS2 posts its own game state (kills, deaths, assists, round info) as JSON to a
// local HTTP endpoint once a "Game State Integration" config file is dropped into
// its cfg folder (see Cs2GsiDeployer). This just listens on that endpoint and
// diffs consecutive payloads to detect discrete events - CS2 doesn't send "a kill
// happened", it sends "here is the current state", so a kill is inferred from
// player.state.round_kills going up.
public sealed class Cs2GsiListener : IDisposable
{
    private readonly Func<Cs2AutoClipSettings> _settingsProvider;
    private HttpListener? _listener;
    private CancellationTokenSource? _cts;
    private bool _seeded;
    private int _lastRoundKills;
    private int _lastRoundKillHs;
    private int _lastMatchDeaths;
    private int _lastMatchAssists;
    private int _lastRoundNumber = -1;
    private string _lastMapName = string.Empty;
    private readonly object _killClipLock = new();
    private Timer? _killClipDebounceTimer;
    private string? _pendingKillLabel;
    // 4s clipped real streaks short - a 5.9s gap between a 3rd and 4th kill (still
    // very plausibly the same fight: reposition, reload, re-peek) let the 3K
    // timer fire before the 4th kill even landed, producing two clips instead of
    // one for the Ace.
    private static readonly TimeSpan KillClipDebounce = TimeSpan.FromSeconds(8);

    public event EventHandler<string>? AutoClipTriggered;

    public bool IsListening => _listener?.IsListening == true;

    public Cs2GsiListener(Func<Cs2AutoClipSettings> settingsProvider)
    {
        _settingsProvider = settingsProvider;
    }

    public bool Start(int port)
    {
        Stop();
        var listener = new HttpListener();
        listener.Prefixes.Add($"http://127.0.0.1:{port}/");
        try
        {
            listener.Start();
        }
        catch (Exception error)
        {
            AppLog.Error($"CS2 GSI listener failed to start on port {port}", error);
            return false;
        }

        _listener = listener;
        _cts = new CancellationTokenSource();
        _ = ListenLoopAsync(listener, _cts.Token);
        AppLog.Info($"CS2 GSI listener started on port {port}.");
        return true;
    }

    public void Stop()
    {
        _cts?.Cancel();
        _cts = null;
        try
        {
            _listener?.Stop();
        }
        catch
        {
            // Best effort.
        }

        _listener?.Close();
        _listener = null;
        _seeded = false;
        _lastRoundKills = 0;
        _lastRoundKillHs = 0;
        _lastMatchDeaths = 0;
        _lastMatchAssists = 0;
        _lastRoundNumber = -1;

        lock (_killClipLock)
        {
            _killClipDebounceTimer?.Dispose();
            _killClipDebounceTimer = null;
            _pendingKillLabel = null;
        }
    }

    private async Task ListenLoopAsync(HttpListener listener, CancellationToken token)
    {
        while (!token.IsCancellationRequested && listener.IsListening)
        {
            HttpListenerContext context;
            try
            {
                context = await listener.GetContextAsync();
            }
            catch
            {
                // Thrown when Stop() closes the listener out from under a pending accept.
                break;
            }

            try
            {
                using var reader = new StreamReader(context.Request.InputStream, context.Request.ContentEncoding);
                var body = await reader.ReadToEndAsync(token);
                context.Response.StatusCode = 200;
                context.Response.Close();
                ProcessPayload(body);
            }
            catch (Exception error)
            {
                AppLog.Error("CS2 GSI payload processing failed", error);
                try
                {
                    context.Response.StatusCode = 500;
                    context.Response.Close();
                }
                catch
                {
                    // Best effort.
                }
            }
        }
    }

    private void ProcessPayload(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        if (!root.TryGetProperty("player", out var player)) return;

        if (root.TryGetProperty("map", out var map) &&
            map.TryGetProperty("round", out var roundNumElement) &&
            roundNumElement.TryGetInt32(out var roundNum) &&
            roundNum != _lastRoundNumber)
        {
            AppLog.Info($"CS2 GSI: round changed {_lastRoundNumber} -> {roundNum}, resetting round kill counters.");
            _lastRoundNumber = roundNum;
            _lastRoundKills = 0;
            _lastRoundKillHs = 0;
            // No more kills are coming for the round that just ended - don't make
            // whatever streak was still debouncing wait out the rest of its timer.
            FirePendingKillClip();
        }

        if (root.TryGetProperty("map", out var mapElement) &&
            mapElement.TryGetProperty("name", out var mapNameElement) &&
            mapNameElement.GetString() is { Length: > 0 } mapName)
        {
            _lastMapName = mapName;
        }

        // "player" is whoever CS2 currently has in view - the local user while
        // alive, but a spectated teammate or a killcam's target otherwise. Without
        // this check, a teammate's kill (or the killcam of the person who just
        // killed you) got tracked as if it were the local user's, auto-clipping
        // for kills that weren't theirs. "provider.steamid" is always the actual
        // account running the client, so only process events when they match.
        if (root.TryGetProperty("provider", out var provider) &&
            provider.TryGetProperty("steamid", out var providerSteamIdElement) &&
            player.TryGetProperty("steamid", out var playerSteamIdElement) &&
            providerSteamIdElement.GetString() is { Length: > 0 } providerSteamId &&
            playerSteamIdElement.GetString() is { Length: > 0 } playerSteamId &&
            !string.Equals(providerSteamId, playerSteamId, StringComparison.Ordinal))
        {
            return;
        }

        var state = player.TryGetProperty("state", out var stateElement) ? stateElement : default;
        var matchStats = player.TryGetProperty("match_stats", out var statsElement) ? statsElement : default;

        // The first payload after (re)connecting reflects whatever already
        // happened this session/round - seed the trackers from it instead of
        // treating "already had 2 kills this round" as a brand new event.
        if (!_seeded)
        {
            _seeded = true;
            _lastRoundKills = GetInt(state, "round_kills") ?? 0;
            _lastRoundKillHs = GetInt(state, "round_killhs") ?? 0;
            _lastMatchDeaths = GetInt(matchStats, "deaths") ?? 0;
            _lastMatchAssists = GetInt(matchStats, "assists") ?? 0;
            return;
        }

        var settings = _settingsProvider();
        if (!settings.Enabled) return;

        var roundKills = GetInt(state, "round_kills");
        var roundKillHs = GetInt(state, "round_killhs");
        var matchKills = GetInt(matchStats, "kills");
        if (roundKills.HasValue)
        {
            AppLog.Info($"CS2 GSI: round={_lastRoundNumber}, round_kills={roundKills.Value} (was {_lastRoundKills}), round_killhs={roundKillHs}, match_kills={matchKills}.");
        }

        if (roundKills.HasValue && roundKills.Value > _lastRoundKills)
        {
            var isHeadshotKill = roundKillHs.HasValue && roundKillHs.Value > _lastRoundKillHs;
            var baseLabel = roundKills.Value switch
            {
                1 when settings.Kill => "Kill",
                2 when settings.TwoKill => "2K",
                3 when settings.ThreeKill => "3K",
                4 when settings.FourKill => "4K",
                5 when settings.Ace => "Ace",
                _ => null
            };

            var killLabel = isHeadshotKill && settings.Headshot
                ? (baseLabel is null ? "Headshot" : $"Headshot {baseLabel}")
                : baseLabel;

            // Each kill in a streak (3K -> 4K -> Ace) used to fire its own
            // immediate clip, so a fast multi-kill round produced several
            // duplicate/overlapping saves of essentially the same moment - and
            // queuing them (so none get dropped) meant several full save
            // pipelines running back to back, which is also what was spiking
            // memory. Debounce instead: wait a few seconds after each kill
            // before actually clipping, and if another kill lands before that
            // timer elapses, restart it for the new (higher) label. Only the
            // streak's final milestone actually gets clipped.
            if (killLabel is not null) ScheduleKillClip(killLabel);
        }

        if (roundKills.HasValue) _lastRoundKills = roundKills.Value;
        if (roundKillHs.HasValue) _lastRoundKillHs = roundKillHs.Value;

        string? label = null;

        var deaths = GetInt(matchStats, "deaths");
        if (settings.Death && deaths.HasValue && deaths.Value > _lastMatchDeaths)
        {
            label = "Death";
        }

        if (deaths.HasValue) _lastMatchDeaths = deaths.Value;

        var assists = GetInt(matchStats, "assists");
        if (label is null && settings.Assist && assists.HasValue && assists.Value > _lastMatchAssists)
        {
            label = "Assist";
        }

        if (assists.HasValue) _lastMatchAssists = assists.Value;

        if (label is not null) Fire(label);
    }

    private void ScheduleKillClip(string label)
    {
        lock (_killClipLock)
        {
            _pendingKillLabel = label;
            _killClipDebounceTimer?.Dispose();
            _killClipDebounceTimer = new Timer(_ => FirePendingKillClip(), null, KillClipDebounce, Timeout.InfiniteTimeSpan);
        }
    }

    private void FirePendingKillClip()
    {
        string? label;
        lock (_killClipLock)
        {
            label = _pendingKillLabel;
            _pendingKillLabel = null;
            _killClipDebounceTimer?.Dispose();
            _killClipDebounceTimer = null;
        }

        if (label is not null) Fire(label);
    }

    private void Fire(string label)
    {
        var mapDisplayName = FormatMapName(_lastMapName);
        var title = string.IsNullOrEmpty(mapDisplayName) ? label : $"{label} - {mapDisplayName}";
        AutoClipTriggered?.Invoke(this, title);
    }

    private static int? GetInt(JsonElement parent, string name) =>
        parent.ValueKind == JsonValueKind.Object && parent.TryGetProperty(name, out var element) && element.TryGetInt32(out var value)
            ? value
            : null;

    // CS2's GSI map name is the internal codename ("de_inferno"), not the name
    // players actually call it ("Inferno") - matches how Medal titles its own
    // CS2 auto-clips (e.g. "4K - Inferno").
    private static readonly Dictionary<string, string> KnownMapNames = new(StringComparer.OrdinalIgnoreCase)
    {
        ["de_dust2"] = "Dust II",
        ["de_inferno"] = "Inferno",
        ["de_mirage"] = "Mirage",
        ["de_nuke"] = "Nuke",
        ["de_overpass"] = "Overpass",
        ["de_vertigo"] = "Vertigo",
        ["de_ancient"] = "Ancient",
        ["de_anubis"] = "Anubis",
        ["de_train"] = "Train",
        ["de_cache"] = "Cache",
        ["cs_office"] = "Office",
        ["cs_italy"] = "Italy"
    };

    private static string FormatMapName(string rawMapName)
    {
        if (string.IsNullOrWhiteSpace(rawMapName)) return string.Empty;
        if (KnownMapNames.TryGetValue(rawMapName, out var known)) return known;

        var cleaned = rawMapName;
        foreach (var prefix in new[] { "de_", "cs_", "ar_", "gd_" })
        {
            if (cleaned.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                cleaned = cleaned[prefix.Length..];
                break;
            }
        }

        return cleaned.Length == 0 ? string.Empty : char.ToUpperInvariant(cleaned[0]) + cleaned[1..];
    }

    public void Dispose() => Stop();
}
