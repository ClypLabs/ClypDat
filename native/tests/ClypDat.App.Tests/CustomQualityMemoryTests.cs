using System.Collections.ObjectModel;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.Json;
using ClypDat.App.ViewModels;
using ClypDat.App.Services;
using ClypDat.Core.Settings;
using Xunit;

namespace ClypDat.App.Tests;

public sealed class CustomQualityMemoryTests
{
    private static void Field(object target, string name, object value) => target.GetType()
        .GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(target, value);

    // Skip constructor services (audio, accounts, network and capture); exercise
    // the real setters with isolated persisted settings on Avalonia's UI thread.
    private static MainWindowViewModel Global(AppSettings settings)
    {
        var vm = (MainWindowViewModel)RuntimeHelpers.GetUninitializedObject(typeof(MainWindowViewModel));
        Field(vm, "<Settings>k__BackingField", settings);
        Field(vm, "<ReplayQualityPresets>k__BackingField", new ObservableCollection<MainWindowViewModel.ReplayQualityPreset>(MainWindowViewModel.QualityPresets));
        Field(vm, "<ReplayResolutions>k__BackingField", new ObservableCollection<ResolutionOption>());
        Field(vm, "<ReplayBitrateOptions>k__BackingField", new ObservableCollection<string>());
        return vm;
    }

    [Fact]
    public void GlobalCustomValuesSurviveOtherPresetsAndReload()
    {
        AvaloniaTestThread.Run(() =>
        {
            var previous = AppDataPaths.ProductFolderName;
            AppDataPaths.ConfigureProductFolder("ClypDat-CustomQualityTest-" + Guid.NewGuid().ToString("N"));
            var root = AppDataPaths.Root;
            try
            {
                var settings = new AppSettings { ReplayQualityCustom = true, ReplayMaxHeight = 1440, ReplayFrameRate = 120, ReplayBitrateMbps = 35 };
                var vm = Global(settings);
                vm.SelectedReplayQualityPreset = MainWindowViewModel.QualityPresets[0];
                Assert.Equal(480, settings.ReplayMaxHeight);
                vm.SelectedReplayQualityPreset = MainWindowViewModel.QualityPresets[2];
                Assert.Equal(1080, settings.ReplayMaxHeight);
                var restored = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(Path.Combine(root, "settings.json")))!;
                vm = Global(restored);
                vm.SelectedReplayQualityPreset = MainWindowViewModel.QualityPresets[^1];
                Assert.Equal((1440, 120, 35), (restored.ReplayMaxHeight, vm.SelectedReplayFrameRate, restored.ReplayBitrateMbps));
                Assert.True(restored.ReplayQualityCustom);
                vm.SelectedReplayBitrateOption = "40M";
                vm.SelectedReplayQualityPreset = MainWindowViewModel.QualityPresets[1];
                vm.SelectedReplayQualityPreset = MainWindowViewModel.QualityPresets[^1];
                Assert.Equal(40, restored.ReplayBitrateMbps);
            }
            finally
            {
                AppDataPaths.ConfigureProductFolder(previous);
                if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
            }
        }, TimeSpan.FromSeconds(30), "Custom quality state did not finish.");
    }

    [Fact]
    public void GameCustomValuesHaveIndependentMemoryAcrossSerialization()
    {
        var profile = new CustomGameProfile { ReplayQualityCustom = true, ReplayMaxHeight = 2160, ReplayFrameRate = 120, ReplayBitrateMbps = 50 };
        CustomGameTabViewModel Game(CustomGameProfile value)
        {
            var vm = (CustomGameTabViewModel)RuntimeHelpers.GetUninitializedObject(typeof(CustomGameTabViewModel));
            Field(vm, "<Profile>k__BackingField", value);
            Field(vm, "_save", (Action)(() => { }));
            return vm;
        }
        var game = Game(profile);
        game.SelectedQualityPreset = MainWindowViewModel.QualityPresets[0];
        game.SelectedQualityPreset = MainWindowViewModel.QualityPresets[1];
        profile = JsonSerializer.Deserialize<CustomGameProfile>(JsonSerializer.Serialize(profile))!;
        game = Game(profile);
        game.SelectedQualityPreset = MainWindowViewModel.QualityPresets[^1];
        Assert.Equal((2160, 120, 50), (game.ReplayMaxHeight, game.ReplayFrameRate, game.ReplayBitrateMbps));
        game.SelectedBitrateOption = "45M";
        game.SelectedQualityPreset = MainWindowViewModel.QualityPresets[2];
        game.SelectedQualityPreset = MainWindowViewModel.QualityPresets[^1];
        Assert.Equal(45, game.ReplayBitrateMbps);
    }
}
