using Avalonia.Threading;
using ClypDat.App.Services;

namespace ClypDat.App.ViewModels;

// Match stats: the editor's Stats section, the CS2 match history link and
// Fortnite's replay folder in Settings › Game Stats.
public sealed partial class MainWindowViewModel
{
    private Cs2MatchHistoryService? _cs2MatchHistory;
    private FortniteReplayService? _fortniteReplays;
    private MatchStatsViewModel? _selectedMatchStats;
    private bool _isSelectedClipStatsGame;
    private int _matchStatsLoadGeneration;
    private volatile bool _matchStatsDisposed;
    private int _fortniteRefreshQueued;
    private readonly CancellationTokenSource _cs2DemoImportLifetime = new();
    private bool _isImportingCs2Demo;
    private string _cs2DemoImportStatus = string.Empty;
    private string? _matchStatsLoadedPath;
    private (MediaFileInfo Media, ClipInfo? Info)? _matchStatsLoadedClip;
    private IReadOnlyList<ClipEventMarker> _selectedClipOwnMarkers = Array.Empty<ClipEventMarker>();
    private string _cs2SteamIdInput = string.Empty;
    private string _cs2AuthCodeInput = string.Empty;
    private string _cs2ShareCodeInput = string.Empty;
    private string? _cs2SteamIdError, _cs2AuthCodeError, _cs2ShareCodeError;
    private bool _isCs2AuthCodeRevealed;
    private bool _isEditingCs2MatchHistory;
    private string? _detectedCs2SteamId;

    private MatchStatsStore? MatchStore => string.IsNullOrWhiteSpace(Settings.LibraryFolder) ? null : new MatchStatsStore(Settings.LibraryFolder);

    private void InitializeMatchStats()
    {
        _cs2MatchHistory = new Cs2MatchHistoryService(
            token => _clypDatAccount.GetAccessTokenAsync("Sign in to ClypDat to download full match stats.", token),
            () => MatchStore,
            () => MemoryTrimmer.GameRunning,
            HasCs2MediaBetween);
        _cs2MatchHistory.StatusChanged += Cs2MatchHistoryStatusChanged;
        _cs2MatchHistory.Start();
        _fortniteReplays = new FortniteReplayService(() => MatchStore, () => MemoryTrimmer.GameRunning,
            () => Settings.FortniteMatchStatsEnabled, HasFortniteMediaBetween);
        // A replay appearing, growing or finishing can move the open clip from
        // waiting to reading to ready; re-check it without reopening.
        _fortniteReplays.Changed += FortniteReplaysChanged;
        _fortniteReplays.MatchParsed += FortniteMatchParsed;
        _fortniteReplays.Start();
        // Prefill the Steam ID from the last match GSI reported.
        var store = MatchStore;
        if (store is not null && !IsCs2MatchHistoryLinked)
            _ = Task.Run(() =>
            {
                try
                {
                    var steamId = store.LoadAll().Where(match => match.HasLiveData).OrderByDescending(match => match.EndedUtc)
                        .Select(match => match.LocalSteamId).FirstOrDefault(id => !string.IsNullOrEmpty(id));
                    if (steamId is null) return;
                    _detectedCs2SteamId = steamId;
                    Dispatcher.UIThread.Post(() => { if (string.IsNullOrWhiteSpace(Cs2SteamIdInput)) Cs2SteamIdInput = steamId; });
                }
                catch (Exception error) { AppLog.Info($"Match stats: Steam ID prefill skipped ({error.GetType().Name})."); }
            });
    }

    private void DisposeMatchStats()
    {
        if (_matchStatsDisposed) return;
        _matchStatsDisposed = true;
        ++_matchStatsLoadGeneration;
        _cs2DemoImportLifetime.Cancel();
        _cs2DemoImportLifetime.Dispose();
        if (_cs2MatchHistory is not null) _cs2MatchHistory.StatusChanged -= Cs2MatchHistoryStatusChanged;
        if (_fortniteReplays is not null)
        {
            _fortniteReplays.Changed -= FortniteReplaysChanged;
            _fortniteReplays.MatchParsed -= FortniteMatchParsed;
        }
        _cs2MatchHistory?.Dispose();
        _fortniteReplays?.Dispose();
    }

