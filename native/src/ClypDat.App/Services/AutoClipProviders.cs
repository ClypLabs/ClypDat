using System.Net;
using System.Net.Http;
using System.Text.Json;
using ClypDat.Core.Settings;

namespace ClypDat.App.Services;

public sealed record AutoClipRequest(string GameId, string GameName, string EventId, string EventType, string Title, DateTime StartUtc, DateTime EndUtc, int Priority = 0);

public sealed class DotaGsiListener : IDisposable
{
    private static readonly TimeSpan Padding = TimeSpan.FromSeconds(4);
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan MultiKillQuietWindow = TimeSpan.FromSeconds(18);
    private readonly Func<AutoClipGameSettings> _settings;
    private readonly object _gate = new();
    private HttpListener? _listener;
    private CancellationTokenSource? _cts;
    private bool _seeded;
    private string _authToken = string.Empty;
    private int _kills, _deaths, _assists;
    private readonly List<DateTime> _killTimes = new();
    private readonly List<AutoClipEvent> _pendingEvents = new();
    private string? _pendingLabel;
    private DateTime _lastKillUtc;
    private DateTime _lastPlayEventUtc;
    private bool _hadAegis;
    private DateTime _lastRoshanUtc;

    public DotaGsiListener(Func<AutoClipGameSettings> settings) => _settings = settings;
    public event EventHandler<string>? AutoClipPending;
    public event EventHandler<AutoClipRequest>? AutoClipReady;
    public bool IsListening => _listener?.IsListening == true;

    public bool Start(int port, string authToken)
    {
        Stop();
        _authToken = authToken;
        var listener = new HttpListener();
        listener.Prefixes.Add($"http://127.0.0.1:{port}/");
        try { listener.Start(); }
        catch (Exception error) { AppLog.Error("Dota GSI listener failed to start", error); return false; }
        _listener = listener; _cts = new CancellationTokenSource(); _ = ListenAsync(listener, _cts.Token); return true;
    }

    public void Stop()
    {
        _cts?.Cancel(); _cts = null;
        try { _listener?.Stop(); } catch { }
        _listener?.Close(); _listener = null;
        lock (_gate) { _seeded = false; _kills = _deaths = _assists = 0; _killTimes.Clear(); _pendingEvents.Clear(); _pendingLabel = null; _hadAegis = false; }
    }

    private async Task ListenAsync(HttpListener listener, CancellationToken token)
    {
        while (!token.IsCancellationRequested && listener.IsListening)
        {
            HttpListenerContext context;
            // GetContextAsync sat inside the try with no break, so a persistently
            // failing accept spun a hot logging loop instead of ending the listener.
            try { context = await listener.GetContextAsync(); }
            catch { break; }

            _ = Task.Run(async () =>
            {
                try
                {
                    if (!GsiAuth.IsRequestShapeValid(context.Request))
                    {
                        context.Response.StatusCode = 403; context.Response.Close(); return;
                    }

                    using var requestTimeout = CancellationTokenSource.CreateLinkedTokenSource(token);
                    requestTimeout.CancelAfter(RequestTimeout);

                    var body = await GsiAuth.ReadBoundedBodyAsync(context.Request, requestTimeout.Token);
                    if (body is null)
                    {
                        context.Response.StatusCode = 413; context.Response.Close(); return;
                    }

                    context.Response.StatusCode = 200; context.Response.Close(); Process(body);
                }
                catch (OperationCanceledException) { try { context.Response.Abort(); } catch { } }
                catch (Exception error) { AppLog.Error("Dota GSI payload processing failed", error); }
            }, token);
        }
    }

