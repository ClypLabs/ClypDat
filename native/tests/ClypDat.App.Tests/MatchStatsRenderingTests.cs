using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.LogicalTree;
using ClypDat.App.Controls;
using ClypDat.App.Services;
using ClypDat.App.ViewModels;
using Xunit;

namespace ClypDat.App.Tests;

public sealed class MatchStatsRenderingTests
{
    [Theory]
    [InlineData(Key.Right)]
    [InlineData(Key.Left)]
    [InlineData(Key.Up)]
    [InlineData(Key.Down)]
    [InlineData(Key.Enter)]
    [InlineData(Key.Space)]
    [InlineData(Key.Escape)]
    public void FocusedRadarNavigationRunsBeforeEditorShortcuts(Key key)
    {
        AvaloniaTestThread.Run(() =>
        {
            MatchRadarAxis[] axes = [new("Combat", 0.25, "25"), new("Aim", 0.5, "50"), new("Survival", 0.75, "75")];
            var chart = new MatchRadarChart { Axes = axes, SelectedAxis = axes[0] };
            var root = new Panel();
            root.Children.Add(chart);
            var editorKeys = new List<Key>();
            root.AddHandler(InputElement.KeyDownEvent, (_, e) =>
            {
                if (MatchRadarChart.TryHandleNavigation(e)) return;
                editorKeys.Add(e.Key);
                e.Handled = true;
            }, RoutingStrategies.Tunnel);
            var input = new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = key };
            chart.RaiseEvent(input);
            Assert.Empty(editorKeys);
            Assert.True(input.Handled);
            if (key is Key.Right or Key.Down) Assert.Same(axes[1], chart.SelectedAxis);
            else if (key is Key.Left or Key.Up) Assert.Same(axes[2], chart.SelectedAxis);
            else Assert.Null(chart.SelectedAxis);
            if (key == Key.Escape)
            {
                chart.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Escape });
                Assert.Equal([Key.Escape], editorKeys);
            }
        }, TimeSpan.FromSeconds(15), "Graph keyboard routing did not finish.");
    }

    [Fact]
    public void RefreshKeepsSelectionUsingTheNewAxisAndUpdatedExplanation()
    {
        var match = new MatchStats
        {
            Game = "fortnite", ZeroBuild = true, HasDemoData = true, Placement = 12, PlayerCount = 100, LocalEndSeconds = 210,
            Players = [new() { IsLocal = true, Kills = 2, Damage = 900, AccuracyPercent = 38, LootingPickups = 24 }]
        };
        var previous = MatchStatsViewModel.FromFortnite(match, new(120, 150, 1), string.Empty);
        previous.SelectedScope = MatchStatsViewModel.FullScope;
        previous.SelectedRadarAxis = Assert.Single(previous.RadarAxes, axis => axis.Label == "Combat");
        var updated = match with { Players = [match.LocalPlayer! with { Kills = 4 }] };
        var refreshed = MatchStatsViewModel.FromFortnite(updated, new(120, 150, 1), string.Empty);
        refreshed.RestoreSelectionFrom(previous);
        Assert.Equal(MatchStatsViewModel.FullScope, refreshed.SelectedScope);
        Assert.Same(Assert.Single(refreshed.RadarAxes, axis => axis.Label == "Combat"), refreshed.SelectedRadarAxis);
        Assert.NotSame(previous.SelectedRadarAxis, refreshed.SelectedRadarAxis);
        Assert.Contains("4 eliminations", refreshed.RadarExplanation);
        previous.SelectedRadarAxis = null;
        var afterDismissal = MatchStatsViewModel.FromFortnite(updated, new(120, 150, 1), string.Empty);
        afterDismissal.RestoreSelectionFrom(previous);
        Assert.False(afterDismissal.HasRadarExplanation);
        var builds = MatchStatsViewModel.FromFortnite(match with { ZeroBuild = false }, null, string.Empty);
        builds.SelectedRadarAxis = Assert.Single(builds.RadarAxes, axis => axis.Label == "Building");
        var zeroBuild = MatchStatsViewModel.FromFortnite(match, null, string.Empty);
        zeroBuild.RestoreSelectionFrom(builds);
        Assert.Null(zeroBuild.SelectedRadarAxis);
    }

    [Theory]
    [InlineData(true, false, false)]
    [InlineData(false, true, false)]
    [InlineData(false, false, true)]
    public void CompleteRadarScoresKeepWarningsAboutIncompleteReplayData(bool partialLooting, bool incompleteReplay, bool missingEvents)
    {
        var match = new MatchStats
        {
            Game = "fortnite", ZeroBuild = true, HasDemoData = true, Placement = 12, PlayerCount = 100, LocalEndSeconds = 210,
            ReplayDetailsIncomplete = incompleteReplay, LocalEventsAvailable = !missingEvents,
            Players = [new() { IsLocal = true, Kills = 2, Damage = 900, AccuracyPercent = 38, LootingPickups = 24, LootingPartial = partialLooting }]
        };
        var model = MatchStatsViewModel.FromFortnite(match, null, string.Empty);
        Assert.All(model.RadarAxes, axis => Assert.True(axis.Available));
        Assert.True(model.HasRadarNote);
        var expected = partialLooting ? FortniteStatsPresentation.LootingPartial
            : incompleteReplay ? "Some replay data couldn't be read" : "Your event totals are unavailable";
        Assert.Contains(expected, model.RadarNote);
        var complete = MatchStatsViewModel.FromFortnite(match with
        {
            ReplayDetailsIncomplete = false, LocalEventsAvailable = true,
            Players = [match.LocalPlayer! with { LootingPartial = false }]
        }, null, string.Empty);
        Assert.False(complete.HasRadarNote);
    }

    [Fact]
    public void Cs2UsesTheSharedClickableRadarLabelsAndOutsideClickDismissal()
    {
        var model = MatchStatsViewModel.From(new MatchStats
        {
            Game = "cs2", HasDemoData = true,
            Players = [new() { IsLocal = true, Kills = 12, HeadshotKills = 6, Deaths = 8, RoundsPlayed = 20, Damage = 1600 }]
        });
        AvaloniaTestThread.Run(() =>
        {
            var panel = new MatchStatsPanel { DataContext = model };
            var chart = Assert.Single(panel.GetLogicalDescendants().OfType<MatchRadarChart>());
            chart.Measure(new Size(300, chart.Height));
            chart.Arrange(new Rect(0, 0, 300, chart.Height));
            using var target = new RenderTargetBitmap(new PixelSize(300, (int)chart.Height), new Vector(96, 96));
            target.Render(chart);
            Assert.Equal(5, chart.LabelBounds.Count);
            Assert.All(model.RadarAxes, axis => Assert.NotEmpty(axis.Explanation));
            Assert.True(chart.SelectAxisAt(chart.LabelBounds[0].Center));
            Assert.Equal("Aim · 73/100", model.RadarExplanationTitle);
            Assert.Contains("50% HS", model.RadarExplanation);
            panel.DismissRadarSelectionAt(new Point(-20, -20));
            Assert.False(model.HasRadarExplanation);
        }, TimeSpan.FromSeconds(20), "Shared CS2 radar labels did not render.");
    }

    [Theory]
    [InlineData(true, 400)]
    [InlineData(false, 400)]
    [InlineData(true, 240)]
    [InlineData(false, 240)]
    public void FortniteRadarLabelsFitAndClicksShowTheirScoreInputs(bool zeroBuild, int width)
    {
        var match = new MatchStats
        {
            Game = "fortnite", ZeroBuild = zeroBuild, HasDemoData = true, HasMatchSummary = true,
            Placement = 12, PlayerCount = 100, LocalEndSeconds = 210,
            Players = [new() { IsLocal = true, Kills = 2, Damage = 900, AccuracyPercent = 38, LootingPickups = 24, LootingPartial = true }]
        };
        var model = MatchStatsViewModel.FromFortnite(match, null, string.Empty);
        AvaloniaTestThread.Run(() =>
        {
            var panel = new MatchStatsPanel { DataContext = model, Width = width };
            // Exercise the real XAML binding without opening a native window.
            var chart = Assert.Single(panel.GetLogicalDescendants().OfType<MatchRadarChart>());
            chart.Measure(new Size(width, chart.Height));
            chart.Arrange(new Rect(0, 0, width, chart.Height));
            using var target = new RenderTargetBitmap(new PixelSize(width, (int)chart.Height), new Vector(96, 96));
            target.Render(chart);
            Assert.Equal(zeroBuild ? 4 : 5, chart.LabelBounds.Count);
            Assert.All(chart.LabelBounds, bounds => Assert.True(new Rect(chart.Bounds.Size).Inflate(0.01).Contains(bounds)));
            Point LabelCentre(string label) => chart.LabelBounds[model.RadarAxes.ToList().FindIndex(axis => axis.Label == label)].Center;
            Assert.Equal(chart.Bounds.Width / 2, LabelCentre("Survival").X, 3);
            Assert.Equal(chart.Bounds.Width / 2, LabelCentre("Looting").X, 3);
            Assert.True(LabelCentre("Survival").Y < chart.Bounds.Height / 2);
            Assert.True(LabelCentre("Looting").Y > chart.Bounds.Height / 2);
            Assert.Equal(chart.Bounds.Height / 2 + 4, LabelCentre("Combat").Y, 3);
            Assert.Equal(chart.Bounds.Height / 2 + 4, LabelCentre("Aim").Y, 3);
            Assert.True(LabelCentre("Combat").X > chart.Bounds.Width / 2);
            Assert.True(LabelCentre("Aim").X < chart.Bounds.Width / 2);
            Assert.False(chart.SelectAxisAt(new Point(-20, -20)));
            for (var i = 0; i < model.RadarAxes.Count; i++)
            {
                var axis = model.RadarAxes[i];
                Assert.True(chart.SelectAxisAt(chart.LabelBounds[i].Center));
                Assert.Same(axis, model.SelectedRadarAxis);
                Assert.True(model.HasRadarExplanation);
                Assert.Contains(axis.Label, model.RadarExplanationTitle);
                var expected = axis.Label switch
                {
                    "Combat" => "2 eliminations contributed 10/50",
                    "Aim" => "38% accuracy",
                    "Survival" => "3:30 in the match contributed 7/40",
                    "Looting" => "24 confirmed pickups",
                    "Building" => "No verified building activity",
                    _ => throw new InvalidOperationException(axis.Label)
                };
                Assert.Contains(expected, model.RadarExplanation);
                if (axis.Label == "Looting")
                {
                    Assert.Contains("≥24/100", model.RadarExplanationTitle);
                    Assert.Contains("confirmed minimum", model.RadarExplanation);
                }
            }
            panel.DismissRadarSelectionAt(chart.LabelBounds[^1].Center);
            Assert.True(model.HasRadarExplanation);
            panel.DismissRadarSelectionAt(new Point(-20, -20));
            Assert.False(model.HasRadarExplanation);
            Assert.True(chart.SelectAxisAt(chart.LabelBounds[^1].Center));
            Assert.True(chart.SelectAxisAt(chart.LabelBounds[^1].Center));
            Assert.False(model.HasRadarExplanation);
        }, TimeSpan.FromSeconds(20), "Radar labels or score explanations did not render.");
    }

    [Fact]
    public void WorkerReadsTheCurrentThemeWithoutAccessingItsMutableResources()
    {
        AvaloniaTestThread.Run(() =>
        {
            var app = Application.Current!;
            try
            {
                AppThemeService.Apply(app, "Berry", Colors.Blue, false);
                var expected = AppThemeService.ThemeColor("AccentBrush", "#5864E8");
                Color actual = default;
                Exception? failure = null;
                var worker = new Thread(() =>
                {
                    try { actual = ((ISolidColorBrush)AppThemeService.Brush("AccentBrush", "#5864E8")).Color; }
                    catch (Exception error) { failure = error; }
                }) { IsBackground = true };
                worker.Start();
                Assert.True(worker.Join(TimeSpan.FromSeconds(3)), "Background theme lookup waited for the UI thread.");
                Assert.Null(failure);
                Assert.Equal(expected, actual);
            }
            finally { AppThemeService.Apply(app, "System", Colors.Blue, false); }
        }, TimeSpan.FromSeconds(15), "Background theme lookup did not finish.");
    }

    [Fact]
    public async Task BackgroundFallbackBrushCanBeUsedOnTheRenderThread()
    {
        var brush = await Task.Run(() => AppThemeService.Brush("Missing_match_stats_test_brush", "#CB8CFF"));
        AvaloniaTestThread.Run(() =>
        {
            Assert.Equal(Color.Parse("#CB8CFF"), ((ISolidColorBrush)brush).Color);
            var marker = new Avalonia.Controls.Border { Background = brush, Width = 40, Height = 40 };
            marker.Measure(new Size(40, 40));
            marker.Arrange(new Rect(0, 0, 40, 40));
            using var target = new RenderTargetBitmap(new PixelSize(40, 40), new Vector(96, 96));
            target.Render(marker);
        }, TimeSpan.FromSeconds(15), "Fallback brush never rendered on the UI thread.");
    }

    [Fact]
    public async Task FortnitePanelBuiltOnWorkerRendersBothScopes()
    {
        var match = new MatchStats
        {
            Game = "fortnite", Mode = "Zero Build · Solo", HasDemoData = true, HasMatchSummary = true,
            ZeroBuild = true, Placement = 12, PlayerCount = 100, LocalEndSeconds = 210, DemoDurationSeconds = 300,
            LocalPlayerName = "You", Players = [new() { IsLocal = true, Kills = 2, Damage = 900, AccuracyPercent = 38 }],
            PlayersLeft = [new(0, 100), new(125, 90), new(210, 12)], PlayersLeftAtEnd = 12,
            Events = [new() { Kind = "kill", DemoSeconds = 125 }, new() { Kind = "knock", DemoSeconds = 130 }, new() { Kind = "death", DemoSeconds = 210 }]
        };
        var model = await Task.Run(() => MatchStatsViewModel.FromFortnite(match, new(120, 150, 1), ""));
        AvaloniaTestThread.Run(() =>
        {
            // Renderers read these values while recording/serializing draws.
            // Standalone controls can leave virtualized rows unrealized, so
            // check every bound brush as well as drawing the complete panel.
            foreach (var brush in model.Timeline.Concat(model.ClipEvents).SelectMany(row => new[] { row.Dot, row.Background }).Append(model.ResultBrush))
                _ = ((ISolidColorBrush)brush).Color;
            var panel = new MatchStatsPanel { DataContext = model, Width = 400 };
            foreach (var scope in model.Scopes)
            {
                model.SelectedScope = scope;
                panel.Measure(new Size(400, 1800));
                panel.Arrange(new Rect(0, 0, 400, 1800));
                panel.UpdateLayout();
                using var target = new RenderTargetBitmap(new PixelSize(400, 1800), new Vector(96, 96));
                target.Render(panel);
            }
        }, TimeSpan.FromSeconds(20), "Fortnite Stats panel did not render.");
    }
}