    private void Cs2MatchHistoryStatusChanged(object? sender, EventArgs e) => Dispatcher.UIThread.Post(() =>
    {
        if (!_matchStatsDisposed) RaiseCs2MatchHistoryChanged();
    });

    private void FortniteReplaysChanged(object? sender, EventArgs e) => QueueFortniteStatsRefresh();
    private void FortniteMatchParsed(object? sender, MatchStats e) => QueueFortniteStatsRefresh();

    private void QueueFortniteStatsRefresh()
    {
        if (_matchStatsDisposed || Interlocked.Exchange(ref _fortniteRefreshQueued, 1) != 0) return;
        Dispatcher.UIThread.Post(() =>
        {
            Interlocked.Exchange(ref _fortniteRefreshQueued, 0);
            if (_matchStatsDisposed) return;
            RaiseFortniteReplaysChanged();
            ReloadFortniteMatchStats();
        });
    }

    // ---- Editor ----

    public MatchStatsViewModel? SelectedMatchStats
    {
        get => _selectedMatchStats;
        private set => SetProperty(ref _selectedMatchStats, value);
    }

    // CS2 and Fortnite clips have a Stats section.
    public bool IsSelectedClipStatsGame
    {
        get => _isSelectedClipStatsGame;
        private set
        {
            if (!SetProperty(ref _isSelectedClipStatsGame, value)) return;
            if (!value && ActiveEditorSidebarSection == EditorSidebarSection.Stats) OpenEditorSidebar(EditorSidebarSection.Info);
        }
    }

    public bool IsEditorStatsSidebarActive => ActiveEditorSidebarSection == EditorSidebarSection.Stats;

    /// <summary>
    /// Finds the match the opened clip was recorded in, off the UI thread, and
    /// adds its events to the clip's markers when they can be placed exactly.
    /// </summary>
    private void LoadSelectedMatchStats(MediaFileInfo media, ClipInfo? clipInfo)
    {
        var generation = ++_matchStatsLoadGeneration;
        _selectedClipOwnMarkers = clipInfo?.AutoClipMarkers ?? Array.Empty<ClipEventMarker>();
        var game = GameFilterResolver.Resolve(clipInfo, media.Name);
        var isCs2 = MatchClipMapping.IsCs2(game, clipInfo?.AutoClipGameId);
        var isFortnite = !isCs2 && FortniteMatchStats.IsFortnite(game, clipInfo?.AutoClipGameId);
        IsSelectedClipStatsGame = isCs2 || isFortnite;
        // A rebuild of the same clip keeps its panel up while it reloads.
        var sameClip = string.Equals(_matchStatsLoadedPath, media.Path, StringComparison.OrdinalIgnoreCase);
        if (!sameClip) SelectedMatchStats = null;
        _matchStatsLoadedPath = media.Path;
        _matchStatsLoadedClip = isCs2 || isFortnite ? (media, clipInfo) : null;
        if (isFortnite)
        {
            LoadFortniteMatchStats(media, clipInfo, generation);
            return;
        }
        if (!isCs2 || MatchStore is not { } store) return;

        var libraryRoot = store.LibraryRoot;
        var isRecording = media.Duration.TotalSeconds > LibraryLayout.ClipMaximumDurationSeconds ||
                          media.Path.StartsWith(LibraryLayout.VodsRoot(libraryRoot), StringComparison.OrdinalIgnoreCase);
        _ = Task.Run(() =>
        {
            try
            {
                var timeline = MatchClipMapping.Describe(clipInfo, media.CreatedAt, media.Duration, isRecording, ReadSourceWindow(libraryRoot, media.Path));
                var match = MatchClipMapping.FindMatch(store.LoadAll(), timeline);
                var markers = match is null ? [] : MatchClipMapping.Markers(match, timeline);
                var view = match is null
                    ? MatchStatsViewModel.Empty("No match stats for this clip yet. ClypDat keeps a scoreboard for each CS2 match while it runs, and can fill in older matches from your linked match history in Settings › Game Stats.")
                    : MatchStatsViewModel.From(match);
                Dispatcher.UIThread.Post(() =>
                {
                    if (generation != _matchStatsLoadGeneration || !string.Equals(SelectedVideoPath, media.Path, StringComparison.OrdinalIgnoreCase)) return;
                    SelectedMatchStats = view;
                    if (markers.Count == 0) return;
                    SelectedAutoClipMarkers = _selectedClipOwnMarkers.Concat(markers).OrderBy(marker => marker.OffsetSeconds).ToArray();
                    OnPropertyChanged(nameof(SelectedAutoClipMarkers));
                });
            }
            catch (Exception error)
            {
                AppLog.Error("Match stats could not be loaded for the clip", error);
            }
        });
    }

