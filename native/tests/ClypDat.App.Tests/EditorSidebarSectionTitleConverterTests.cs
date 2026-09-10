using System.Globalization;
using ClypDat.App.Converters;
using ClypDat.App.ViewModels;
using Xunit;

namespace ClypDat.App.Tests;

public sealed class EditorSidebarSectionTitleConverterTests
{
    [Theory]
    [InlineData(EditorSidebarSection.Info, "Info")]
    [InlineData(EditorSidebarSection.Effects, "Effects")]
    [InlineData(EditorSidebarSection.Overlays, "Overlays")]
    [InlineData(EditorSidebarSection.Export, "Export")]
    public void EverySidebarSectionHasTitle(EditorSidebarSection section, string expected)
    {
        Assert.Equal(expected, EditorSidebarSectionTitleConverter.Instance.Convert(section, typeof(string), null, CultureInfo.InvariantCulture));
    }
}
