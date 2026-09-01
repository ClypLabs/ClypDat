using System.Net;
using System.Text.Json;
using ClypDat.Core.Settings;

namespace ClypDat.App.Services;

public sealed record Cs2AutoClipRequest(string EventId, string EventType, string Title, DateTime StartUtc, DateTime EndUtc);

// Safe to put in a support bundle: this deliberately records stages and counts,
// never a payload, token, Steam ID, player name, map name, or filesystem path.
public sealed record Cs2AutoClipHealthSnapshot(
    bool IsListening,
    int? Port,
    DateTime? StartedUtc,
    DateTime UpdatedUtc,
    bool? ConfigurationDeployed,
    long RequestsReceived,
    long RequestShapeRejected,
    long OversizedRequests,
    long TimedOutRequests,
    long ParseFailures,
    long UnauthorizedPayloads,
    long MissingPlayerPayloads,
    long SteamIdMismatches,
    long AcceptedPayloads,
    long DisabledBySettings,
    long BlockedByMode,
    long PendingEvents,
    long FinalizedEvents,
    long SaveRequests,
    long SavedClips,
    long SaveFailures,
    string LastStage,
    string? LastGate,
    DateTime? LastStageUtc);

// CS2 GSI supplies snapshots rather than discrete events. Keep the round's
// timeline so a 3K can become a 4K/Ace before one precise clip is exported.
public sealed class Cs2GsiListener : IDisposable
{
    private static readonly TimeSpan EventPadding = TimeSpan.FromSeconds(4);
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan PostKillCaptureDuration = TimeSpan.FromSeconds(10);
    private readonly Func<AutoClipGameSettings> _settingsProvider;
    private readonly object _stateLock = new();
    private HttpListener? _listener;
    private CancellationTokenSource? _cts;
    private bool _seeded;
    private string _authToken = string.Empty;
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
    private int? _healthPort;
    private DateTime? _healthStartedUtc;
    private DateTime _healthUpdatedUtc = DateTime.UtcNow;
    private DateTime _healthLastPersistedUtc = DateTime.MinValue;
    private bool? _healthConfigurationDeployed;
    private long _healthRequestsReceived;
    private long _healthRequestShapeRejected;
    private long _healthOversizedRequests;
    private long _healthTimedOutRequests;
    private long _healthParseFailures;
    private long _healthUnauthorizedPayloads;
    private long _healthMissingPlayerPayloads;
    private long _healthSteamIdMismatches;
    private long _healthAcceptedPayloads;
    private long _healthDisabledBySettings;
    private long _healthBlockedByMode;
    private long _healthPendingEvents;
    private long _healthFinalizedEvents;
    private long _healthSaveRequests;
    private long _healthSavedClips;
    private long _healthSaveFailures;
    private string _healthLastStage = "Not started";
    private string? _healthLastGate;
    private DateTime? _healthLastStageUtc;

    public event EventHandler<string>? AutoClipPending;
    public event EventHandler<Cs2AutoClipRequest>? AutoClipReady;

    public bool IsListening => _listener?.IsListening == true;

    public Cs2GsiListener(Func<AutoClipGameSettings> settingsProvider, string? authToken = null)
    {
        _settingsProvider = settingsProvider;
        _authToken = authToken ?? string.Empty;
    }

    public Cs2AutoClipHealthSnapshot GetHealthSnapshot()
    {
        lock (_stateLock) return BuildHealthSnapshotLocked();
    }

    public void SetConfigurationDeploymentResult(bool deployed)
    {
        lock (_stateLock)
        {
            _healthConfigurationDeployed = deployed;
            SetHealthStageLocked(deployed ? "Configuration deployed" : "Configuration deployment failed");
        }
        PersistHealthSnapshot(force: true);
    }

    public void MarkSaveRequested()
    {
        lock (_stateLock)
        {
            _healthSaveRequests++;
            SetHealthStageLocked("Save requested");
        }
        PersistHealthSnapshot(force: true);
    }

    public void MarkSaveCompleted(bool succeeded)
    {
        lock (_stateLock)
        {
            if (succeeded) _healthSavedClips++;
            else _healthSaveFailures++;
            SetHealthStageLocked(succeeded ? "Saved" : "Save failed");
        }
        PersistHealthSnapshot(force: true);
    }

