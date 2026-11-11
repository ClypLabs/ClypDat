using System.Text.Json;
using ClypDat.App.Services;
using Xunit;

namespace ClypDat.App.Tests;

public sealed class NightlyBuildTests
{
    [Theory]
    [InlineData("feat(cs2): split rounds\n\nNightly: Rounds show each half.", "new", "Rounds show each half.")]
    [InlineData("fix: clip whole 4K", "fixed", "Clip whole 4K")]
    [InlineData("chore: bump deps\n\nNightly: Behind-the-scenes improvements.", "other", "Behind-the-scenes improvements.")]
    [InlineData("Update readme", "other", "Update readme")]
    public void ChangeUsesNightlyTrailerOrSubject(string message, string kind, string text)
    {
        var change = NightlyBuild.Change(message);
        Assert.NotNull(change);
        Assert.Equal(kind, change!.Kind);
        Assert.Equal(text, change.Text);
    }

    [Fact]
    public void ParseChangesListsNewestFirstAndSkipsMerges()
    {
        using var document = JsonDocument.Parse("""
            {"commits":[
              {"parents":[{}],"commit":{"message":"fix: first"}},
              {"parents":[{},{}],"commit":{"message":"Merge branch"}},
              {"parents":[{}],"commit":{"message":"feat: second"}}
            ]}
            """);
        var changes = NightlyBuild.ParseChanges(document.RootElement);
        Assert.Equal(["Second", "First"], changes.Select(change => change.Text));
    }
}
