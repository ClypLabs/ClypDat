using System.Xml.Linq;
using ClypDat.App.Services;
using Xunit;

namespace ClypDat.App.Tests;

// build.ps1 publishes into %LOCALAPPDATA%\Programs\ClypDat with the real
// version (1.6.0 on this branch), so to the Stable updater it looked like an
// old release: at launch it installed the current release over the branch.
// A local build (ClypDatLocalBuild) now never offers or installs one, and
// nothing else about it changes. Releases behave exactly as before.
public sealed class LocalBuildModeTests
{
    private static string RepositoryFile(params string[] parts)
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            var candidate = Path.Combine([directory.FullName, .. parts]);
            if (File.Exists(candidate)) return candidate;
        }
        throw new FileNotFoundException(string.Join('/', parts));
    }

    [Theory]
    [InlineData("v1.6.1", "1.6.0", true)]
    [InlineData("v1.7.0", "1.6.0", true)]
    [InlineData("v1.6.0", "1.6.0", false)]
    [InlineData("v1.5.9", "1.6.0", false)]
    public void ReleaseBuild_SeesNewerStableReleases(string tag, string current, bool offered)
    {
        Assert.Equal(offered, AppUpdateService.IsNewerStableRelease(false, false, tag, Version.Parse(current), out _));
        // Drafts and prereleases never were.
        Assert.False(AppUpdateService.IsNewerStableRelease(true, false, tag, Version.Parse(current), out _));
        Assert.False(AppUpdateService.IsNewerStableRelease(false, true, tag, Version.Parse(current), out _));
    }

    [Fact]
    public void ReleaseBuild_InstallsAtLaunchAsBefore()
    {
        Assert.True(StartupUpdatePolicy.ShouldInstall(localBuild: false, installUpdatesOnLaunch: true, ignoredUpdateVersion: "", candidateTag: "v1.6.1"));
        Assert.True(StartupUpdatePolicy.ShouldInstall(false, true, null, "v1.6.1"));
        Assert.False(StartupUpdatePolicy.ShouldInstall(false, installUpdatesOnLaunch: false, "", "v1.6.1"));
    }

    [Fact]
    public void ReleaseBuild_IgnoredUpdateVersionUnchanged()
    {
        Assert.False(StartupUpdatePolicy.ShouldInstall(false, true, "v1.6.1", "v1.6.1"));
        Assert.False(StartupUpdatePolicy.ShouldInstall(false, true, "V1.6.1", "v1.6.1"));
        // It only ever skipped that one version.
        Assert.True(StartupUpdatePolicy.ShouldInstall(false, true, "v1.6.1", "v1.6.2"));
    }

    [Theory]
    [InlineData(true, "")]
    [InlineData(true, "v1.6.1")]
    [InlineData(false, "")]
    public void LocalBuild_NeverInstallsAtLaunch(bool installUpdatesOnLaunch, string ignored)
    {
        Assert.False(StartupUpdatePolicy.ShouldInstall(localBuild: true, installUpdatesOnLaunch, ignored, "v1.6.2"));
        Assert.False(StartupUpdatePolicy.ShouldInstall(localBuild: true, installUpdatesOnLaunch, ignored, "v99.0.0"));
    }

    [Fact]
    public async Task LocalBuild_NeverLearnsOfARelease()
    {
        // Answered without asking GitHub, GitLab or the mirror: the check
        // completes synchronously with nothing to offer.
        var check = AppUpdateService.CheckAsync(localBuild: true, CancellationToken.None);
        Assert.True(check.IsCompleted);
        Assert.Null(await check);
    }

    [Fact]
    public async Task LocalBuild_CannotRunTheStableInstaller()
    {
        // Even handed a release that verified, the install action refuses
        // before downloading anything.
        var release = new AppUpdateInfo(new Version(1, 6, 0), new Version(1, 6, 1), "v1.6.1",
            "https://github.com/ClypLabs/ClypDat/releases/download/v1.6.1/ClypDat-Setup.exe", [], [], new string('0', 64));
        var progress = new List<UpdateDownloadProgress>();
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            AppUpdateService.DownloadAndRestartAsync(release, new Progress<UpdateDownloadProgress>(progress.Add), localBuild: true, CancellationToken.None));
        Assert.Equal(LocalBuildMode.UpdatesSuppressedMessage, error.Message);
        Assert.Empty(progress);
    }

    [Fact]
    public void LocalBuild_KeepsTheNormalDataRootAndProduct()
    {
        // The local-build switch defines CLYPDAT_LOCAL_BUILD and nothing else:
        // no data root, product, name or version change like the Dev channel's.
        var project = XDocument.Load(RepositoryFile("native", "src", "ClypDat.App", "ClypDat.App.csproj"));
        var group = Assert.Single(project.Descendants("PropertyGroup"),
            element => (string?)element.Attribute("Condition") == "'$(ClypDatLocalBuild)' == 'true'");
        var property = Assert.Single(group.Elements());
        Assert.Equal("DefineConstants", property.Name.LocalName);
        Assert.Equal("$(DefineConstants);CLYPDAT_LOCAL_BUILD", property.Value);

        // The data root is the Stable one: the app moves it in one place, for
        // the Dev channel only, and the local-build switch has no say in it.
        Assert.False(DevChannelMode.Enabled);
        Assert.Equal("ClypDat", DevChannelMode.ProductFolder);
        var source = Path.GetDirectoryName(RepositoryFile("native", "src", "ClypDat.App", "ClypDat.App.csproj"))!;
        var moves = Directory.GetFiles(source, "*.cs", SearchOption.AllDirectories)
            .Where(file => !file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}") && File.ReadAllText(file).Contains("ConfigureProductFolder("))
            .Select(file => Path.GetFileName(file)).ToArray();
        Assert.Equal(["DevChannelMode.cs"], moves);
        Assert.DoesNotContain("AppDataPaths", File.ReadAllText(Path.Combine(source, "Services", "LocalBuildMode.cs")));
    }

    [Fact]
    public void BuildScript_MarksWhatItPublishes_ReleasesDoNot()
    {
        var script = File.ReadAllText(RepositoryFile("build.ps1"));
        Assert.Contains("-p:ClypDatLocalBuild=true", script);
        // Release packaging never sets it.
        foreach (var workflow in Directory.GetFiles(Path.GetDirectoryName(RepositoryFile(".github", "workflows", "release.yml"))!, "*.yml"))
            Assert.DoesNotContain("ClypDatLocalBuild", File.ReadAllText(workflow));
        // And this test build is not one.
        Assert.False(LocalBuildMode.Enabled);
    }
}
