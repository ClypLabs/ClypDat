using System.Net;
using System.Text.Json;
using ClypDat.Core.Settings;

namespace ClypDat.App.Services;

public sealed record Cs2AutoClipRequest(string EventId, string EventType, string Title, DateTime StartUtc, DateTime EndUtc);

// CS2 GSI supplies snapshots rather than discrete events. Keep the round's
// timeline so a 3K can become a 4K/Ace before one precise clip is exported.
public sealed class Cs2GsiListener : IDisposable
{
    private static readonly TimeSpan EventPadding = TimeSpan.FromSeconds(4);
    private static readonly TimeSpan PostKillCaptureDuration = TimeSpan.FromSeconds(10);
    private readonly Func<AutoClipGameSettings> _settingsProvider;
    private readonly object _stateLock = new();
    private HttpListener? _listener;
    private CancellationTokenSource? _cts;
    private bool _seeded;
    private int _lastRoundKills;
    private int _lastRoundKillHs;
    private int _lastMatchDeaths;
    private int _lastMatchAssists;
    private int _lastRoundNumber = -1;
    private string _lastMapName = string.Empty;
    private string _lastMapMode = string.Empty;
    private readonly List<DateTime> _roundKillTimes = new();
    private string? _pendingLabel;
    private readonly List<AutoClipEvent> _roundEvents = new();

    public event EventHandler<string>? AutoClipPending;
    public event EventHandler<Cs2AutoClipRequest>? AutoClipReady;

    public bool IsListening => _listener?.IsListening == true;

