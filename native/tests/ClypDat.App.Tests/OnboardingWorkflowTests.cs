using System.Reflection;
using System.Runtime.CompilerServices;
using System.Xml.Linq;
using ClypDat.App.Services;
using ClypDat.App.ViewModels;
using ClypDat.Capture.Abstractions;
using ClypDat.Core.Settings;
using Xunit;

namespace ClypDat.App.Tests;

public sealed class OnboardingWorkflowTests
{
    private static void Field(object target, string name, object value) => target.GetType()
        .GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(target, value);

    private static void Isolated(Action<MainWindowViewModel> test) => AvaloniaTestThread.Run(() =>
    {
        var previous = AppDataPaths.ProductFolderName;
        AppDataPaths.ConfigureProductFolder("ClypDat-OnboardingTest-" + Guid.NewGuid().ToString("N"));
        var root = AppDataPaths.Root;
        try
        {
            var vm = (MainWindowViewModel)RuntimeHelpers.GetUninitializedObject(typeof(MainWindowViewModel));
            Field(vm, "<Settings>k__BackingField", new AppSettings { HasSeenOnboarding = false });
            Field(vm, "_activeGameDetection", GameDetection.None);
            Field(vm, "_selectedReplayCaptureSource", "Game Capture");
            Field(vm, "_recordingHealth", ReplayCaptureHealth.Unknown("Native"));
            Field(vm, "_recorderStatus", "Replay Off");
            Field(vm, "_clypDatSnapshot", XboxActivitySnapshot.Disconnected);
            test(vm);
        }
        finally
        {
            AppDataPaths.ConfigureProductFolder(previous);
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }, TimeSpan.FromSeconds(30), "Onboarding workflow test timed out.");

    [Fact]
    public void InterruptedSetupStaysIncompleteAndCannotFinishEarly() => Isolated(vm =>
    {
        vm.StartOnboarding();
        Assert.True(vm.IsOnboardingVisible);
        Assert.True(vm.IsFirstRunOnboarding);
        Assert.False(vm.OnboardingCompletion.IsCompleted);
        typeof(MainWindowViewModel).GetMethod("FinishOnboarding", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(vm, null);
        Assert.True(vm.IsOnboardingVisible);
        vm.OnboardingNext();
        Assert.Equal("CaptureMode", vm.OnboardingStep);
        Assert.False(vm.OnboardingCanContinue);
        vm.SelectedOnboardingCaptureMode = "Unknown";
        Assert.False(vm.OnboardingCanContinue);
        vm.OnboardingNext();
        Assert.Equal("CaptureMode", vm.OnboardingStep);
        vm.SaveSettings();
        Assert.False(AppSettingsStore.Load().HasSeenOnboarding);
        Assert.False(vm.OnboardingCompletion.IsCompleted);
    });

    [Fact]
    public void EveryStepMustBePassedBeforeCompletionPersists() => Isolated(vm =>
    {
        vm.StartOnboarding();
        vm.OnboardingNext();
        vm.SelectedOnboardingCaptureMode = "Desktop";
        vm.OnboardingBack();
        Assert.Equal("Capture", vm.OnboardingStep);
        Assert.False(vm.OnboardingBackEnabled);
        var visited = new List<string>();
        while (vm.IsOnboardingVisible)
        {
            Assert.True(visited.Count < vm.OnboardingStepCount, "Onboarding did not advance.");
            visited.Add(vm.OnboardingStep);
            Assert.False(vm.Settings.HasSeenOnboarding);
            Assert.False(vm.OnboardingCompletion.IsCompleted);
            vm.OnboardingNext();
        }
        Assert.Equal(["Capture", "CaptureMode", "Quality", "Audio", "Tracks", "Exclusions", "Startup"], visited);
        Assert.Equal(visited.Count, vm.OnboardingStepCount);
        Assert.True(vm.OnboardingCompletion.IsCompletedSuccessfully);
        Assert.True(AppSettingsStore.Load().HasSeenOnboarding);
        vm.OnboardingNext();
        Assert.False(vm.IsOnboardingVisible);
    });

    [Theory]
    [InlineData("Game", "Game", false)]
    [InlineData("Desktop", "Desktop", false)]
    [InlineData("Game (automatic)", "Desktop", true)]
    public void CaptureChoicePersistsAndAutomaticModeFollowsGameDetection(string choice, string source, bool automatic) => Isolated(vm =>
    {
        vm.StartOnboarding();
        vm.OnboardingNext();
        vm.SelectedOnboardingCaptureMode = choice;
        Assert.True(vm.OnboardingCanContinue);
        var persisted = AppSettingsStore.Load();
        Assert.Equal(source, persisted.ReplayCaptureSource);
        Assert.Equal(automatic, persisted.ReplayAutoSwitchToGameCapture);
        Assert.Equal(source, vm.CreateReplayConfig().CaptureSource);
        Field(vm, "_activeGameDetection", new GameDetection("Fixture", "fixture.exe", "", "", 0, 123, true));
        Assert.Equal(automatic, vm.IsAutomaticGameCapture);
        Assert.Equal(source == "Desktop" && !automatic ? "Desktop" : "Game", vm.CreateReplayConfig().CaptureSource);
        Field(vm, "_activeGameDetection", GameDetection.None);
        Assert.Equal(source, vm.CreateReplayConfig().CaptureSource);
    });

    [Fact]
    public void FullSystemAudioChoicePersistsWithoutCompletingSetup() => Isolated(vm =>
    {
        vm.StartOnboarding();
        vm.Settings.AdditionalAudioProcesses["Discord"] = 75;
        vm.SystemAudioEnabled = true;
        var saved = AppSettingsStore.Load();
        Assert.True(saved.SystemAudioEnabled);
        Assert.Equal(75, saved.AdditionalAudioProcesses["Discord"]);
        Assert.False(saved.HasSeenOnboarding);
        Assert.True(vm.CreateReplayConfig().SystemAudioEnabled);
    });

    [Fact]
    public void WizardHasNoDismissControlsAndRequiresCaptureSelection()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        string? path = null;
        for (; directory is not null; directory = directory.Parent)
        {
            var candidate = Path.Combine(directory.FullName, "native/src/ClypDat.App/Views/MainWindow.axaml");
            if (File.Exists(candidate)) { path = candidate; break; }
        }
        Assert.NotNull(path);
        var markup = XDocument.Load(path!);
        XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
        var overlay = markup.Descendants().Single(element => (string?)element.Attribute(x + "Name") == "OnboardingOverlay");
        Assert.Null(overlay.Attribute("PointerPressed"));
        Assert.DoesNotContain(overlay.Descendants(), element => (string?)element.Attribute("Content") == "Skip setup" || (string?)element.Attribute("Classes") == "dialogClose");
        var next = overlay.Descendants().Single(element => (string?)element.Attribute("Content") == "{Binding OnboardingNextLabel}");
        Assert.Equal("{Binding OnboardingCanContinue}", (string?)next.Attribute("IsEnabled"));
        Assert.Contains(overlay.Descendants(), element => (string?)element.Attribute("Text") == "Full System Audio");
    }
}
