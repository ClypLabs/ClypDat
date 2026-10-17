using Avalonia;
using Avalonia.Media;
using Avalonia.Media.Immutable;
using ClypDat.App.Services;

namespace ClypDat.App.ViewModels;

public sealed record MatchRoundCellViewModel(string Number, IBrush Fill, Thickness Margin, string Tip);

public sealed record MatchPlayerRowViewModel(string Name, string Kda, string Detail, double BarWidth, bool IsLocal, FontWeight Weight);

public sealed record MatchTeamBoardViewModel(string Title, string Score, IBrush Accent, IReadOnlyList<MatchPlayerRowViewModel> Players);

public sealed record MatchTimelineRowViewModel(string Time, string Title, string Detail, IBrush Dot, IBrush Background, string ClipTime)
{
    public bool HasDetail => Detail.Length > 0;
    public bool HasClipTime => ClipTime.Length > 0;
}

public sealed record MatchDetailRowViewModel(string Label, string Value);

// What the editor's Stats panel binds to. Built once per opened clip, off
// the match it overlaps; scope and the selected radar explanation can change.
public sealed class MatchStatsViewModel : ViewModelBase
{
    private const double BarMaxWidth = 64;
    public const string ClipScope = "Clip";
    public const string FullScope = "Full Match";
    private string _selectedScope = FullScope;

    private MatchStatsViewModel() { }

    public bool HasMatch { get; private init; }
    public string EmptyTitle { get; private init; } = "MATCH STATS";
    public string EmptyMessage { get; private init; } = string.Empty;
    public bool IsWaitingForMatch { get; private init; }
    public bool IsProcessingMatchStats { get; private init; }
    public string PlayerName { get; private init; } = string.Empty;
    public string? OutfitId { get; private init; }
    public bool HasOutfit => OutfitId is not null;
    public string MatchLabel { get; private init; } = string.Empty;
    public string ResultLabel { get; private init; } = string.Empty;
    public string ScoreLabel { get; private init; } = string.Empty;
    public IBrush ResultBrush { get; private init; } = Brushes.Transparent;
    public IReadOnlyList<MatchStatTile> Tiles => IsClipScope ? ClipTiles : FullTiles;
    private IReadOnlyList<MatchStatTile> FullTiles { get; init; } = [];
    private IReadOnlyList<MatchStatTile> ClipTiles { get; init; } = [];
    public IReadOnlyList<MatchRadarAxis> RadarAxes { get; private init; } = [];
    public bool HasRadar => RadarAxes.Count > 0;
    private MatchRadarAxis? _selectedRadarAxis;
    public MatchRadarAxis? SelectedRadarAxis
    {
        get => _selectedRadarAxis;
        set
        {
            if (!SetProperty(ref _selectedRadarAxis, value)) return;
            OnPropertyChanged(nameof(HasRadarExplanation));
            OnPropertyChanged(nameof(RadarExplanationTitle));
            OnPropertyChanged(nameof(RadarExplanation));
        }
    }
    public bool HasRadarExplanation => SelectedRadarAxis?.Explanation.Length > 0;
    public string RadarExplanationTitle
    {
        get
        {
            if (SelectedRadarAxis is not { } axis) return string.Empty;
            var score = IsFortnite ? axis.Display : $"{Math.Clamp(axis.Value, 0, 1) * 100:0}";
            return $"{axis.Label} · {(axis.Available ? score + "/100" : axis.Display)}";
        }
    }
    public string RadarExplanation => SelectedRadarAxis?.Explanation ?? string.Empty;

    internal void RestoreSelectionFrom(MatchStatsViewModel? previous)
    {
        if (previous is null || previous.IsFortnite != IsFortnite) return;
        if (previous.HasScopeToggle && HasScopeToggle) SelectedScope = previous.SelectedScope;
        SelectedRadarAxis = previous.SelectedRadarAxis is { } selected
            ? RadarAxes.FirstOrDefault(axis => string.Equals(axis.Label, selected.Label, StringComparison.Ordinal))
            : null;
    }
    public IReadOnlyList<MatchRoundCellViewModel> Rounds { get; private init; } = [];
    public bool HasRounds => Rounds.Count > 0;
    public IReadOnlyList<MatchTeamBoardViewModel> Teams { get; private init; } = [];
    public string Footnote { get; private init; } = string.Empty;
    public bool HasFootnote => Footnote.Length > 0;

    // ---- Fortnite: the whole match, or just the clip ----

    public bool IsFortnite { get; private init; }
    public bool HasScopeToggle { get; private init; }
    public IReadOnlyList<string> Scopes { get; } = [ClipScope, FullScope];

    public string SelectedScope
    {
        get => _selectedScope;
        set
        {
            if (value is not (ClipScope or FullScope) || !SetProperty(ref _selectedScope, value)) return;
            OnPropertyChanged(nameof(IsClipScope));
            OnPropertyChanged(nameof(IsFullScope));
            OnPropertyChanged(nameof(IsFortniteFullScope));
            OnPropertyChanged(nameof(Tiles));
            OnPropertyChanged(nameof(TilesLabel));
        }
    }

