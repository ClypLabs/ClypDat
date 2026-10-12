using Avalonia.Threading;
using ClypDat.App.Services;

namespace ClypDat.App.ViewModels;

// Match stats: the editor's Stats section and the CS2 match history link in
// Settings › Game Stats.
public sealed partial class MainWindowViewModel
{
    private Cs2MatchHistoryService? _cs2MatchHistory;
    private MatchStatsViewModel? _selectedMatchStats;
    private bool _isSelectedClipCs2;
    private int _matchStatsLoadGeneration;
    private string? _matchStatsLoadedPath;
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
        _cs2MatchHistory.StatusChanged += (_, _) => Dispatcher.UIThread.Post(RaiseCs2MatchHistoryChanged);
        _cs2MatchHistory.Start();
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

    private void DisposeMatchStats() => _cs2MatchHistory?.Dispose();

    // ---- Editor ----

    public MatchStatsViewModel? SelectedMatchStats
    {
        get => _selectedMatchStats;
        private set => SetProperty(ref _selectedMatchStats, value);
    }

    public bool IsSelectedClipCs2
    {
        get => _isSelectedClipCs2;
        private set
        {
            if (!SetProperty(ref _isSelectedClipCs2, value)) return;
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
        var isCs2 = MatchClipMapping.IsCs2(GameFilterResolver.Resolve(clipInfo, media.Name), clipInfo?.AutoClipGameId);
        IsSelectedClipCs2 = isCs2;
        // A rebuild of the same clip keeps its panel up while it reloads.
        if (!string.Equals(_matchStatsLoadedPath, media.Path, StringComparison.OrdinalIgnoreCase)) SelectedMatchStats = null;
        _matchStatsLoadedPath = media.Path;
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

    public string Cs2MatchHistoryStatus => _cs2MatchHistory?.Link is { NeedsAttention: true }
        ? "Steam didn't accept your authentication code or match code. Enter them again to keep getting match stats."
        : !ClypDatAccountIsConnected && IsCs2MatchHistoryLinked ? "Sign in to ClypDat to download full match stats."
        : _cs2MatchHistory?.Status ?? string.Empty;

    public bool HasCs2MatchHistoryStatus => Cs2MatchHistoryStatus.Length > 0;

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
    }
}
