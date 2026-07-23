using FortniteReplayReader;
using FortniteReplayReader.Models.Events;
using Eve.Core.Settings;
using Unreal.Core.Models.Enums;

namespace Eve.App.Services;

// Fortnite writes a local Unreal replay while a match is in progress.  Reading
// the stable prefix of that file lets us detect events without communicating
// with, hooking, or injecting into the game process.
public sealed class FortniteAutoClipListener : IDisposable
{
    private static readonly TimeSpan ScanInterval = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan StableWriteDelay = TimeSpan.FromMilliseconds(500);
    private static readonly TimeSpan ChainQuietWindow = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan Padding = TimeSpan.FromSeconds(4);
    private static readonly TimeSpan VictoryLeadIn = TimeSpan.FromSeconds(15);
    private const double LongDistanceMeters = 100;
    private const double LudicrousDistanceMeters = 200;

    private readonly Func<AutoClipGameSettings> _settings;
    private readonly object _gate = new();
    private readonly Dictionary<string, ReplayFileState> _files = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _startingFiles = new(StringComparer.OrdinalIgnoreCase);
    private CancellationTokenSource? _cts;
    private FileSystemWatcher? _watcher;
    private PendingChain? _pending;
    private string _statusText = "Waiting for Fortnite replays";

    public FortniteAutoClipListener(Func<AutoClipGameSettings> settings) => _settings = settings;

    public event EventHandler<string>? AutoClipPending;
    public event EventHandler<AutoClipRequest>? AutoClipReady;
    public event EventHandler<string>? StatusChanged;

    public bool IsListening => _cts is not null;
    public string StatusText => _statusText;