    private void Process(string json)
    {
        using var doc = JsonDocument.Parse(json); var root = doc.RootElement;
        // This listener had no sender check of any kind before the token.
        if (!GsiAuth.IsPayloadAuthorized(root, _authToken)) return;
        if (!root.TryGetProperty("player", out var player)) return;
        var now = MonotonicClock.UtcNow;
        var kills = GetInt(player, "kills");
        var deaths = GetInt(player, "deaths");
        var assists = GetInt(player, "assists");
        var hasAegis = ContainsAegis(root);
        lock (_gate)
        {
            if (!_seeded) { _seeded = true; _kills = kills ?? 0; _deaths = deaths ?? 0; _assists = assists ?? 0; _hadAegis = hasAegis; return; }
            var settings = _settings(); if (!settings.Enabled) { Sync(kills, deaths, assists); _hadAegis = hasAegis; return; }
            if (kills is { } currentKills && currentKills > _kills)
            {
                for (var index = _kills + 1; index <= currentKills; index++)
                {
                    _killTimes.Add(now); _lastKillUtc = now;
                    var chainCount = _killTimes.Count;
                    var label = chainCount switch { 1 => "Kill", 2 => "Double Kill", 3 => "Triple Kill", 4 => "Ultra Kill", _ => "Rampage" };
                    var id = chainCount switch { 1 => "kill", 2 => "double", 3 => "triple", 4 => "ultra", _ => "rampage" };
                    if (IsEnabled(settings, id))
                    {
                        _pendingLabel = label;
                        _lastPlayEventUtc = now;
                        _pendingEvents.Add(new AutoClipEvent(id, label, now, KillPriority(chainCount)));
                        AutoClipPending?.Invoke(this, $"Auto clip started — {label} detected, waiting for the play to finish.");
                    }
                    else if (_pendingLabel is not null && IsEnabled(settings, "kill"))
                    {
                        _pendingEvents.Add(new AutoClipEvent("kill", "Kill", now, 10));
                        _lastPlayEventUtc = now;
                    }
                }
            }
            if (deaths is { } currentDeaths && currentDeaths > _deaths)
            {
                if (_pendingLabel is not null)
                {
                    if (IsEnabled(settings, "death")) { _pendingEvents.Add(new AutoClipEvent("death", "Death", now)); _lastPlayEventUtc = now; }
                    Finalize(now);
                }
                else if (IsEnabled(settings, "death")) Fire("death", "Death", now);
            }
            if (assists is { } currentAssists && currentAssists > _assists && IsEnabled(settings, "assist"))
            {
                if (_pendingLabel is null) Fire("assist", "Assist", now);
                else { _pendingEvents.Add(new AutoClipEvent("assist", "Assist", now)); _lastPlayEventUtc = now; }
            }
            if (!_hadAegis && hasAegis)
            {
                var snatched = root.GetRawText().Contains("snatched", StringComparison.OrdinalIgnoreCase);
                var id = snatched ? "aegis-snatched" : "aegis-picked"; var label = snatched ? "Aegis Snatched" : "Aegis Picked Up";
                if (IsEnabled(settings, id))
                {
                    if (_pendingLabel is null) Fire(id, label, now);
                    else { _pendingEvents.Add(new AutoClipEvent(id, label, now, snatched ? 45 : 35)); _lastPlayEventUtc = now; }
                }
            }
            if (root.TryGetProperty("roshan", out _)) _lastRoshanUtc = now;
            Sync(kills, deaths, assists); _hadAegis = hasAegis;
            if (_pendingLabel is not null && now - _lastKillUtc >= MultiKillQuietWindow) Finalize(now);
        }
    }

    private void Finalize(DateTime now)
    {
        if (_pendingLabel is null || _killTimes.Count == 0) return;
        var label = _pendingLabel; var start = _killTimes[0] - Padding; var end = _lastKillUtc + Padding;
        var eventId = label switch { "Kill" => "kill", "Double Kill" => "double", "Triple Kill" => "triple", "Ultra Kill" => "ultra", _ => "rampage" };
        IReadOnlyCollection<AutoClipEvent> events = _pendingEvents.Count == 0
            ? new[] { new AutoClipEvent(eventId, label, _lastKillUtc, KillPriority(_killTimes.Count)) }
            : _pendingEvents;
        end = _lastPlayEventUtc > DateTime.MinValue ? _lastPlayEventUtc + Padding : end;
        AutoClipReady?.Invoke(this, new AutoClipRequest("dota2", "Dota 2", eventId, label, AutoClipTitleFormatter.Format("dota2", events), start, end, _killTimes.Count));
        _killTimes.Clear(); _pendingEvents.Clear(); _pendingLabel = null; _lastPlayEventUtc = default;
    }
    private void Fire(string id, string label, DateTime now) { AutoClipPending?.Invoke(this, $"Auto clip started — {label} detected, finishing the clip."); var events = new[] { new AutoClipEvent(id, label, now) }; AutoClipReady?.Invoke(this, new AutoClipRequest("dota2", "Dota 2", id, label, AutoClipTitleFormatter.Format("dota2", events), now - Padding, now + Padding)); }
    private void Sync(int? kills, int? deaths, int? assists) { if (kills.HasValue) _kills = kills.Value; if (deaths.HasValue) _deaths = deaths.Value; if (assists.HasValue) _assists = assists.Value; }
    private static bool IsEnabled(AutoClipGameSettings settings, string id) => settings.Events.TryGetValue(id, out var enabled) && enabled;
    private static int KillPriority(int chainCount) => chainCount switch { 1 => 10, 2 => 20, 3 => 30, 4 => 40, _ => 50 };
    private static int? GetInt(JsonElement parent, string name) => parent.TryGetProperty(name, out var element) && element.TryGetInt32(out var value) ? value : null;
    private static bool ContainsAegis(JsonElement root) => root.GetRawText().Contains("item_aegis", StringComparison.OrdinalIgnoreCase);
    public void Dispose() => Stop();
}

