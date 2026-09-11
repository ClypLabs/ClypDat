using System.Reflection;
using System.Collections.ObjectModel;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using ClypDat.App.Services;
using ClypDat.App.ViewModels;
using ClypDat.Core.Settings;
using Xunit;

namespace ClypDat.App.Tests;

public sealed class SteamGameLibraryTests
{
    [Theory]
    [InlineData(39)]
    [InlineData(40)]
    [InlineData(41)]
    public void ReadsCommonTypeAcrossFormats(int version)
    {
        var kinds = SteamAppInfoReader.Parse(AppInfo(version,
            (730, "Game"), (620980, "game"), (438100, "Game"), (100, "Demo"), (101, "Beta"),
            (1905180, "Application"), (1009850, "Application"), (250820, "Tool"), (102, null)));
        foreach (var id in new[] { 730, 620980, 438100, 100, 101 }) Assert.Equal(SteamAppKind.Game, kinds[id]);
        foreach (var id in new[] { 1905180, 1009850, 250820 }) Assert.Equal(SteamAppKind.NonGame, kinds[id]);
        Assert.Equal(SteamAppKind.Unknown, kinds[102]);
    }

    [Theory]
    [InlineData(39)]
    [InlineData(40)]
    [InlineData(41)]
    public void RejectsEveryTruncationWithoutPublishingPartialRecords(int version)
    {
        var bytes = AppInfo(version, (730, "Game"), (1905180, "Application"));
        for (var length = 0; length < bytes.Length; length++)
            Assert.ThrowsAny<Exception>(() => SteamAppInfoReader.Parse(bytes[..length]));
    }

    [Fact]
    public void RejectsUnsupportedFormatBadBoundsAndCorruptHash()
    {
        var bytes = AppInfo(41, (730, "Game"));
        bytes[0] = 0x30;
        Assert.Throws<InvalidDataException>(() => SteamAppInfoReader.Parse(bytes));
        bytes = AppInfo(41, (730, "Game"));
        BitConverter.GetBytes(long.MaxValue).CopyTo(bytes, 8);
        Assert.Throws<InvalidDataException>(() => SteamAppInfoReader.Parse(bytes));
        bytes = AppInfo(40, (730, "Game"));
        bytes[16 + 60 + 5] ^= 1;
        Assert.Throws<InvalidDataException>(() => SteamAppInfoReader.Parse(bytes));
        bytes = AppInfo(39, (730, "Game"));
        BitConverter.GetBytes(uint.MaxValue).CopyTo(bytes, 12);
        Assert.Throws<InvalidDataException>(() => SteamAppInfoReader.Parse(bytes));
    }

    [Theory]
    [InlineData(1905180, "obs64.exe")]
    [InlineData(1009850, "AdvancedSettings.exe")]
    [InlineData(250820, "vrmonitor.exe")]
    [InlineData(365670, "blender.exe")]
    [InlineData(431960, "wallpaper64.exe")]
    [InlineData(629520, "soundpad.exe")]
    [InlineData(993090, "LosslessScaling.exe")]
    public async Task SoftwareCannotMatchByPathNameCatalogOrCustomOverride(int id, string exe)
    {
        using var fixture = new LibraryFixture();
        var path = fixture.Install(id, exe);
        fixture.Metadata((id, "Application"));
        var library = fixture.Library();
        await library.RefreshAsync();
        Assert.Null(library.FindByExecutablePath(path));
        Assert.Null(library.FindByExecutableName(exe));
        Assert.True(library.IsSoftware(path));
        Assert.True(library.IsSoftware(executableName: exe));
        Assert.True(library.IsSoftware(detectionKey: $"steam-{id}"));

        var detector = new ForegroundGameDetector(library);
        detector.ApplyRemoteCatalog([new() { Id = "test-software", DisplayName = "Software", Matchers = [new() { Executable = exe }] }]);
        Assert.False(detector.MatchWindow(path, exe, "Software", "Window").IsDetected);
        detector.ApplyCustomGameNames([new() { ExecutableName = exe, DisplayName = "Pretend game", Origin = "UserCustom" }]);
        Assert.False(detector.MatchWindow(path, exe, "Software", "Window").IsDetected);
        Assert.False(detector.MatchWindow(Path.Combine(fixture.Root, "elsewhere", exe), exe, "Software", "Window").IsDetected);
        var fallback = typeof(ForegroundGameDetector).GetMethod("TryResolveGameByPath", BindingFlags.NonPublic | BindingFlags.Instance)!;
        Assert.False((bool)fallback.Invoke(detector, [path, null, null, GameMatchSource.None])!);
    }