    public Cs2GsiListener(Func<AutoClipGameSettings> settingsProvider) => _settingsProvider = settingsProvider;

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
        try { _listener?.Stop(); } catch { }
        _listener?.Close();
        _listener = null;
        lock (_stateLock)
        {
            _seeded = false;
            _lastRoundKills = _lastRoundKillHs = _lastMatchDeaths = _lastMatchAssists = 0;
            _lastRoundNumber = -1;
            _lastMapName = string.Empty;
            _lastMapMode = string.Empty;
            ClearRoundLocked();
        }
    }

    private async Task ListenLoopAsync(HttpListener listener, CancellationToken token)
    {
        while (!token.IsCancellationRequested && listener.IsListening)
        {
            HttpListenerContext context;
            try { context = await listener.GetContextAsync(); }
            catch { break; }

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
                try { context.Response.StatusCode = 500; context.Response.Close(); } catch { }
            }
        }
    }

    internal void ProcessPayload(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        if (!root.TryGetProperty("player", out var player)) return;

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
        var mapName = root.TryGetProperty("map", out var mapElement) && mapElement.TryGetProperty("name", out var mapNameElement)
            ? mapNameElement.GetString() ?? string.Empty
            : string.Empty;
        var mapMode = root.TryGetProperty("map", out mapElement) && mapElement.TryGetProperty("mode", out var mapModeElement)
            ? mapModeElement.GetString() ?? string.Empty
            : string.Empty;
        var roundNumber = root.TryGetProperty("map", out var map) && map.TryGetProperty("round", out var roundElement) && roundElement.TryGetInt32(out var parsedRound)
            ? parsedRound
            : (int?)null;
        var roundOver = root.TryGetProperty("round", out var round) && round.TryGetProperty("phase", out var phase) &&
                        string.Equals(phase.GetString(), "over", StringComparison.OrdinalIgnoreCase);

        var roundKills = GetInt(state, "round_kills");
        var roundKillHs = GetInt(state, "round_killhs");
        var deaths = GetInt(matchStats, "deaths");
        var assists = GetInt(matchStats, "assists");
        var now = MonotonicClock.UtcNow;

        lock (_stateLock)
        {
            var settings = _settingsProvider();
            var clippingBlocked = !IsCompetitive(mapMode) && !(IsDeathmatch(mapMode) && settings.DeathmatchClipping);
            var mapChanged = !string.IsNullOrWhiteSpace(mapName) && !string.Equals(mapName, _lastMapName, StringComparison.OrdinalIgnoreCase);
            var modeChanged = !string.IsNullOrWhiteSpace(mapMode) && !string.Equals(mapMode, _lastMapMode, StringComparison.OrdinalIgnoreCase);
            if (mapChanged || modeChanged)
            {
                // If GSI starts sending a non-Competitive mode after an
                // incomplete snapshot, discard that candidate instead of exporting it.
                if (clippingBlocked && string.IsNullOrWhiteSpace(_lastMapMode)) ClearRoundLocked();
                else FinalizePendingLocked();
                _lastMapName = mapName;
                _lastMapMode = mapMode;
                _lastRoundNumber = -1;
                _lastRoundKills = _lastRoundKillHs = 0;
                ClearRoundLocked();
                _seeded = false;
            }

            if (!_seeded)
            {
                _seeded = true;
                _lastRoundKills = roundKills ?? 0;
                _lastRoundKillHs = roundKillHs ?? 0;
                _lastMatchDeaths = deaths ?? 0;
                _lastMatchAssists = assists ?? 0;
                _lastRoundNumber = roundNumber ?? _lastRoundNumber;
                return;
            }

            // A clean new round means the previous candidate cannot improve.
            // GSI normally reports phase=over first, but this also covers servers
            // that skip that payload.
            if (roundNumber.HasValue && _lastRoundNumber >= 0 && roundNumber.Value != _lastRoundNumber && (roundKills ?? 0) == 0)
            {
                FinalizePendingLocked();
                ClearRoundLocked();
                _lastRoundKills = _lastRoundKillHs = 0;
            }
            if (roundNumber.HasValue) _lastRoundNumber = roundNumber.Value;

            if (!settings.Enabled)
            {
                SyncCounters(roundKills, roundKillHs, deaths, assists);
                return;
            }

            if (clippingBlocked)
            {
                ClearRoundLocked();
                SyncCounters(roundKills, roundKillHs, deaths, assists);
                return;
            }

            if (roundKills is { } currentKills && currentKills > _lastRoundKills)
            {
                for (var killNumber = _lastRoundKills + 1; killNumber <= currentKills; killNumber++)
                {
                    _roundKillTimes.Add(now);
                    var label = LabelForKill(killNumber, settings);
                    if (label is not null)
                    {
                        var changed = !string.Equals(_pendingLabel, label, StringComparison.Ordinal);
                        _pendingLabel = label;
                        _roundEvents.Add(new AutoClipEvent(EventIdForLabel(label), label, now, KillPriority(label)));
                        if (changed)
                        {
                            AutoClipPending?.Invoke(this, $"Auto clip started — {label} detected, waiting for the round result.");
                        }
                    }
                    else if (_pendingLabel is not null && IsEnabled(settings, "kill"))
                    {
                        // If only ordinary kills are enabled, retain each one so
                        // Medal-style "⚔️Kill xN" titles stay truthful.
                        _roundEvents.Add(new AutoClipEvent("kill", "Kill", now, 10));
                    }
                }
            }

            if (roundKillHs is { } currentHeadshots && currentHeadshots > _lastRoundKillHs && IsEnabled(settings, "headshot"))
            {
                if (_pendingLabel is null)
                {
                    FireStandaloneLocked("Headshot", now);
                }
                else _roundEvents.Add(new AutoClipEvent("headshot", "Headshot", now, 15));
            }

            if (deaths is { } currentDeaths && currentDeaths > _lastMatchDeaths)
            {
                if (_pendingLabel is not null)
                {
                    if (IsEnabled(settings, "death")) _roundEvents.Add(new AutoClipEvent("death", "Death", now));
                    FinalizePendingLocked();
                }
                else if (IsEnabled(settings, "death")) FireStandaloneLocked("Death", now);
            }

            if (assists is { } currentAssists && currentAssists > _lastMatchAssists && IsEnabled(settings, "assist"))
            {
                if (_pendingLabel is null) FireStandaloneLocked("Assist", now);
                else _roundEvents.Add(new AutoClipEvent("assist", "Assist", now));
            }

            SyncCounters(roundKills, roundKillHs, deaths, assists);
            if (roundOver) FinalizePendingLocked();
        }
    }

    private void SyncCounters(int? roundKills, int? roundKillHs, int? deaths, int? assists)
    {
        if (roundKills.HasValue) _lastRoundKills = roundKills.Value;
        if (roundKillHs.HasValue) _lastRoundKillHs = roundKillHs.Value;
        if (deaths.HasValue) _lastMatchDeaths = deaths.Value;
        if (assists.HasValue) _lastMatchAssists = assists.Value;
    }

    private void FinalizePendingLocked()
    {
        if (_pendingLabel is null || _roundKillTimes.Count == 0) return;
        // Finish ten seconds after final kill. Round-end, death, and assist GSI
        // snapshots can arrive later, but must not extend event's tail.
        var endUtc = _roundKillTimes[^1] + PostKillCaptureDuration;
        var startUtc = _roundKillTimes[0] - EventPadding;
        var eventId = EventIdForLabel(_pendingLabel);
        var title = BuildTitle(_roundEvents.Count == 0 ? new[] { new AutoClipEvent(eventId, _pendingLabel, startUtc, KillPriority(_pendingLabel)) } : _roundEvents);
        AppLog.Info($"CS2 auto-clip finalized: {title}, window={startUtc:O}..{endUtc:O}.");
        AutoClipReady?.Invoke(this, new Cs2AutoClipRequest(eventId, _pendingLabel, title, startUtc, endUtc));
        ClearRoundLocked();
    }

    private void FireStandaloneLocked(string label, DateTime timestampUtc)
    {
        AutoClipPending?.Invoke(this, $"Auto clip started — {label} detected, finishing the clip.");
        var eventId = EventIdForLabel(label);
        AutoClipReady?.Invoke(this, new Cs2AutoClipRequest(eventId, label, BuildTitle(new[] { new AutoClipEvent(eventId, label, timestampUtc, KillPriority(label)) }), timestampUtc - EventPadding, timestampUtc + EventPadding));
    }

    private void ClearRoundLocked()
    {
        _roundKillTimes.Clear();
        _pendingLabel = null;
        _roundEvents.Clear();
    }

    private static string? LabelForKill(int killNumber, AutoClipGameSettings settings) => killNumber switch
    {
        1 when IsEnabled(settings, "kill") => "Kill",
        2 when IsEnabled(settings, "2k") => "2K",
        3 when IsEnabled(settings, "3k") => "3K",
        4 when IsEnabled(settings, "4k") => "4K",
        >= 5 when IsEnabled(settings, "ace") => "Ace",
        _ => null
    };

    private static bool IsEnabled(AutoClipGameSettings settings, string id) => settings.Events.TryGetValue(id, out var enabled) && enabled;
    private static bool IsCompetitive(string mode) => string.Equals(mode, "competitive", StringComparison.OrdinalIgnoreCase);
    private static bool IsDeathmatch(string mode) => string.Equals(mode, "deathmatch", StringComparison.OrdinalIgnoreCase);

    private static string EventIdForLabel(string label) => label switch { "Kill" => "kill", "2K" => "2k", "3K" => "3k", "4K" => "4k", "Ace" => "ace", "Headshot" => "headshot", "Death" => "death", "Assist" => "assist", _ => "kill" };
    private static int KillPriority(string label) => label switch { "Kill" => 10, "2K" => 20, "3K" => 30, "4K" => 40, "Ace" => 50, "Headshot" => 15, _ => 0 };

    private string BuildTitle(IReadOnlyCollection<AutoClipEvent> events)
    {
        var mapDisplayName = FormatMapName(_lastMapName);
        return AutoClipTitleFormatter.Format("cs2", events, mapDisplayName);
    }

    private static int? GetInt(JsonElement parent, string name) =>
        parent.ValueKind == JsonValueKind.Object && parent.TryGetProperty(name, out var element) && element.TryGetInt32(out var value) ? value : null;

    private static readonly Dictionary<string, string> KnownMapNames = new(StringComparer.OrdinalIgnoreCase)
    {
        ["de_dust2"] = "Dust II", ["de_inferno"] = "Inferno", ["de_mirage"] = "Mirage", ["de_nuke"] = "Nuke",
        ["de_overpass"] = "Overpass", ["de_vertigo"] = "Vertigo", ["de_ancient"] = "Ancient", ["de_anubis"] = "Anubis",
        ["de_train"] = "Train", ["de_cache"] = "Cache", ["cs_office"] = "Office", ["cs_italy"] = "Italy"
    };

    private static string FormatMapName(string rawMapName)
    {
        if (string.IsNullOrWhiteSpace(rawMapName)) return string.Empty;
        if (KnownMapNames.TryGetValue(rawMapName, out var known)) return known;
        var cleaned = rawMapName;
        foreach (var prefix in new[] { "de_", "cs_", "ar_", "gd_" })
        {
            if (cleaned.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) { cleaned = cleaned[prefix.Length..]; break; }
        }
        return cleaned.Length == 0 ? string.Empty : char.ToUpperInvariant(cleaned[0]) + cleaned[1..];
    }

    public void Dispose() => Stop();
}
