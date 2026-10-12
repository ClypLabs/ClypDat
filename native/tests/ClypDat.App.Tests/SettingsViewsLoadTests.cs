using System.Runtime.CompilerServices;
using Avalonia.Controls;
using Avalonia.LogicalTree;
using ClypDat.App.Views.Settings;
using Xunit;
using Xunit.Abstractions;

namespace ClypDat.App.Tests;

// Compiled XAML checks bindings and property names at build time, but
// resources are still resolved when a view is loaded: a misspelled token key,
// or a style resource only defined in some themes, builds cleanly and then
// throws the first time Settings opens.
public sealed class SettingsViewsLoadTests(ITestOutputHelper output)
{
    [Fact]
    public void SettingsPanel_AndEverySection_Load()
    {
        AvaloniaTestThread.Run(() =>
        {
            var panel = new SettingsPanel();
            Assert.NotNull(panel.Content);
            var sections = Assert.IsType<StackPanel>(panel.ScrollViewer.Content);
            Assert.Empty(sections.Children);
            foreach (var name in new[]
            {
                "Replay Buffer", "Video Overlays", "Overlays and Notifications", "Appearance", "Discord Rich Presence", "General",
                "Account", "Game Detection", "Import Clips", "Auto-Clip", "Game Stats", "Audio", "Advanced", "Custom Game Settings",
                "Game Audio Exclusions", "About"
            })
            {
                panel.UpdateSections(true, name, string.Empty);
                Assert.Single(sections.Children);
            }
            panel.UpdateSections(false, "About", string.Empty);
            Assert.Empty(sections.Children);
        }, TimeSpan.FromSeconds(60), "Loading the settings views did not finish.");
    }

    [Fact]
    public void SearchKeepsMatchingSectionsAndTheirEditorsInTheOriginalOrder()
    {
        AvaloniaTestThread.Run(() =>
        {
            var panel = new SettingsPanel();
            var search = panel.SearchBox;
            var scroll = panel.ScrollViewer;
            var sections = Assert.IsType<StackPanel>(scroll.Content);
            panel.UpdateSections(true, "Audio", string.Empty);
            var audio = Assert.IsType<AudioSection>(Assert.Single(sections.Children));
            panel.UpdateSections(true, "Audio", "Audio");
            Assert.Equal([typeof(AudioSection), typeof(AdvancedSection), typeof(GameAudioExclusionsSection)], sections.Children.Select(view => view.GetType()));
            Assert.Same(audio, sections.Children[0]);
            panel.UpdateSections(true, "Advanced", "Audio");
            Assert.Same(audio, sections.Children[0]);
            var advanced = sections.Children[1];
            panel.UpdateSections(true, "Advanced", string.Empty);
            Assert.Same(advanced, Assert.Single(sections.Children));
            panel.UpdateSections(true, "Advanced", "there_is_no_setting_with_this_name");
            Assert.Empty(sections.Children);
            Assert.Same(search, panel.SearchBox);
            Assert.Same(scroll, panel.ScrollViewer);
        }, TimeSpan.FromSeconds(30), "Lazy settings search did not finish.");
    }

    [Fact]
    public void HiddenSettingsReleaseAllSectionsAndReplayQualityNavigationCreatesItsTarget()
    {
        AvaloniaTestThread.Run(() =>
        {
            var panel = new SettingsPanel();
            var sections = Assert.IsType<StackPanel>(panel.ScrollViewer.Content);
            panel.UpdateSections(false, "Replay Buffer", "Audio");
            Assert.Empty(sections.Children);
            panel.UpdateSections(true, "Replay Buffer", string.Empty);
            var replay = Assert.IsType<ReplayBufferSection>(Assert.Single(sections.Children));
            Assert.Single(replay.GetLogicalDescendants().OfType<Border>(), border => border.Name == "RecordingQualityCard");
            panel.UpdateSections(false, "Replay Buffer", string.Empty);
            Assert.Empty(sections.Children);
            Assert.Null(replay.Parent);
            panel.UpdateSections(true, "Replay Buffer", string.Empty);
            Assert.NotSame(replay, Assert.Single(sections.Children));
        }, TimeSpan.FromSeconds(30), "Lazy settings release did not finish.");
    }

