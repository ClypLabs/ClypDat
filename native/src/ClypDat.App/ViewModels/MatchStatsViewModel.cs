using Avalonia;
using Avalonia.Media;
using ClypDat.App.Services;

namespace ClypDat.App.ViewModels;

public sealed record MatchRoundCellViewModel(string Number, IBrush Fill, Thickness Margin, string Tip);

public sealed record MatchPlayerRowViewModel(string Name, string Kda, string Detail, double BarWidth, bool IsLocal, FontWeight Weight);

public sealed record MatchTeamBoardViewModel(string Title, string Score, IBrush Accent, IReadOnlyList<MatchPlayerRowViewModel> Players);

// What the editor's Stats panel binds to. Built once per opened clip, off
// the match it overlaps; immutable afterwards, so no change notification.
public sealed class MatchStatsViewModel
{
    private const double BarMaxWidth = 64;

    private MatchStatsViewModel() { }

    public bool HasMatch { get; private init; }
    public string EmptyMessage { get; private init; } = string.Empty;
    public string PlayerName { get; private init; } = string.Empty;
    public string MatchLabel { get; private init; } = string.Empty;
    public string ResultLabel { get; private init; } = string.Empty;
    public string ScoreLabel { get; private init; } = string.Empty;
    public IBrush ResultBrush { get; private init; } = Brushes.Transparent;
    public IReadOnlyList<MatchStatTile> Tiles { get; private init; } = [];
    public IReadOnlyList<MatchRadarAxis> RadarAxes { get; private init; } = [];
    public bool HasRadar => RadarAxes.Count > 0;
    public IReadOnlyList<MatchRoundCellViewModel> Rounds { get; private init; } = [];
    public bool HasRounds => Rounds.Count > 0;
    public IReadOnlyList<MatchTeamBoardViewModel> Teams { get; private init; } = [];
    public string Footnote { get; private init; } = string.Empty;
    public bool HasFootnote => Footnote.Length > 0;

    public static MatchStatsViewModel Empty(string message) => new() { EmptyMessage = message };

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
            : "The full scoreboard, ADR and radar appear once ClypDat reads this match's demo. Link your CS2 match history in Settings › Auto-Clipping.";

        return new MatchStatsViewModel
        {
            HasMatch = true,
            PlayerName = match.LocalPlayerName ?? match.LocalPlayer?.Name ?? "Your match",
            MatchLabel = $"{MatchStatsPresentation.MapDisplayName(match.Map)} · {MatchStatsPresentation.ModeDisplayName(match.Mode)}",
            ResultLabel = result,
            ScoreLabel = mine is not null && theirs is not null ? $"{mine.Score} – {theirs.Score}" : string.Empty,
            ResultBrush = result == "Victory" ? win : result == "Defeat" ? loss : neutral,
            Tiles = MatchStatsPresentation.Tiles(match),
            RadarAxes = MatchStatsPresentation.RadarAxes(match),
            Rounds = rounds,
            Teams = teams,
            Footnote = footnote
        };
    }
}