    private void ReloadFortniteMatchStats()
    {
        if (_matchStatsDisposed || _matchStatsLoadedClip is not { } clip || !string.Equals(SelectedVideoPath, clip.Media.Path, StringComparison.OrdinalIgnoreCase)) return;
        if (!FortniteMatchStats.IsFortnite(GameFilterResolver.Resolve(clip.Info, clip.Media.Name), clip.Info?.AutoClipGameId)) return;
        LoadFortniteMatchStats(clip.Media, clip.Info, ++_matchStatsLoadGeneration);
    }

    // Fortnite: the clip's match comes from Fortnite's own replay. Until that
    // replay is finished the panel says it is waiting; once it is, it is read
    // (or its saved summary reused) and the panel fills in by itself.
    private void LoadFortniteMatchStats(MediaFileInfo media, ClipInfo? clipInfo, int generation)
    {
        var store = MatchStore;
        var service = _fortniteReplays;
        var enabled = Settings.FortniteMatchStatsEnabled;
        if (store is null || service is null)
        {
            SelectedMatchStats = MatchStatsViewModel.Empty("Match stats are unavailable until a library folder is selected.");
            return;
        }
        var libraryRoot = store.LibraryRoot;
        var isRecording = media.Duration.TotalSeconds > LibraryLayout.ClipMaximumDurationSeconds ||
                          media.Path.StartsWith(LibraryLayout.VodsRoot(libraryRoot), StringComparison.OrdinalIgnoreCase);
        _ = Task.Run(() =>
        {
            try
            {
                var timeline = MatchClipMapping.Describe(clipInfo, media.CreatedAt, media.Duration, isRecording, ReadSourceWindow(libraryRoot, media.Path));
                var view = FortniteView(store, service, timeline, enabled, out var match);
                var markers = match is null ? [] : MatchClipMapping.Markers(match, timeline);
                Dispatcher.UIThread.Post(() =>
                {
                    if (_matchStatsDisposed || generation != _matchStatsLoadGeneration || !string.Equals(SelectedVideoPath, media.Path, StringComparison.OrdinalIgnoreCase)) return;
                    // Keep the Clip / Full Match choice across a refresh.
                    if (SelectedMatchStats is { HasScopeToggle: true } previous && view.HasScopeToggle) view.SelectedScope = previous.SelectedScope;
                    SelectedMatchStats = view;
                    SelectedAutoClipMarkers = _selectedClipOwnMarkers.Concat(markers).OrderBy(marker => marker.OffsetSeconds).ToArray();
                    OnPropertyChanged(nameof(SelectedAutoClipMarkers));
                });
            }
            catch (Exception error)
            {
                AppLog.Error("Fortnite match stats could not be loaded for the clip", error);
                Dispatcher.UIThread.Post(() =>
                {
                    if (_matchStatsDisposed || generation != _matchStatsLoadGeneration || !string.Equals(SelectedVideoPath, media.Path, StringComparison.OrdinalIgnoreCase)) return;
                    SelectedMatchStats = MatchStatsViewModel.Empty("This clip's match stats couldn't be loaded. Reopen the clip to try again.");
                    SelectedAutoClipMarkers = _selectedClipOwnMarkers;
                    OnPropertyChanged(nameof(SelectedAutoClipMarkers));
                });
            }
        });
    }

    private const string RecordReplaysHelp = "Turn on Record Replays in Fortnite: Settings › Game › Replays.";