    [Fact]
    public void HeadlessControlCountsCompareEagerAndLazySettingsTrees()
    {
        AvaloniaTestThread.Run(() =>
        {
            var panel = new SettingsPanel();
            static int Count(Control tree) => 1 + tree.GetLogicalDescendants().OfType<Control>().Count();
            var hidden = Count(panel);
            var eager = new StackPanel
            {
                Children =
                {
                    new ReplayBufferSection(), new VideoOverlaysSection(), new OverlaysSection(), new ThemesSection(),
                    new DiscordRichPresenceSection(), new GeneralSection(), new IntegrationsSection(), new GameDetectionSection(),
                    new ImportClipsSection(), new AutoClipSection(), new GameStatsSection(), new AudioSection(), new AdvancedSection(),
                    new CustomGameSection(), new GameAudioExclusionsSection(), new AboutSection()
                }
            };
            var eagerCount = hidden + Count(eager) - 1;
            panel.UpdateSections(true, "General", string.Empty);
            var generalCount = Count(panel);
            Assert.True(eagerCount > generalCount);
            Assert.True(generalCount > hidden);
            panel.UpdateSections(false, "General", string.Empty);
            Assert.Equal(hidden, Count(panel));
            output.WriteLine($"Headless logical controls: eager Settings={eagerCount}; lazy hidden={hidden}; lazy General={generalCount}.");
            eager.Children.Clear();
        }, TimeSpan.FromSeconds(60), "Settings control-count measurement did not finish.");
    }

    [Fact]
    public void ClosingSettingsDoesNotKeepVisitedSectionTreesAlive()
    {
        AvaloniaTestThread.Run(() =>
        {
            var panel = new SettingsPanel();
            var visits = Enumerable.Range(0, 5).Select(_ => VisitAndClose(panel)).ToArray();
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
            Assert.All(visits, visit => Assert.False(visit.IsAlive));
            GC.KeepAlive(panel);
        }, TimeSpan.FromSeconds(30), "Settings section lifetime check did not finish.");
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference VisitAndClose(SettingsPanel panel)
    {
        panel.UpdateSections(true, "Replay Buffer", string.Empty);
        var view = Assert.Single(Assert.IsType<StackPanel>(panel.ScrollViewer.Content).Children);
        var reference = new WeakReference(view);
        panel.UpdateSections(false, "Replay Buffer", string.Empty);
        return reference;
    }

    // The About page's licences dialog renders the real notice file, every
    // section of it: a heading per bundled component, nothing silently lost.
    [Fact]
    public void LicenceDialog_RendersEverySectionOfTheNoticeFile()
    {
        var path = Path.Combine(RepositoryRoot(), "THIRD-PARTY-LICENSES.md");
        var markdown = File.ReadAllText(path);
        var headings = markdown.Split('\n').Count(line => line.TrimStart().StartsWith('#'));

        AvaloniaTestThread.Run(() =>
        {
            var view = Assert.IsType<Avalonia.Controls.StackPanel>(ClypDat.App.Views.LicenceDocumentView.Build(markdown));
            var rendered = view.Children.OfType<Avalonia.Controls.TextBlock>()
                .Count(text => text.FontWeight >= Avalonia.Media.FontWeight.SemiBold);
            Assert.Equal(headings, rendered);
        }, TimeSpan.FromSeconds(30), "Rendering the licence notices did not finish.");
    }

    // Option lists swap Fluent's ListBox template for the sliding one through a
    // style setter; this is the check that a real window actually applies it
    // and the highlight lands on the chosen item.
    [Fact]
    public void OptionList_UsesSlidingIndicator_OnTheSelectedItem()
    {
        AvaloniaTestThread.Run(() =>
        {
            var list = new Avalonia.Controls.ListBox
            {
                Classes = { "settingsSegmented" },
                ItemsSource = new[] { "Low", "Medium", "High" },
                SelectedIndex = 1
            };
            var window = new Avalonia.Controls.Window { Width = 400, Height = 200, Content = list };
            try
            {
                window.Show();
                window.UpdateLayout();

                var indicator = Avalonia.VisualTree.VisualExtensions.GetVisualDescendants(list)
                    .OfType<Avalonia.Controls.Border>()
                    .Single(border => border.Name == "PART_SelectionIndicator");
                var selected = Assert.IsAssignableFrom<Avalonia.Controls.Control>(list.ContainerFromIndex(1));
                Assert.Equal(1, indicator.Opacity);
                Assert.Equal(selected.Bounds.Width, indicator.Bounds.Width, 1);

                list.SelectedIndex = 2;
                window.UpdateLayout();
                Assert.Equal(1, indicator.Opacity);
            }
            finally
            {
                window.Close();
            }
        }, TimeSpan.FromSeconds(30), "The option list test did not finish.");
    }

    private static string RepositoryRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "THIRD-PARTY-LICENSES.md"))) return directory.FullName;
        }
        throw new DirectoryNotFoundException("THIRD-PARTY-LICENSES.md was not found above the test output.");
    }

    // The stored value is a settings key and never changes; only the label does.
    [Theory]
    [InlineData("Top Left", "Top left")]
    [InlineData("Center Right", "Centre right")]
    [InlineData("Bottom Right", "Bottom right")]
    public void OverlayPositionLabel_IsSentenceCaseWithAustralianSpelling(string stored, string shown)
    {
        Assert.Equal(shown, ClypDat.App.Converters.OverlayPositionLabelConverter.Label(stored));
    }
}