    private static string ReplayDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "FortniteGame", "Saved", "Demos");

    public void Start()
    {
        if (_cts is not null) return;

        _cts = new CancellationTokenSource();
        _startingFiles.Clear();
        try
        {
            if (Directory.Exists(ReplayDirectory))
            {
                foreach (var file in Directory.EnumerateFiles(ReplayDirectory, "*.replay")) _startingFiles.Add(file);
                _watcher = new FileSystemWatcher(ReplayDirectory, "*.replay")
                {
                    IncludeSubdirectories = false,
                    NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size,
                    EnableRaisingEvents = true
                };
                _watcher.Created += OnReplayFileChanged;
                _watcher.Changed += OnReplayFileChanged;
                _watcher.Renamed += OnReplayFileRenamed;
            }
        }
        catch (Exception error)
        {
            AppLog.Error("Fortnite replay watcher could not start", error);
        }

        SetStatus(Directory.Exists(ReplayDirectory)
            ? "Waiting for a live Fortnite replay"
            : "Enable Fortnite Record Replays to use auto-clipping");
        _ = ScanLoopAsync(_cts.Token);
    }

    public void Stop()
    {
        _cts?.Cancel();
        _cts?.Dispose();
        _cts = null;
        if (_watcher is not null)
        {
            _watcher.EnableRaisingEvents = false;
            _watcher.Created -= OnReplayFileChanged;
            _watcher.Changed -= OnReplayFileChanged;
            _watcher.Renamed -= OnReplayFileRenamed;
            _watcher.Dispose();
            _watcher = null;
        }

        lock (_gate)
        {
            _files.Clear();
            _startingFiles.Clear();
            _pending = null;
        }
    }

    private void OnReplayFileChanged(object sender, FileSystemEventArgs args) => _ = ProcessFileAsync(args.FullPath, _cts?.Token ?? CancellationToken.None);
    private void OnReplayFileRenamed(object sender, RenamedEventArgs args) => _ = ProcessFileAsync(args.FullPath, _cts?.Token ?? CancellationToken.None);

    private async Task ScanLoopAsync(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            try
            {
                if (Directory.Exists(ReplayDirectory))
                {
                    foreach (var file in Directory.EnumerateFiles(ReplayDirectory, "*.replay"))
                    {
                        await ProcessFileAsync(file, token);
                    }
                }

                FinalizePendingIfQuiet(MonotonicClock.UtcNow);
                await Task.Delay(ScanInterval, token);
            }
            catch (OperationCanceledException) { break; }
            catch (Exception error) { AppLog.Error("Fortnite replay scan failed", error); }
        }
    }

    private async Task ProcessFileAsync(string path, CancellationToken token)
    {
        if (token.IsCancellationRequested || !File.Exists(path)) return;

        ReplayFileState state;
        lock (_gate)
        {
            if (_startingFiles.Contains(path)) return;
            if (!_files.TryGetValue(path, out state!))
            {
                state = new ReplayFileState();
                _files[path] = state;
            }
            if (state.IsParsing) return;
            state.IsParsing = true;
        }

        try
        {
            var info = new FileInfo(path);
            var length = info.Length;
            var now = MonotonicClock.UtcNow;
            if (length <= 0) return;
            if (state.LastLength != length)
            {
                state.LastLength = length;
                state.LastWriteUtc = now;
                return;
            }
            if (now - state.LastWriteUtc < StableWriteDelay) return;

            FortniteReplayReader.Models.FortniteReplay replay;
            try
            {
                var reader = new ReplayReader(parseMode: ParseMode.Minimal);
                replay = await Task.Run(() => reader.ReadReplay(path), token);
            }
            catch (OperationCanceledException) { return; }
            catch (Exception error)
            {
                // A replay is commonly between chunks while Fortnite writes it.
                // The next stable scan retries without surfacing a failure to users.
                AppLog.Info($"Fortnite replay is not ready yet: {Path.GetFileName(path)} ({error.GetType().Name}).");
                return;
            }

            if (replay.PlayerData is null) return;
            var owner = replay.PlayerData.FirstOrDefault(player => player.IsReplayOwner);
            if (owner?.PlayerId is not { Length: > 0 } ownerId) return;

            var replayStart = replay.Info.Timestamp.Kind == DateTimeKind.Utc
                ? replay.Info.Timestamp
                : replay.Info.Timestamp.ToUniversalTime();
            if (replayStart == DateTime.MinValue) return;

            var settings = _settings();
            if (!settings.Enabled) return;

            foreach (var elimination in replay.Eliminations.OrderBy(item => item.Info?.StartTime))
            {
                if (elimination.Info is null) continue;
                var eventKey = $"elim:{elimination.Info.StartTime}:{elimination.Eliminator}:{elimination.Eliminated}";
                if (!state.SeenEvents.Add(eventKey)) continue;

                var timestamp = replayStart.AddMilliseconds(elimination.Info.StartTime);
                if (string.Equals(elimination.Eliminator, ownerId, StringComparison.OrdinalIgnoreCase) && !elimination.Knocked)
                {
                    ProcessPlayerElimination(elimination, timestamp, settings);
                }
                else if (string.Equals(elimination.Eliminated, ownerId, StringComparison.OrdinalIgnoreCase) && !elimination.Knocked)
                {
                    ProcessPlayerDeath(timestamp, settings);
                }
            }

            if (replay.GameData.WinningPlayerIds?.Contains(owner.Id.GetValueOrDefault()) == true && state.SeenEvents.Add("victory"))
            {
                ProcessVictory(replayStart.AddMilliseconds(replay.Info.LengthInMs), settings);
            }

            SetStatus(replay.Info.IsLive ? "Watching live Fortnite replay" : "Fortnite replay processed");
        }
        finally
        {
            lock (_gate) state.IsParsing = false;
        }
    }

    private void ProcessPlayerElimination(PlayerElimination elimination, DateTime timestamp, AutoClipGameSettings settings)
    {
        lock (_gate)
        {
            var distanceMeters = (elimination.Distance ?? 0) / 100d;
            var (eventId, label) = distanceMeters >= LudicrousDistanceMeters
                ? ("ludicrous-shot", "Ludicrous Shot")
                : distanceMeters >= LongDistanceMeters
                    ? ("long-distance-shot", "Long Distance Shot")
                    : ("eliminated-player", "Eliminated Player");

            _pending ??= new PendingChain(timestamp);
            _pending.LastUtc = timestamp;
            _pending.KillCount++;
            if (IsEnabled(settings, eventId))
            {
                _pending.BestSingleId = eventId;
                _pending.BestSingleLabel = label;
            }

            if (!_pending.NotificationShown && (IsEnabled(settings, eventId) || IsEnabled(settings, "double-kill") || IsEnabled(settings, "multi-kill")))
            {
                _pending.NotificationShown = true;
                AutoClipPending?.Invoke(this, "Auto clip started — kill detected, waiting for more info.");
            }
        }
    }

    private void ProcessPlayerDeath(DateTime timestamp, AutoClipGameSettings settings)
    {
        lock (_gate)
        {
            if (_pending is not null) FinalizePendingLocked(timestamp, settings);
            else if (IsEnabled(settings, "got-eliminated")) FireLocked("got-eliminated", "Got Eliminated", timestamp - Padding, timestamp + Padding);
        }
    }

    private void ProcessVictory(DateTime timestamp, AutoClipGameSettings settings)
    {
        lock (_gate)
        {
            if (_pending is not null)
            {
                if (IsEnabled(settings, "victory-royale"))
                {
                    FireLocked("victory-royale", "Victory Royale", _pending.StartUtc - Padding, timestamp + Padding);
                    _pending = null;
                }
                else FinalizePendingLocked(timestamp, settings);
            }
            else if (IsEnabled(settings, "victory-royale"))
            {
                FireLocked("victory-royale", "Victory Royale", timestamp - VictoryLeadIn, timestamp + Padding);
            }
        }
    }

    private void FinalizePendingIfQuiet(DateTime now)
    {
        lock (_gate)
        {
            if (_pending is not null && now - _pending.LastUtc >= ChainQuietWindow) FinalizePendingLocked(_pending.LastUtc, _settings());
        }
    }

    private void FinalizePendingLocked(DateTime endUtc, AutoClipGameSettings settings)
    {
        if (_pending is null) return;
        var (eventId, label) = _pending.KillCount switch
        {
            >= 3 when IsEnabled(settings, "multi-kill") => ("multi-kill", "Multi Kill"),
            >= 2 when IsEnabled(settings, "double-kill") => ("double-kill", "Double Kill"),
            _ => (_pending.BestSingleId, _pending.BestSingleLabel)
        };
        if (!string.IsNullOrEmpty(eventId)) FireLocked(eventId, label, _pending.StartUtc - Padding, endUtc + Padding);
        _pending = null;
    }

    private void FireLocked(string eventId, string label, DateTime startUtc, DateTime endUtc)
    {
        AutoClipPending?.Invoke(this, $"Auto clip started — {label} detected, finishing the clip.");
        AutoClipReady?.Invoke(this, new AutoClipRequest("fortnite", "Fortnite", eventId, label, startUtc, endUtc));
    }

    private static bool IsEnabled(AutoClipGameSettings settings, string eventId) => settings.Events.TryGetValue(eventId, out var enabled) && enabled;

    private void SetStatus(string value)
    {
        if (string.Equals(_statusText, value, StringComparison.Ordinal)) return;
        _statusText = value;
        StatusChanged?.Invoke(this, value);
    }

    public void Dispose() => Stop();

    private sealed class ReplayFileState
    {
        public long LastLength { get; set; }
        public DateTime LastWriteUtc { get; set; }
        public bool IsParsing { get; set; }
        public HashSet<string> SeenEvents { get; } = new(StringComparer.Ordinal);
    }

    private sealed class PendingChain
    {
        public PendingChain(DateTime timestamp) { StartUtc = LastUtc = timestamp; }
        public DateTime StartUtc { get; }
        public DateTime LastUtc { get; set; }
        public int KillCount { get; set; }
        public bool NotificationShown { get; set; }
        public string BestSingleId { get; set; } = string.Empty;
        public string BestSingleLabel { get; set; } = string.Empty;
    }
}