    public bool IsClipScope => HasScopeToggle && SelectedScope == ClipScope;
    public bool IsFullScope => !IsClipScope;
    public bool IsFortniteFullScope => IsFortnite && IsFullScope;
    public string TilesLabel => IsClipScope ? "IN THIS CLIP" : "WHOLE MATCH";
    public IReadOnlyList<MatchTimelineRowViewModel> ClipEvents { get; private init; } = [];
    public bool HasClipEvents => ClipEvents.Count > 0;
    public bool HasNoClipEvents => ClipEvents.Count == 0;
    public IReadOnlyList<MatchTimelineRowViewModel> Timeline { get; private init; } = [];
    public bool HasTimeline => Timeline.Count > 0;
    public string RadarNote { get; private init; } = string.Empty;
    public bool HasRadarNote => RadarNote.Length > 0;
    public bool HasPlacement { get; private init; }
    public string PlacementLabel { get; private init; } = string.Empty;
    public string PlacementSummary { get; private init; } = string.Empty;
    public IReadOnlyList<MatchCountStep> PlacementSteps { get; private init; } = [];
    public IReadOnlyList<double> PlacementEliminations { get; private init; } = [];
    public double PlacementEndSeconds { get; private init; }
    public double ClipStartSeconds { get; private init; } = double.NaN;
    public double ClipEndSeconds { get; private init; } = double.NaN;
    public IReadOnlyList<MatchDetailRowViewModel> MatchDetails { get; private init; } = [];
    public bool HasMatchDetails => MatchDetails.Count > 0;

    public static MatchStatsViewModel Empty(string message, string title = "MATCH STATS") => new() { EmptyMessage = message, EmptyTitle = title };

    public static MatchStatsViewModel WaitingForMatch() => new()
    {
        IsFortnite = true,
        IsWaitingForMatch = true,
        EmptyTitle = "Waiting for match to end",
        EmptyMessage = "Your clip is ready to play. Match stats appear here after Fortnite finishes its replay."
    };

    public static MatchStatsViewModel ProcessingMatchStats(bool deferred = false) => new()
    {
        IsFortnite = true,
        IsProcessingMatchStats = true,
        EmptyTitle = "Processing match stats",
        EmptyMessage = deferred
            ? "Your clip is ready to play. Match stats will be processed automatically when you finish playing."
            : "Your clip is ready to play. Match stats appear automatically when processing finishes."
    };

    /// <summary>
    /// A Fortnite match. <paramref name="window"/> is where the clip sits in
    /// the replay, or null when that can't be trusted: then only the whole
    /// match is shown, and <paramref name="alignmentNote"/> says why.
    /// </summary>
    public static MatchStatsViewModel FromFortnite(MatchStats match, MatchReplayWindow? window, string alignmentNote)
    {
        var purple = AppThemeService.Brush("Semantic_CB8CFF", "#CB8CFF");
        var blue = AppThemeService.Brush("Semantic_59B6FF", "#59B6FF");
        var red = AppThemeService.Brush("Semantic_F05A63", "#F05A63");
        var gold = AppThemeService.Brush("Semantic_E5A00D", "#E5A00D");
        var neutral = AppThemeService.Brush("TextMuted", "#A8B5C3");
        // Models are built on the replay worker. Reading a mutable Avalonia
        // brush's Color there is illegal even when no application is loaded.
        IBrush highlight = new ImmutableSolidColorBrush(Color.Parse("#CB8CFF"), 0.16);
        IBrush Dot(string kind) => kind switch { "kill" => purple, "knock" => blue, "knocked" or "death" => red, "victory" => gold, _ => neutral };

        var rows = FortniteStatsPresentation.Timeline(match, window);
        MatchTimelineRowViewModel Row(MatchTimelineRow row, bool shade) =>
            new(row.Time, row.Title, row.Detail, Dot(row.Kind), shade && row.InClip ? highlight : Brushes.Transparent, row.ClipTime ?? string.Empty);
        var end = match.LocalEndSeconds ?? match.DemoDurationSeconds ?? 0;
        var radar = FortniteStatsPresentation.RadarAxes(match);
        var notes = FortniteStatsPresentation.RadarNotes(match);
        return new MatchStatsViewModel
        {
            HasMatch = true,
            IsFortnite = true,
            HasScopeToggle = window is not null,
            // Clip first whenever the clip lies inside the match.
            _selectedScope = window is not null ? ClipScope : FullScope,
            PlayerName = match.LocalPlayerName ?? "You",
            OutfitId = FortniteOutfitPortraitCache.NormaliseId(match.OutfitId),
            MatchLabel = string.IsNullOrWhiteSpace(match.Playlist) ? match.Mode ?? "Fortnite" : FortniteMatchStats.ModeLabel(match.Playlist),
            ResultLabel = match.Won ? "Victory Royale" : string.Empty,
            ResultBrush = gold,
            ScoreLabel = match.Placement is { } place ? $"#{place}" : string.Empty,
            FullTiles = FortniteStatsPresentation.Tiles(match),
            ClipTiles = window is null ? [] : FortniteStatsPresentation.ClipTiles(match, window),
            ClipEvents = rows.Where(row => row.InClip).Select(row => Row(row, false)).ToArray(),
            Timeline = rows.Select(row => Row(row, true)).ToArray(),
            // Keep unavailable axes visible instead of dropping the radar:
            // their labels explain which data the replay did not provide.
            RadarAxes = radar,
            RadarNote = string.Join(" ", notes),
            HasPlacement = match.PlayersLeft.Count > 1 && end > 0,
            PlacementLabel = match.Placement is { } placed ? $"#{placed}" : string.Empty,
            PlacementSummary = FortniteStatsPresentation.PlacementSummary(match),
            PlacementSteps = match.PlayersLeft,
            PlacementEliminations = match.Events.Where(e => e.Kind == "kill" && e.DemoSeconds is not null).Select(e => e.DemoSeconds!.Value).ToArray(),
            PlacementEndSeconds = end,
            ClipStartSeconds = window?.StartSeconds ?? double.NaN,
            ClipEndSeconds = window?.EndSeconds ?? double.NaN,
            MatchDetails = FortniteStatsPresentation.MatchDetails(match).Select(item => new MatchDetailRowViewModel(item.Label, item.Value)).ToArray(),
            Footnote = alignmentNote
        };
    }