    internal static MatchStatsViewModel FortniteView(MatchStatsStore store, FortniteReplayService service, ClipTimeline timeline, bool enabled, out MatchStats? match)
    {
        match = null;
        if (!enabled) return MatchStatsViewModel.Empty("Fortnite match stats are off. Turn on Keep match stats in Settings › Game Stats.");
        var (state, header) = service.Resolve(timeline);
        var all = store.LoadAll();
        if (state != FortniteClipReplayState.Waiting)
            match = header is not null
                ? all.FirstOrDefault(item => item.Game == FortniteMatchStats.Game && string.Equals(item.SourceKey, header.CacheKey, StringComparison.OrdinalIgnoreCase))
                : MatchClipMapping.FindMatch(all, timeline, FortniteMatchStats.Game);
        if (match is not null)
        {
            var window = MatchClipMapping.ReplayWindow(match, timeline);
            var note = window is not null ? string.Empty
                : !timeline.Exact ? "This clip has no verified capture timing, so its place in the match isn't known exactly. Showing the whole match."
                : "This clip is from outside the match itself, so only the whole match is shown.";
            return MatchStatsViewModel.FromFortnite(match, window, note);
        }
        switch (state)
        {
            case FortniteClipReplayState.Waiting:
                return MatchStatsViewModel.WaitingForMatch();
            case FortniteClipReplayState.Incomplete:
                return MatchStatsViewModel.Empty("Fortnite closed before this match's replay was finished, so there are no stats for it.");
            case FortniteClipReplayState.Ready when header is not null && service.HasFailed(header):
                return MatchStatsViewModel.Empty("This match's replay couldn't be read. It may be from a Fortnite update ClypDat can't decode yet.");
            case FortniteClipReplayState.Ready when header is not null:
                service.Request(header);
                return MatchStatsViewModel.ProcessingMatchStats(service.IsParsingDeferred);
        }
        return service.ReplayCount == 0
            ? MatchStatsViewModel.Empty($"No Fortnite replays found, so there are no match stats yet. {RecordReplaysHelp} Matches played after that get stats.")
            : MatchStatsViewModel.Empty($"No Fortnite replay covers this clip. Fortnite keeps only its most recent replays, and records them only while Record Replays is on.");
    }

    /// <summary>The replay being recorded (or just finished) across a clip's span, for its save-time metadata.</summary>
    internal FortniteReplayHeader? FortniteReplayAt(DateTime startUtc, DateTime endUtc) =>
        Settings.FortniteMatchStatsEnabled ? _fortniteReplays?.ReplayAt(startUtc, endUtc) : null;

    // The recorder's own timeline for the clip, from any boot: the match
    // decides whether its clock still applies (SpotifySourceWindow.Load only
    // returns this boot's).
    private static SpotifySourceWindow? ReadSourceWindow(string libraryRoot, string clipPath)
    {
        try
        {
            var path = LibraryLayout.SidecarPath(libraryRoot, clipPath, ".source.json");
            if (!LibraryPathGuard.IsWithin(libraryRoot, path) || !File.Exists(path) || new FileInfo(path).Length > 256 * 1024) return null;
            return System.Text.Json.JsonSerializer.Deserialize<SpotifySourceWindow>(File.ReadAllText(path));
        }
        catch { return null; }
    }

    // Whether anything in the library was recorded in CS2 between two times;
    // demos are only downloaded for matches that have something to show.
    private bool HasCs2MediaBetween(DateTime startUtc, DateTime endUtc)
    {
        IReadOnlyList<(DateTime Start, DateTime End)> spans;
        try
        {
            spans = Dispatcher.UIThread.InvokeAsync(() => AllClips
                .Where(card => MatchClipMapping.IsCs2(card.GameFilterKey, null))
                .Select(card => (card.CreatedAt.UtcDateTime - card.Duration - TimeSpan.FromMinutes(1), card.CreatedAt.UtcDateTime + card.Duration + TimeSpan.FromMinutes(1)))
                .ToArray()).GetTask().WaitAsync(TimeSpan.FromSeconds(15)).GetAwaiter().GetResult();
        }
        catch (Exception error) when (error is TimeoutException or TaskCanceledException) { return false; }
        return spans.Any(span => span.Start < endUtc && span.End > startUtc);
    }

    // Whether anything in the library was recorded in Fortnite between two
    // times; only those replays are read without being asked.
    private bool HasFortniteMediaBetween(DateTime startUtc, DateTime endUtc)
    {
        IReadOnlyList<(DateTime Start, DateTime End)> spans;
        try
        {
            spans = Dispatcher.UIThread.InvokeAsync(() => AllClips
                .Where(card => FortniteMatchStats.IsFortnite(card.GameFilterKey, null))
                .Select(card => (card.CreatedAt.UtcDateTime - card.Duration - TimeSpan.FromMinutes(1), card.CreatedAt.UtcDateTime + card.Duration + TimeSpan.FromMinutes(1)))
                .ToArray()).GetTask().WaitAsync(TimeSpan.FromSeconds(15)).GetAwaiter().GetResult();
        }
        catch (Exception error) when (error is TimeoutException or TaskCanceledException) { return false; }
        return spans.Any(span => span.Start < endUtc && span.End > startUtc);
    }