    [Theory]
    [InlineData(730, "Game")]
    [InlineData(620980, "Game")]
    [InlineData(438100, "Game")]
    [InlineData(111, "Demo")]
    [InlineData(112, "Beta")]
    public async Task VerifiedGamesDemosAndPlaytestsMatchByPathAndRelocatedExecutable(int id, string type)
    {
        using var fixture = new LibraryFixture();
        var exe = $"fixture-{id}.exe";
        var path = fixture.Install(id, exe);
        fixture.Metadata((id, type));
        var library = fixture.Library();
        await library.RefreshAsync();
        Assert.Equal(id, library.FindByExecutablePath(path)?.AppId);
        Assert.Equal(id, library.FindByExecutableName(exe)?.AppId);
        var detector = new ForegroundGameDetector(library);
        Assert.True(detector.MatchWindow(path, exe, "Game", "Window").IsDetected);
        Assert.Equal($"steam-{id}", detector.MatchWindow(Path.Combine(fixture.Root, exe), exe, "Game", "Window").DetectionKey);
    }

    [Fact]
    public async Task ExternalJavaRuntimeIsOnlyMatchedByItsMinecraftWindow()
    {
        using var fixture = new LibraryFixture();
        fixture.Install(108600, "javaw.exe", "Project Zomboid");
        fixture.Metadata((108600, "Game"));
        var library = fixture.Library();
        await library.RefreshAsync();

        var detector = new ForegroundGameDetector(library);
        var minecraftJava = Path.Combine(fixture.Root, "Modrinth", "javaw.exe");

        detector.ApplyRemoteCatalog([new()
        {
            Id = "minecraft-java", DisplayName = "Minecraft",
            Matchers = [new() { Executable = "javaw.exe", ClassEquals = ["GLFW30"], TitleContains = ["Minecraft"] }]
        }]);

        Assert.Equal("Minecraft", detector.MatchWindow(minecraftJava, "javaw.exe", "Minecraft", "GLFW30", processId: 42).DisplayName);
        Assert.False(detector.MatchWindow(minecraftJava, "javaw.exe", "Java configuration", "GLFW30", processId: 42).IsDetected);
    }

    [Fact]
    public async Task UnknownInstallsRequireMetadataButIndependentCatalogAndCustomGamesWork()
    {
        using var fixture = new LibraryFixture();
        var path = fixture.Install(123, "unknown-fixture.exe");
        var library = fixture.Library();
        await library.RefreshAsync();
        Assert.Null(library.FindByExecutablePath(path));
        Assert.Null(library.FindByExecutableName("unknown-fixture.exe"));
        var detector = new ForegroundGameDetector(library);
        Assert.False(detector.MatchWindow(path, "unknown-fixture.exe", "Game", "Window").IsDetected);
        detector.ApplyRemoteCatalog([new() { Id = "independent", DisplayName = "Game", Matchers = [new() { Executable = "unknown-fixture.exe" }] }]);
        Assert.Equal(GameMatchSource.Catalog, detector.MatchWindow(path, "unknown-fixture.exe", "Game", "Window").MatchSource);
        detector.ApplyCustomGameNames([new() { ExecutableName = "custom-fixture.exe", DisplayName = "Custom", Origin = "UserCustom" }]);
        Assert.Equal(GameMatchSource.UserCustom, detector.MatchWindow(Path.Combine(fixture.Root, "custom-fixture.exe"), "custom-fixture.exe", "Game", "Window").MatchSource);
    }

