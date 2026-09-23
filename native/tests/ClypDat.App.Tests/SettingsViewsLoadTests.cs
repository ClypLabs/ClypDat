using ClypDat.App.Views.Settings;
using Xunit;

namespace ClypDat.App.Tests;

// Compiled XAML checks bindings and property names at build time, but
// resources are still resolved when a view is loaded: a misspelled token key,
// or a style resource only defined in some themes, builds cleanly and then
// throws the first time Settings opens.
public sealed class SettingsViewsLoadTests
{
    [Fact]
    public void SettingsPanel_AndEverySection_Load()
    {
        AvaloniaTestThread.Run(() =>
        {
            var panel = new SettingsPanel();
            Assert.NotNull(panel.Content);
        }, TimeSpan.FromSeconds(60), "Loading the settings views did not finish.");
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
