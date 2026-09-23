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