    [Fact]
    public async Task MostSpecificInstallWinsAndAmbiguousNamesDoNotExcludeGames()
    {
        using var fixture = new LibraryFixture();
        var software = fixture.Install(1905180, "shared.exe", "Parent");
        var game = fixture.Install(730, "shared.exe", "Parent/Game");
        fixture.Metadata((1905180, "Application"), (730, "Game"));
        var library = fixture.Library();
        await library.RefreshAsync();
        Assert.True(library.IsSoftware(software));
        Assert.False(library.IsSoftware(game));
        Assert.Equal(730, library.FindByExecutablePath(game)?.AppId);
        Assert.False(library.IsSoftware(executableName: "shared.exe"));
        Assert.Null(library.FindByExecutableName("shared.exe"));
        Assert.Null(library.FindByExecutablePath(fixture.Root + "-neighbor/shared.exe"));
    }

    [Fact]
    public async Task FailedRefreshRetainsLastValidSnapshotAndStoredSettingsCannotRestoreSoftware()
    {
        using var fixture = new LibraryFixture();
        var path = fixture.Install(321, "reclassified.exe");
        fixture.Metadata((321, "Game"));
        var library = fixture.Library();
        await library.RefreshAsync();
        var old = library.Snapshot;
        var settings = new AppSettings();
        settings.GameCaptureOverrides.Add(new() { ExecutableName = "reclassified.exe", ProcessName = "reclassified.exe", DisplayName = "Old software", Origin = "UserCustom" });
        var detector = new ForegroundGameDetector(library);
        detector.ApplyCustomGameNames(settings.GameCaptureOverrides);
        detector.RefreshSteamClassification();
        var detection = detector.MatchWindow(path, "reclassified.exe", "Software", "Window");
        Assert.True(detection.IsDetected);
        typeof(ForegroundGameDetector).GetField("_lastGame", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(detector, detection);
        fixture.Metadata((321, "Application"));
        await library.RefreshAsync(true);
        Assert.NotSame(old, library.Snapshot);
        Assert.Equal(SteamAppKind.Game, old.Classify(321)); // immutable older generation
        detector.RefreshSteamClassification();
        Assert.Equal(GameDetection.None, typeof(ForegroundGameDetector).GetField("_lastGame", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(detector));
        detector.ApplyCustomGameNames(settings.GameCaptureOverrides);
        Assert.False(detector.MatchWindow(path, "reclassified.exe", "Software", "Window").IsDetected);
        Assert.True(library.IsSoftware(executableName: settings.GameCaptureOverrides[0].ProcessName));
        Assert.Single(settings.GameCaptureOverrides);
        var valid = library.Snapshot;
        File.WriteAllBytes(fixture.MetadataPath, [1, 2, 3]);
        await library.RefreshAsync(true);
        Assert.Same(valid, library.Snapshot);
        File.Delete(fixture.MetadataPath);
        await library.RefreshAsync(true);
        Assert.Same(valid, library.Snapshot);
    }

    [Fact]
    public async Task MetadataChangeDuringIndexBuildDiscardsObsoleteResults()
    {
        using var fixture = new LibraryFixture();
        var path = fixture.Install(321, "changing.exe");
        fixture.Metadata((321, "Game"));
        using var entered = new ManualResetEventSlim();
        using var resume = new ManualResetEventSlim();
        var library = fixture.Library(() => { entered.Set(); Assert.True(resume.Wait(TimeSpan.FromSeconds(10))); });
        var refresh = library.RefreshAsync();
        Assert.True(entered.Wait(TimeSpan.FromSeconds(10)));
        fixture.Metadata((321, "Application"));
        File.SetLastWriteTimeUtc(fixture.MetadataPath, DateTime.UtcNow.AddSeconds(1));
        resume.Set();
        await refresh;
        await library.RefreshAsync(true);
        Assert.True(library.IsSoftware(path));
        Assert.Null(library.FindByExecutableName("changing.exe"));
    }

    [Fact]
    public async Task SettingsFilterSoftwareAndRejectManualAddsWithoutChangingStoredSettings()
    {
        using var fixture = new LibraryFixture();
        var path = fixture.Install(321, "software-fixture.exe");
        fixture.Install(730, "game-fixture.exe");
        fixture.Metadata((321, "Application"), (730, "Game"));
        var library = fixture.Library();
        await library.RefreshAsync();
        var settings = new AppSettings();
        settings.GameCaptureOverrides =
        [
            new() { ExecutableName = "steam-321", DisplayName = "Software", ProcessName = "software-fixture.exe" },
            new() { ExecutableName = "software-fixture.exe", DisplayName = "Software custom", Origin = "UserCustom" },
            new() { ExecutableName = "steam-730", DisplayName = "Game", ProcessName = "game-fixture.exe" }
        ];
        settings.CustomGameSettings["steam-321"] = new() { DisplayName = "Software profile" };
        // Exercise the actual settings consumers without starting audio devices,
        // account restoration, timers or reading/writing the user's settings.
        var viewModel = (MainWindowViewModel)RuntimeHelpers.GetUninitializedObject(typeof(MainWindowViewModel));
        void Field(string name, object value) => typeof(MainWindowViewModel)
            .GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(viewModel, value);
        Field("_steamGames", library);
        Field("<Settings>k__BackingField", settings);
        Field("<GameCaptureRows>k__BackingField", new ObservableCollection<GameBackendRowViewModel>());
        Field("<CustomGameCandidates>k__BackingField", new ObservableCollection<GameBackendRowViewModel>());
        Field("<CustomGameTabs>k__BackingField", new ObservableCollection<CustomGameTabViewModel>());
        Field("_gameSearchText", "");
        Field("_customGameSearchText", "");
        typeof(MainWindowViewModel).GetMethod("RebuildGameCaptureRows", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(viewModel, null);
        viewModel.RebuildCustomGameTabs();
        Assert.Equal("steam-730", Assert.Single(viewModel.GameCaptureRows).ExecutableName);
        Assert.Equal("steam-730", Assert.Single(viewModel.CustomGameCandidates).ExecutableName);
        Assert.Empty(viewModel.CustomGameTabs);
        var candidateMethod = typeof(MainWindowViewModel).GetMethod("IsGameCandidate", BindingFlags.Instance | BindingFlags.NonPublic)!;
        Assert.False((bool)candidateMethod.Invoke(viewModel, [new ProcessOption("software-fixture.exe", path)])!);
        Assert.True((bool)candidateMethod.Invoke(viewModel, [new ProcessOption("non-steam-game.exe", Path.Combine(fixture.Root, "non-steam-game.exe"))])!);
        viewModel.NewCustomGameExecutable = path;
        viewModel.NewCustomGameDisplayName = "Pretend game";
        viewModel.AddCustomGame();
        Assert.Contains("software, not a game", viewModel.GameAdditionError);
        viewModel.AddCustomGame("steam-1905180", "OBS profile");
        Assert.Contains("software, not a game", viewModel.GameAdditionError);
        Assert.Equal(3, settings.GameCaptureOverrides.Count);
        Assert.Single(settings.CustomGameSettings);
    }

    [Fact]
    public async Task CachedSteamMatchDisappearsWhenMetadataBecomesUnknown()
    {
        using var fixture = new LibraryFixture();
        var path = fixture.Install(321, "cache-fixture.exe");
        fixture.Metadata((321, "Game"));
        var library = fixture.Library();
        await library.RefreshAsync();
        var detector = new ForegroundGameDetector(library);
        var first = detector.MatchWindow(path, "cache-fixture.exe", "Game", "Window", handle: 42);
        Assert.True(first.IsDetected);
        Assert.Same(first, detector.MatchWindow(path, "cache-fixture.exe", "Game", "Window", handle: 42));
        fixture.Metadata((321, null));
        await library.RefreshAsync(true);
        Assert.False(detector.MatchWindow(path, "cache-fixture.exe", "Game", "Window", handle: 42).IsDetected);
    }

    [Fact]
    public void CatalogSoftwareAppIdCannotBeOverriddenByCustomExecutableRule()
    {
        var detector = new ForegroundGameDetector(new SteamGameLibrary(() => null));
        detector.ApplyRemoteCatalog([new()
        {
            Id = "software-alias", DisplayName = "OBS", SteamAppIds = [1905180],
            Matchers = [new() { Executable = "renamed-obs.exe" }]
        }]);
        detector.ApplyCustomGameNames([new() { ExecutableName = "renamed-obs.exe", DisplayName = "Pretend game", Origin = "UserCustom" }]);
        Assert.False(detector.MatchWindow(@"C:\elsewhere\renamed-obs.exe", "renamed-obs.exe", "OBS", "Window").IsDetected);
    }

    internal static byte[] AppInfo(int version, params (int Id, string? Type)[] entries)
    {
        string[] strings = ["appinfo", "common", "type", "unrelated"];
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream, Encoding.UTF8, true);
        writer.Write(0x07564400u + (uint)version);
        writer.Write(1);
        if (version == 41) writer.Write(0L);
        foreach (var (id, type) in entries)
        {
            using var data = new MemoryStream();
            using var record = new BinaryWriter(data, Encoding.UTF8, true);
            void Key(byte kind, string key)
            {
                record.Write(kind);
                if (version == 41) record.Write(Array.IndexOf(strings, key));
                else { record.Write(Encoding.UTF8.GetBytes(key)); record.Write((byte)0); }
            }
            Key(0, "appinfo");
            Key(1, "type"); // must not classify an unrelated root-level type
            record.Write(Encoding.UTF8.GetBytes("Tool\0"));
            Key(0, "common");
            if (type is not null) { Key(1, "type"); record.Write(Encoding.UTF8.GetBytes(type + "\0")); }
            record.Write((byte)8);
            record.Write((byte)8);
            record.Write((byte)8);
            var bytes = data.ToArray();
            writer.Write(id);
            writer.Write(bytes.Length + (version >= 40 ? 60 : 40));
            writer.Write(new byte[40]);
            if (version >= 40) writer.Write(SHA1.HashData(bytes));
            writer.Write(bytes);
        }
        writer.Write(0);
        if (version == 41)
        {
            var offset = stream.Position;
            writer.Write(strings.Length);
            foreach (var value in strings) { writer.Write(Encoding.UTF8.GetBytes(value)); writer.Write((byte)0); }
            stream.Position = 8;
            writer.Write(offset);
        }
        return stream.ToArray();
    }

    private sealed class LibraryFixture : IDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "ClypDat-SteamTests-" + Guid.NewGuid());
        public string MetadataPath => Path.Combine(Root, "appcache", "appinfo.vdf");
        public LibraryFixture() { Directory.CreateDirectory(Path.Combine(Root, "appcache")); }
        public SteamGameLibrary Library(Action? beforeIndexBuild = null) => new(() => Root, beforeIndexBuild);
        public void Metadata(params (int Id, string? Type)[] entries) => File.WriteAllBytes(MetadataPath, AppInfo(41, entries));
        public string Install(int id, string exe, string? folder = null)
        {
            folder ??= id.ToString();
            var root = Path.Combine(Root, "steamapps", "common", folder);
            Directory.CreateDirectory(root);
            File.WriteAllText(Path.Combine(Root, "steamapps", $"appmanifest_{id}.acf"), $"\"appid\" \"{id}\"\n\"name\" \"Fixture {id}\"\n\"installdir\" \"{folder}\"");
            var path = Path.Combine(root, exe);
            File.WriteAllBytes(path, []);
            return path;
        }
        public void Dispose() => Directory.Delete(Root, true);
    }
}
