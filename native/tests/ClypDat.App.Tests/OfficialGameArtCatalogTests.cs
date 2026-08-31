using System.Text.Json;
using ClypDat.App.Services;
using Xunit;

namespace ClypDat.App.Tests;

public sealed class OfficialGameArtCatalogTests
{
    [Fact]
    public void CatalogAssets_AreSquarePngsAtLeast512_AndHaveOfficialSourceUrls()
    {
        var root = FindRepositoryRoot();
        var manifestPath = Path.Combine(root, "native", "official-game-art.json");
        var manifest = JsonSerializer.Deserialize<OfficialGameArtManifest>(File.ReadAllText(manifestPath),
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;

        foreach (var game in manifest.Games)
        {
            Assert.True(Uri.TryCreate(game.OfficialSourceUrl, UriKind.Absolute, out var source) && source.Scheme == Uri.UriSchemeHttps);
            var path = Path.Combine(root, "native", "official-game-art", game.Asset);
            Assert.True(File.Exists(path), $"Missing catalog asset: {game.Asset}");
            using var stream = File.OpenRead(path);
            Assert.Equal(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }, ReadExactly(stream, 8));
            stream.Position = 16;
            var width = ReadBigEndianInt32(stream);
            var height = ReadBigEndianInt32(stream);
            Assert.Equal(width, height);
            Assert.True(width >= 512, $"{game.Asset} is only {width}px.");
        }
    }

    private static string FindRepositoryRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
            if (File.Exists(Path.Combine(directory.FullName, "native", "official-game-art.json"))) return directory.FullName;
        throw new DirectoryNotFoundException("Could not find ClypDat repository root.");
    }

    private static byte[] ReadExactly(Stream stream, int count)
    {
        var bytes = new byte[count];
        Assert.Equal(count, stream.Read(bytes));
        return bytes;
    }

    private static int ReadBigEndianInt32(Stream stream)
    {
        var bytes = ReadExactly(stream, 4);
        return (bytes[0] << 24) | (bytes[1] << 16) | (bytes[2] << 8) | bytes[3];
    }
}