    public bool Start(int port, string authToken)
    {
        Stop();
        _authToken = authToken;
        var listener = new HttpListener();
        listener.Prefixes.Add($"http://127.0.0.1:{port}/");
        try
        {
            listener.Start();
        }
        catch (Exception error)
        {
            lock (_stateLock) SetHealthStageLocked("Listener start failed");
            PersistHealthSnapshot(force: true);
            AppLog.Error($"CS2 GSI listener failed to start on port {port}", error);
            return false;
        }

        _listener = listener;
        _cts = new CancellationTokenSource();
        lock (_stateLock)
        {
            ResetHealthLocked(port);
            SetHealthStageLocked("Listening");
        }
        PersistHealthSnapshot(force: true);
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
            SetHealthStageLocked("Stopped");
        }
        PersistHealthSnapshot(force: true);
    }

    private async Task ListenLoopAsync(HttpListener listener, CancellationToken token)
    {
        while (!token.IsCancellationRequested && listener.IsListening)
        {
            HttpListenerContext context;
            try { context = await listener.GetContextAsync(); }
            catch { break; }

            // Handle off the accept loop: reading a body inline lets one slow client
            // that declares a Content-Length and never sends it block every later
            // request indefinitely.
            _ = Task.Run(async () =>
            {
                try
                {
                    lock (_stateLock)
                    {
                        _healthRequestsReceived++;
                        SetHealthStageLocked("Request received");
                    }
                    if (!GsiAuth.IsRequestShapeValid(context.Request))
                    {
                        lock (_stateLock)
                        {
                            _healthRequestShapeRejected++;
                            SetHealthStageLocked("Request rejected", "Invalid request shape");
                        }
                        PersistHealthSnapshot(force: true);
                        context.Response.StatusCode = 403;
                        context.Response.Close();
                        return;
                    }

                    using var requestTimeout = CancellationTokenSource.CreateLinkedTokenSource(token);
                    requestTimeout.CancelAfter(RequestTimeout);

                    var body = await GsiAuth.ReadBoundedBodyAsync(context.Request, requestTimeout.Token);
                    if (body is null)
                    {
                        lock (_stateLock)
                        {
                            _healthOversizedRequests++;
                            SetHealthStageLocked("Request rejected", "Payload too large");
                        }
                        PersistHealthSnapshot(force: true);
                        context.Response.StatusCode = 413;
                        context.Response.Close();
                        return;
                    }

                    context.Response.StatusCode = 200;
                    context.Response.Close();
                    ProcessPayload(body);
                    PersistHealthSnapshot();
                }
                catch (OperationCanceledException)
                {
                    lock (_stateLock)
                    {
                        _healthTimedOutRequests++;
                        SetHealthStageLocked("Request timed out");
                    }
                    PersistHealthSnapshot(force: true);
                    try { context.Response.Abort(); } catch { }
                }
                catch (Exception error)
                {
                    if (error is JsonException)
                    {
                        lock (_stateLock)
                        {
                            _healthParseFailures++;
                            SetHealthStageLocked("Payload parse failed");
                        }
                    }
                    else
                    {
                        lock (_stateLock) SetHealthStageLocked("Payload processing failed");
                    }
                    PersistHealthSnapshot(force: true);
                    AppLog.Error("CS2 GSI payload processing failed", error);
                    try { context.Response.StatusCode = 500; context.Response.Close(); } catch { }
                }
            }, token);
        }
    }

    internal void ProcessPayload(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        // Before anything else. The old provider/steamid comparison below was the only
        // sender check and was skipped entirely when the payload omitted "provider",
        // so it authenticated nothing.
        if (!GsiAuth.IsPayloadAuthorized(root, _authToken))
        {
            lock (_stateLock)
            {
                _healthUnauthorizedPayloads++;
                SetHealthStageLocked("Payload rejected", "Unauthorized");
            }
            return;
        }

        if (!TryGetObjectProperty(root, "player", out var player))
        {
            lock (_stateLock)
            {
                _healthMissingPlayerPayloads++;
                SetHealthStageLocked("Payload rejected", "Missing player");
            }
            return;
        }

        if (TryGetObjectProperty(root, "provider", out var provider) &&
            TryGetProperty(provider, "steamid", out var providerSteamIdElement) &&
            TryGetProperty(player, "steamid", out var playerSteamIdElement) &&
            providerSteamIdElement.GetString() is { Length: > 0 } providerSteamId &&
            playerSteamIdElement.GetString() is { Length: > 0 } playerSteamId &&
            !string.Equals(providerSteamId, playerSteamId, StringComparison.Ordinal))
        {
            lock (_stateLock)
            {
                _healthSteamIdMismatches++;
                SetHealthStageLocked("Payload rejected", "Player mismatch");
            }
            return;
        }

        var state = TryGetObjectProperty(player, "state", out var stateElement) ? stateElement : default;
        var matchStats = TryGetObjectProperty(player, "match_stats", out var statsElement) ? statsElement : default;
        var mapName = TryGetObjectProperty(root, "map", out var mapElement) && TryGetProperty(mapElement, "name", out var mapNameElement)
            ? mapNameElement.GetString() ?? string.Empty
            : string.Empty;
        var mapMode = TryGetObjectProperty(root, "map", out mapElement) && TryGetProperty(mapElement, "mode", out var mapModeElement)
            ? mapModeElement.GetString() ?? string.Empty
            : string.Empty;
        var roundNumber = TryGetObjectProperty(root, "map", out var map) && TryGetProperty(map, "round", out var roundElement) && roundElement.TryGetInt32(out var parsedRound)
            ? parsedRound
            : (int?)null;
        var roundOver = TryGetObjectProperty(root, "round", out var round) && TryGetProperty(round, "phase", out var phase) &&
                        string.Equals(phase.GetString(), "over", StringComparison.OrdinalIgnoreCase);

        var roundKills = GetInt(state, "round_kills");
        var roundKillHs = GetInt(state, "round_killhs");
        var deaths = GetInt(matchStats, "deaths");
        var assists = GetInt(matchStats, "assists");
        var now = MonotonicClock.UtcNow;

        lock (_stateLock)
        {
            _healthAcceptedPayloads++;
            SetHealthStageLocked("Payload accepted");
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
                SetHealthStageLocked("State seeded");
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
                _healthDisabledBySettings++;
                SetHealthStageLocked("Processing gated", "Game disabled");
                SyncCounters(roundKills, roundKillHs, deaths, assists);
                return;
            }

            if (clippingBlocked)
            {
                _healthBlockedByMode++;
                SetHealthStageLocked("Processing gated", "Unsupported mode");
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
                            _healthPendingEvents++;
                            SetHealthStageLocked("Event pending");
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
        _healthFinalizedEvents++;
        SetHealthStageLocked("Event finalized");
        AppLog.Info($"CS2 auto-clip finalized: {title}, window={startUtc:O}..{endUtc:O}.");
        AutoClipReady?.Invoke(this, new Cs2AutoClipRequest(eventId, _pendingLabel, title, startUtc, endUtc));
        ClearRoundLocked();
    }

    private void FireStandaloneLocked(string label, DateTime timestampUtc)
    {
        _healthPendingEvents++;
        _healthFinalizedEvents++;
        SetHealthStageLocked("Event finalized");
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
        TryGetProperty(parent, name, out var element) && element.TryGetInt32(out var value) ? value : null;

    private static bool TryGetProperty(JsonElement parent, string name, out JsonElement value)
    {
        if (parent.ValueKind == JsonValueKind.Object) return parent.TryGetProperty(name, out value);
        value = default;
        return false;
    }

    private static bool TryGetObjectProperty(JsonElement parent, string name, out JsonElement value) =>
        TryGetProperty(parent, name, out value) && value.ValueKind == JsonValueKind.Object;

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

    private void ResetHealthLocked(int port)
    {
        _healthPort = port;
        _healthStartedUtc = DateTime.UtcNow;
        _healthConfigurationDeployed = null;
        _healthRequestsReceived = _healthRequestShapeRejected = _healthOversizedRequests = _healthTimedOutRequests = 0;
        _healthParseFailures = _healthUnauthorizedPayloads = _healthMissingPlayerPayloads = _healthSteamIdMismatches = 0;
        _healthAcceptedPayloads = _healthDisabledBySettings = _healthBlockedByMode = _healthPendingEvents = _healthFinalizedEvents = 0;
        _healthSaveRequests = _healthSavedClips = _healthSaveFailures = 0;
        _healthLastGate = null;
    }

    private void SetHealthStageLocked(string stage, string? gate = null)
    {
        _healthLastStage = stage;
        _healthLastGate = gate;
        _healthLastStageUtc = DateTime.UtcNow;
        _healthUpdatedUtc = _healthLastStageUtc.Value;
    }

    private Cs2AutoClipHealthSnapshot BuildHealthSnapshotLocked() => new(
        IsListening,
        _healthPort,
        _healthStartedUtc,
        _healthUpdatedUtc,
        _healthConfigurationDeployed,
        _healthRequestsReceived,
        _healthRequestShapeRejected,
        _healthOversizedRequests,
        _healthTimedOutRequests,
        _healthParseFailures,
        _healthUnauthorizedPayloads,
        _healthMissingPlayerPayloads,
        _healthSteamIdMismatches,
        _healthAcceptedPayloads,
        _healthDisabledBySettings,
        _healthBlockedByMode,
        _healthPendingEvents,
        _healthFinalizedEvents,
        _healthSaveRequests,
        _healthSavedClips,
        _healthSaveFailures,
        _healthLastStage,
        _healthLastGate,
        _healthLastStageUtc);

    private void PersistHealthSnapshot(bool force = false)
    {
        Cs2AutoClipHealthSnapshot snapshot;
        lock (_stateLock)
        {
            var now = DateTime.UtcNow;
            if (!force && now - _healthLastPersistedUtc < TimeSpan.FromSeconds(5)) return;
            _healthLastPersistedUtc = now;
            snapshot = BuildHealthSnapshotLocked();
        }

        try
        {
            Directory.CreateDirectory(AppLog.LogFolder);
            var path = Path.Combine(AppLog.LogFolder, "cs2-auto-clip-health.json");
            var temporaryPath = path + ".tmp";
            File.WriteAllText(temporaryPath, JsonSerializer.Serialize(snapshot, new JsonSerializerOptions { WriteIndented = true }));
            File.Move(temporaryPath, path, overwrite: true);
        }
        catch (Exception error)
        {
            AppLog.Error("CS2 auto-clip health snapshot write failed", error);
        }
    }

    public void Dispose() => Stop();
}
