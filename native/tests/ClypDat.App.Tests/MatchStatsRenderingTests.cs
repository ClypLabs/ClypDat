using Avalonia;
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
            chart.Measure(new Size(width, 210));
            chart.Arrange(new Rect(0, 0, width, 210));
            using var target = new RenderTargetBitmap(new PixelSize(width, 210), new Vector(96, 96));
            target.Render(chart);
            Assert.Equal(zeroBuild ? 4 : 5, chart.LabelBounds.Count);
            Assert.All(chart.LabelBounds, bounds => Assert.True(new Rect(chart.Bounds.Size).Inflate(0.01).Contains(bounds)));
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