public sealed class LeagueAutoClipListener : IDisposable
{
    private static readonly TimeSpan Padding = TimeSpan.FromSeconds(4);
    private static readonly TimeSpan PlayQuietWindow = TimeSpan.FromSeconds(4);
    private static readonly TimeSpan MaxPlayWindow = TimeSpan.FromSeconds(18);
    private readonly Func<AutoClipGameSettings> _settings;
    private readonly HttpClient _client;
    private CancellationTokenSource? _cts;
    private readonly HashSet<long> _seenEventIds = new();
    private readonly object _playGate = new();
    private readonly List<AutoClipEvent> _pendingPlayEvents = new();
    private CancellationTokenSource? _playFlushCts;
    private DateTime _firstPlayUtc;
    private DateTime _lastPlayUtc;
    public LeagueAutoClipListener(Func<AutoClipGameSettings> settings)
    {
        _settings = settings;
        _client = new HttpClient(new HttpClientHandler { ServerCertificateCustomValidationCallback = (request, _, _, _) => request.RequestUri?.Host is "127.0.0.1" or "localhost" }) { BaseAddress = new Uri("https://127.0.0.1:2999/"), Timeout = TimeSpan.FromSeconds(1) };
    }
    public event EventHandler<string>? AutoClipPending;
    public event EventHandler<AutoClipRequest>? AutoClipReady;
    public bool IsListening => _cts is not null;
    public void Start() { if (_cts is not null) return; _cts = new CancellationTokenSource(); _ = PollAsync(_cts.Token); }
    public void Stop()
    {
        _cts?.Cancel(); _cts?.Dispose(); _cts = null; _seenEventIds.Clear();
        lock (_playGate) { _playFlushCts?.Cancel(); _playFlushCts?.Dispose(); _playFlushCts = null; _pendingPlayEvents.Clear(); _firstPlayUtc = _lastPlayUtc = default; }
    }
    private async Task PollAsync(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            try
            {
                var playerName = await _client.GetStringAsync("liveclientdata/activeplayername", token);
                var json = await _client.GetStringAsync("liveclientdata/eventdata", token); Process(json, playerName.Trim());
            }
            catch (OperationCanceledException) { break; }
            catch { /* League is simply not in a live match yet. */ }
            try { await Task.Delay(500, token); } catch (OperationCanceledException) { break; }
        }
    }
    private void Process(string json, string playerName)
    {
        using var doc = JsonDocument.Parse(json); if (!doc.RootElement.TryGetProperty("Events", out var events)) return;
        var settings = _settings(); if (!settings.Enabled) return;
        foreach (var item in events.EnumerateArray())
        {
            if (!item.TryGetProperty("EventID", out var idElement) || !idElement.TryGetInt64(out var eventId) || !_seenEventIds.Add(eventId)) continue;
            var name = item.TryGetProperty("EventName", out var nameElement) ? nameElement.GetString() : null;
            var isKiller = Matches(item, "KillerName", playerName);
            var isVictim = Matches(item, "VictimName", playerName);
            var isAssist = IsAssister(item, playerName);
            var isParticipant = isKiller || isAssist;
            var (id, label, priority) = name switch
            {
                "Multikill" when isKiller => MultiKill(item),
                "ChampionKill" when isKiller => ("kill", "Enemy Slain", 10),
                "ChampionKill" when isVictim => ("death", "Player Slain", 5),
                "ChampionKill" when isAssist => ("assist", "Assist", 5),
                "DragonKill" or "BaronKill" or "HeraldKill" when isParticipant => StealOrKill(item, name![..^4].ToLowerInvariant()),
                "HordeKill" when isParticipant => StealOrKill(item, "voidgrub"),
                "TurretKilled" when isParticipant => ("turret", "Turret Destroyed", 25), "InhibKilled" when isParticipant => ("inhibitor", "Inhibitor Destroyed", 30),
                "Ace" when Matches(item, "Acer", playerName) => ("ace", "Ace", 45), _ => (string.Empty, string.Empty, 0)
            };
            if (string.IsNullOrEmpty(id) || !settings.Events.TryGetValue(id, out var enabled) || !enabled) continue;
            var now = MonotonicClock.UtcNow;
            AutoClipPending?.Invoke(this, $"Auto clip started — {label} detected, finishing the clip.");
            QueuePlayEvent(new AutoClipEvent(id, label, now, priority));
        }
    }

    private void QueuePlayEvent(AutoClipEvent item)
    {
        AutoClipRequest? completed = null;
        lock (_playGate)
        {
            if (_pendingPlayEvents.Count > 0 && item.OccurredUtc - _firstPlayUtc >= MaxPlayWindow)
                completed = TakePendingPlayLocked();

            if (_pendingPlayEvents.Count == 0) _firstPlayUtc = item.OccurredUtc;
            _pendingPlayEvents.Add(item);
            _lastPlayUtc = item.OccurredUtc;
            _playFlushCts?.Cancel(); _playFlushCts?.Dispose();
            _playFlushCts = new CancellationTokenSource();
            _ = FlushAfterQuietAsync(_playFlushCts.Token);
        }
        if (completed is not null) AutoClipReady?.Invoke(this, completed);
    }

    private async Task FlushAfterQuietAsync(CancellationToken token)
    {
        try { await Task.Delay(PlayQuietWindow, token); }
        catch (OperationCanceledException) { return; }
        AutoClipRequest? request;
        lock (_playGate)
        {
            if (token.IsCancellationRequested) return;
            request = TakePendingPlayLocked();
        }
        if (request is not null) AutoClipReady?.Invoke(this, request);
    }

    private AutoClipRequest? TakePendingPlayLocked()
    {
        if (_pendingPlayEvents.Count == 0) return null;
        var primary = _pendingPlayEvents.OrderByDescending(item => item.Priority).ThenBy(item => item.OccurredUtc).First();
        var request = new AutoClipRequest("league", "League of Legends", primary.Id, primary.Label,
            AutoClipTitleFormatter.Format("league", _pendingPlayEvents), _firstPlayUtc - Padding, _lastPlayUtc + Padding, primary.Priority);
        _pendingPlayEvents.Clear(); _firstPlayUtc = _lastPlayUtc = default;
        _playFlushCts?.Dispose(); _playFlushCts = null;
        return request;
    }
    private static (string, string, int) MultiKill(JsonElement item) => GetInt(item, "KillStreak") switch { 2 => ("double", "Double Kill", 20), 3 => ("triple", "Triple Kill", 30), 4 => ("quadra", "Quadra Kill", 40), >= 5 => ("penta", "Pentakill", 50), _ => (string.Empty, string.Empty, 0) };
    private static (string, string, int) StealOrKill(JsonElement item, string type) => string.Equals(item.TryGetProperty("Stolen", out var stolen) ? stolen.GetString() : null, "True", StringComparison.OrdinalIgnoreCase) ? ($"{type}-steal", $"{char.ToUpperInvariant(type[0]) + type[1..]} Steal", 45) : ($"{type}-kill", $"{char.ToUpperInvariant(type[0]) + type[1..]} Kill", 35);
    private static int? GetInt(JsonElement parent, string name) => parent.TryGetProperty(name, out var element) && element.TryGetInt32(out var value) ? value : null;
    private static bool Matches(JsonElement item, string property, string playerName) => !string.IsNullOrWhiteSpace(playerName) && item.TryGetProperty(property, out var value) && string.Equals(value.GetString(), playerName, StringComparison.OrdinalIgnoreCase);
    private static bool IsAssister(JsonElement item, string playerName) => item.TryGetProperty("Assisters", out var assisters) && assisters.ValueKind == JsonValueKind.Array && assisters.EnumerateArray().Any(value => string.Equals(value.GetString(), playerName, StringComparison.OrdinalIgnoreCase));
    public void Dispose() { Stop(); _client.Dispose(); }
}