    internal void OnCs2MatchCompleted(MatchStats match)
    {
        if (match.LocalSteamId is { Length: > 0 } steamId) _detectedCs2SteamId = steamId;
        _cs2MatchHistory?.OnLiveMatchCompleted(match);
        Dispatcher.UIThread.Post(() =>
        {
            if (string.IsNullOrWhiteSpace(Cs2SteamIdInput) && _detectedCs2SteamId is { } detected) Cs2SteamIdInput = detected;
        });
    }

    // ---- Settings ----

    public bool Cs2MatchStatsEnabled
    {
        get => Settings.Cs2MatchStatsEnabled;
        set
        {
            if (Settings.Cs2MatchStatsEnabled == value) return;
            Settings.Cs2MatchStatsEnabled = value;
            SaveSettings();
            OnPropertyChanged();
            OnPropertyChanged(nameof(WantsCs2MatchStats));
        }
    }

    public bool WantsCs2MatchStats => Settings.Cs2MatchStatsEnabled;

    public bool IsImportingCs2Demo
    {
        get => _isImportingCs2Demo;
        private set => SetProperty(ref _isImportingCs2Demo, value);
    }

    public string Cs2DemoImportStatus
    {
        get => _cs2DemoImportStatus;
        private set
        {
            if (!SetProperty(ref _cs2DemoImportStatus, value)) return;
            OnPropertyChanged(nameof(HasCs2DemoImportStatus));
        }
    }

    public bool HasCs2DemoImportStatus => Cs2DemoImportStatus.Length > 0;

    public async Task ImportCs2DemoAsync(string demoPath)
    {
        if (_matchStatsDisposed || IsImportingCs2Demo) return;
        if (MatchStore is not { } store)
        {
            Cs2DemoImportStatus = "Choose a library folder before importing match stats.";
            return;
        }
        IsImportingCs2Demo = true;
        Cs2DemoImportStatus = MemoryTrimmer.GameRunning ? "Waiting for the game to close before reading this demo…" : "Reading saved demo…";
        try
        {
            var steamId = _cs2MatchHistory?.Link?.SteamId ?? Cs2MatchHistoryFormats.NormaliseSteamId(Cs2SteamIdInput) ?? _detectedCs2SteamId;
            var result = await Cs2LocalDemoImport.ImportAsync(demoPath, steamId, store, _cs2DemoImportLifetime.Token);
            if (_matchStatsDisposed) return;
            Cs2DemoImportStatus = result.Message;
            if (_matchStatsLoadedClip is { } clip && string.Equals(SelectedVideoPath, clip.Media.Path, StringComparison.OrdinalIgnoreCase) &&
                MatchClipMapping.IsCs2(GameFilterResolver.Resolve(clip.Info, clip.Media.Name), clip.Info?.AutoClipGameId))
                LoadSelectedMatchStats(clip.Media, clip.Info);
        }
        catch (OperationCanceledException)
        {
            if (!_matchStatsDisposed) Cs2DemoImportStatus = "Demo import cancelled.";
        }
        catch (Exception error)
        {
            AppLog.Error("CS2 saved demo import failed", error);
            if (!_matchStatsDisposed) Cs2DemoImportStatus = error is InvalidDataException ? error.Message : "This demo couldn't be imported. Check that it is a complete CS2 .dem file.";
        }
        finally
        {
            if (!_matchStatsDisposed) IsImportingCs2Demo = false;
        }
    }

    public bool FortniteMatchStatsEnabled
    {
        get => Settings.FortniteMatchStatsEnabled;
        set
        {
            if (Settings.FortniteMatchStatsEnabled == value) return;
            Settings.FortniteMatchStatsEnabled = value;
            SaveSettings();
            OnPropertyChanged();
            if (value) _ = Task.Run(() => _fortniteReplays?.Backfill());
            ReloadFortniteMatchStats();
        }
    }

    public bool FortniteReplayFolderExists => _fortniteReplays?.FolderExists == true;

