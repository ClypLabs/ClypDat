using ClypDat.App.Services;
using Xunit;

namespace ClypDat.App.Tests;

public sealed class StandaloneGameDiscoveryTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "ClypDat-discovery-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public void RecognizesMetadataFreePsychInstallation()
    {
        var game = Path.Combine(_root, "Renamed mod");
        Directory.CreateDirectory(Path.Combine(game, "assets", "songs"));
        Directory.CreateDirectory(Path.Combine(game, "assets", "characters"));
        File.WriteAllText(Path.Combine(game, "anything.exe"), "");

        var match = StandaloneGameClassifier.Classify(Path.Combine(game, "anything.exe"));

        Assert.Equal(StandaloneClassificationKind.RecognizedGame, match.Kind);
        Assert.Equal("Renamed mod", match.DisplayName);
    }

    [Theory]
    [InlineData("obs64.exe")]
    public void ExcludesKnownSoftware(string executable) => Assert.True(StandaloneGameClassifier.IsExcluded(null, executable));

    public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, true); }
}