    public static MatchStatsViewModel From(MatchStats match)
    {
        var win = AppThemeService.Brush("Semantic_8BD9AE", "#8BD9AE");
        var loss = AppThemeService.Brush("Semantic_F05A63", "#F05A63");
        var neutral = AppThemeService.Brush("TextMuted", "#A8B5C3");
        var result = MatchStatsPresentation.ResultLabel(match);
        var mine = match.LocalTeam;
        var theirs = match.OpposingTeam;

        var rounds = MatchStatsPresentation.Rounds(match).Select(cell => new MatchRoundCellViewModel(
            cell.Number.ToString(),
            cell.Won switch { true => win, false => loss, _ => neutral },
            new Thickness(cell.StartsHalf ? 8 : 0, 0, 2, 3),
            $"Round {cell.Number} · {(cell.Won switch { true => "Won", false => "Lost", _ => "Result unknown" })} · {cell.Reason}")).ToArray();

        var maxScore = Math.Max(1, match.Players.Select(player => player.Score ?? player.Kills).DefaultIfEmpty(1).Max());
        MatchTeamBoardViewModel Board(MatchTeam team, bool local)
        {
            var players = match.Players.Where(player => player.TeamId == team.Id)
                .OrderByDescending(player => player.Score ?? player.Kills).ThenByDescending(player => player.Kills)
                .Select(player => new MatchPlayerRowViewModel(
                    player.Name,
                    $"{player.Kills}/{player.Deaths}/{player.Assists}",
                    player.Adr is { } adr ? $"{adr:0} ADR" : player.Score is { } score ? $"{score} pts" : string.Empty,
                    BarMaxWidth * (player.Score ?? player.Kills) / maxScore,
                    player.IsLocal,
                    player.IsLocal ? FontWeight.SemiBold : FontWeight.Normal))
                .ToArray();
            var title = mine is null || theirs is null || mine.Score == theirs.Score ? team.Name
                : (team.Score > (local ? theirs.Score : mine.Score)) ? "Victory" : "Defeat";
            return new MatchTeamBoardViewModel(title, team.Score.ToString(), title == "Victory" ? win : title == "Defeat" ? loss : neutral, players);
        }

        var teams = new List<MatchTeamBoardViewModel>();
        if (mine is not null) teams.Add(Board(mine, true));
        if (theirs is not null) teams.Add(Board(theirs, false));

        var footnote = match.HasDemoData ? string.Empty
            : match.DemoStatus == "expired" ? "Valve no longer keeps this match's demo, so only your own stats are shown."
            : match.DemoStatus == "failed" ? "This match's demo couldn't be read, so only your own stats are shown."
            : "The full scoreboard, ADR and radar appear once ClypDat reads this match's demo. Link your CS2 match history in Settings › Game Stats.";

        return new MatchStatsViewModel
        {
            HasMatch = true,
            PlayerName = match.LocalPlayerName ?? match.LocalPlayer?.Name ?? "Your match",
            MatchLabel = $"{MatchStatsPresentation.MapDisplayName(match.Map)} · {MatchStatsPresentation.ModeDisplayName(match.Mode)}",
            ResultLabel = result,
            ScoreLabel = mine is not null && theirs is not null ? $"{mine.Score} – {theirs.Score}" : string.Empty,
            ResultBrush = result == "Victory" ? win : result == "Defeat" ? loss : neutral,
            FullTiles = MatchStatsPresentation.Tiles(match),
            RadarAxes = MatchStatsPresentation.RadarAxes(match),
            Rounds = rounds,
            Teams = teams,
            Footnote = footnote
        };
    }
}