    public string FortniteReplayStatus => _fortniteReplays is not { } service ? string.Empty
        : service.ReplayCount == 0 ? $"No replays found. {RecordReplaysHelp}"
        : service.ReplayCount == 1 ? "Found 1 replay."
        : $"Found {service.ReplayCount} replays.";

    public bool HasNoFortniteReplays => _fortniteReplays is { ReplayCount: 0 };

    public void OpenFortniteReplayFolder()
    {
        if (_fortniteReplays is { FolderExists: true } service) ExplorerService.Open(service.Folder, selectFile: false);
    }

    private void RaiseFortniteReplaysChanged()
    {
        OnPropertyChanged(nameof(FortniteReplayFolderExists));
        OnPropertyChanged(nameof(FortniteReplayStatus));
        OnPropertyChanged(nameof(HasNoFortniteReplays));
    }

    public string Cs2SteamIdInput
    {
        get => _cs2SteamIdInput;
        set
        {
            if (!SetProperty(ref _cs2SteamIdInput, value ?? string.Empty)) return;
            ShowExtractedCode(_cs2SteamIdInput, Cs2MatchHistoryFormats.NormaliseSteamId, extracted => Cs2SteamIdInput = extracted);
            if (Cs2SteamIdError is not null) ValidateCs2MatchHistoryInputs();
        }
    }

    public string Cs2AuthCodeInput
    {
        get => _cs2AuthCodeInput;
        set { if (SetProperty(ref _cs2AuthCodeInput, value ?? string.Empty) && Cs2AuthCodeError is not null) ValidateCs2MatchHistoryInputs(); }
    }

    public string Cs2ShareCodeInput
    {
        get => _cs2ShareCodeInput;
        set
        {
            if (!SetProperty(ref _cs2ShareCodeInput, value ?? string.Empty)) return;
            ShowExtractedCode(_cs2ShareCodeInput, Cs2MatchHistoryFormats.NormaliseShareCode, extracted => Cs2ShareCodeInput = extracted);
            if (Cs2ShareCodeError is not null) ValidateCs2MatchHistoryInputs();
        }
    }

    // CS2 copies a share code as a whole steam:// link, and Steam profiles
    // as URLs. Replace a pasted link with just the code it carries, so the
    // box shows what will be saved. Posted, because rewriting the text in
    // the middle of the TextBox's own update can be dropped.
    private static void ShowExtractedCode(string input, Func<string?, string?> normalise, Action<string> apply)
    {
        var extracted = normalise(input);
        if (extracted is null || string.Equals(extracted, input, StringComparison.Ordinal)) return;
        Dispatcher.UIThread.Post(() => apply(extracted));
    }

    public string? Cs2SteamIdError { get => _cs2SteamIdError; private set { SetProperty(ref _cs2SteamIdError, value); OnPropertyChanged(nameof(HasCs2SteamIdError)); } }
    public string? Cs2AuthCodeError { get => _cs2AuthCodeError; private set { SetProperty(ref _cs2AuthCodeError, value); OnPropertyChanged(nameof(HasCs2AuthCodeError)); } }
    public string? Cs2ShareCodeError { get => _cs2ShareCodeError; private set { SetProperty(ref _cs2ShareCodeError, value); OnPropertyChanged(nameof(HasCs2ShareCodeError)); } }
    public bool HasCs2SteamIdError => Cs2SteamIdError is not null;
    public bool HasCs2AuthCodeError => Cs2AuthCodeError is not null;
    public bool HasCs2ShareCodeError => Cs2ShareCodeError is not null;

    public bool IsCs2AuthCodeRevealed
    {
        get => _isCs2AuthCodeRevealed;
        set { if (SetProperty(ref _isCs2AuthCodeRevealed, value)) OnPropertyChanged(nameof(Cs2AuthCodeRevealLabel)); }
    }

    public string Cs2AuthCodeRevealLabel => IsCs2AuthCodeRevealed ? "Hide" : "Show";

    public bool IsCs2MatchHistoryLinked => _cs2MatchHistory?.Link is not null;
    public bool Cs2MatchHistoryNeedsAttention => _cs2MatchHistory?.Link?.NeedsAttention == true;
    public bool IsCs2MatchHistoryFormVisible => !IsCs2MatchHistoryLinked || _isEditingCs2MatchHistory || Cs2MatchHistoryNeedsAttention;
    public bool IsCs2MatchHistorySummaryVisible => IsCs2MatchHistoryLinked && !IsCs2MatchHistoryFormVisible;

    // Never the code itself once saved: its last four characters at most.
    public string Cs2MatchHistorySummary => _cs2MatchHistory?.Link is { } link
        ? $"Linked to Steam ID {link.SteamId}, authentication code ending {link.AuthCodeHint}."
        : string.Empty;

    public string Cs2MatchHistoryStatus => _cs2MatchHistory?.Link is { NeedsAttention: true } rejected
        ? Cs2MatchHistoryService.AttentionMessage(rejected.RejectedField)
        : !ClypDatAccountIsConnected && IsCs2MatchHistoryLinked ? "Sign in to ClypDat to download full match stats."
        : _cs2MatchHistory?.Status ?? string.Empty;

    public bool HasCs2MatchHistoryStatus => Cs2MatchHistoryStatus.Length > 0;
    public bool CanRetryCs2MatchHistory => ClypDatAccountIsConnected && _cs2MatchHistory?.CanRetry == true;

    public void RetryCs2MatchHistory() => _cs2MatchHistory?.RetryNow();

    public void BeginEditCs2MatchHistory()
    {
        _isEditingCs2MatchHistory = true;
        if (_cs2MatchHistory?.Link is { } link) Cs2SteamIdInput = link.SteamId;
        RaiseCs2MatchHistoryChanged();
    }

    public void CancelEditCs2MatchHistory()
    {
        _isEditingCs2MatchHistory = false;
        ClearCs2MatchHistoryInputs();
        RaiseCs2MatchHistoryChanged();
    }

    /// <summary>Validates all three, saves only if every one is valid.</summary>
    public bool LinkCs2MatchHistory()
    {
        if (!ValidateCs2MatchHistoryInputs() || _cs2MatchHistory is null) return false;
        _cs2MatchHistory.SaveLink(
            Cs2MatchHistoryFormats.NormaliseSteamId(Cs2SteamIdInput)!,
            Cs2MatchHistoryFormats.NormaliseAuthCode(Cs2AuthCodeInput)!,
            Cs2MatchHistoryFormats.NormaliseShareCode(Cs2ShareCodeInput)!);
        _isEditingCs2MatchHistory = false;
        ClearCs2MatchHistoryInputs();
        RaiseCs2MatchHistoryChanged();
        return true;
    }

    public void UnlinkCs2MatchHistory()
    {
        _cs2MatchHistory?.Unlink();
        _isEditingCs2MatchHistory = false;
        ClearCs2MatchHistoryInputs();
        RaiseCs2MatchHistoryChanged();
    }

    private bool ValidateCs2MatchHistoryInputs()
    {
        Cs2SteamIdError = Cs2MatchHistoryFormats.SteamIdError(Cs2SteamIdInput);
        Cs2AuthCodeError = Cs2MatchHistoryFormats.AuthCodeError(Cs2AuthCodeInput);
        Cs2ShareCodeError = Cs2MatchHistoryFormats.ShareCodeError(Cs2ShareCodeInput);
        return Cs2SteamIdError is null && Cs2AuthCodeError is null && Cs2ShareCodeError is null;
    }

    private void ClearCs2MatchHistoryInputs()
    {
        // The typed code goes as soon as it is saved or abandoned.
        Cs2AuthCodeInput = string.Empty;
        Cs2ShareCodeInput = string.Empty;
        IsCs2AuthCodeRevealed = false;
        Cs2SteamIdError = Cs2AuthCodeError = Cs2ShareCodeError = null;
        if (_detectedCs2SteamId is { } detected) Cs2SteamIdInput = detected;
    }

    private void RaiseCs2MatchHistoryChanged()
    {
        OnPropertyChanged(nameof(IsCs2MatchHistoryLinked));
        OnPropertyChanged(nameof(Cs2MatchHistoryNeedsAttention));
        OnPropertyChanged(nameof(IsCs2MatchHistoryFormVisible));
        OnPropertyChanged(nameof(IsCs2MatchHistorySummaryVisible));
        OnPropertyChanged(nameof(Cs2MatchHistorySummary));
        OnPropertyChanged(nameof(Cs2MatchHistoryStatus));
        OnPropertyChanged(nameof(HasCs2MatchHistoryStatus));
        OnPropertyChanged(nameof(CanRetryCs2MatchHistory));
    }
}
