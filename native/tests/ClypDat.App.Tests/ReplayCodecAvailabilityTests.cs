using System.Collections.ObjectModel;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Avalonia.Controls;
using ClypDat.App.Services;
using ClypDat.App.ViewModels;
using ClypDat.Core.Settings;
using Xunit;

namespace ClypDat.App.Tests;

public sealed class ReplayCodecAvailabilityTests
{
    private static void Field(object target, string name, object value) => target.GetType()
        .GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(target, value);

    // Exercise real setters and probe-result delivery without starting capture,
    // network, accounts, audio enumeration or GPU detection from the constructor.
    private static MainWindowViewModel Model(AppSettings settings)
    {
        var model = (MainWindowViewModel)RuntimeHelpers.GetUninitializedObject(typeof(MainWindowViewModel));
        Field(model, "<Settings>k__BackingField", settings);
        Field(model, "<ReplayVideoCodecs>k__BackingField", new ObservableCollection<ReplayVideoCodecOption>
        {
            new("H.264", "H.264", "Default"), new("AV1", "AV1", "Hardware AV1", isEnabled: false)
        });
        Field(model, "<ReplayEncoderModes>k__BackingField", new ObservableCollection<MainWindowViewModel.ReplayEncoderModeOption>
        {
            new("GPU", "Hardware"), new("CPU", "Software")
        });
        Field(model, "<CustomGameTabs>k__BackingField", new ObservableCollection<CustomGameTabViewModel>());
        foreach (var profile in settings.CustomGameSettings.Values)
        {
            var game = (CustomGameTabViewModel)RuntimeHelpers.GetUninitializedObject(typeof(CustomGameTabViewModel));
            Field(game, "<Profile>k__BackingField", profile);
            Field(game, "_settings", settings);
            Field(game, "_save", (Action)model.SaveSettings);
            Field(game, "_isAv1Available", (Func<bool>)(() => model.IsReplayAv1Available));
            model.CustomGameTabs.Add(game);
        }
        return model;
    }

    private static AppSettings Settings(string codec = "AV1") => new()
    {
        ReplayVideoCodec = codec,
        CustomGameSettings = new()
        {
            ["fixture.exe"] = new() { ReplayVideoCodec = codec, Groups = [CustomGameSettingsResolver.QualityGroup] }
        }
    };

    private static void Isolated(Action action) => AvaloniaTestThread.Run(() =>
    {
        var previous = AppDataPaths.ProductFolderName;
        AppDataPaths.ConfigureProductFolder("ClypDat-Av1OptionsTest-" + Guid.NewGuid().ToString("N"));
        var root = AppDataPaths.Root;
        try { action(); }
        finally
        {
            AppDataPaths.ConfigureProductFolder(previous);
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }, TimeSpan.FromSeconds(30), "AV1 option availability test timed out.");

    [Theory]
    [InlineData(null, false)]
    [InlineData("qsv", false)]
    [InlineData("unknown", false)]
    [InlineData("nvenc", true)]
    [InlineData("amf", true)]
    public void ProbeResultUpdatesOptionsAndPersistedGlobalAndGameSelections(string? family, bool available) => Isolated(() =>
    {
        var settings = Settings();
        var model = Model(settings);
        var game = model.CustomGameTabs.Single();
        model.SaveSettings();
        Assert.False(model.ReplayVideoCodecs[1].IsEnabled);
        Assert.Contains("Checking", model.ReplayVideoCodecStatus);
        game.RefreshReplayCodecAvailability(probeCompleted: false);
        Assert.Equal("AV1", game.ReplayVideoCodec);
        Assert.Equal("AV1", model.SelectedReplayVideoCodec.Value);

        model.ApplyReplayAv1ProbeResult(family);
        var expected = available ? "AV1" : "H.264";
        Assert.Equal(available, model.IsReplayAv1Available);
        Assert.Equal(available, model.ReplayVideoCodecs[1].IsEnabled);
        Assert.True(model.ReplayVideoCodecs[0].IsEnabled);
        Assert.Equal(expected, model.SelectedReplayVideoCodec.Value);
        Assert.Equal(expected, game.ReplayVideoCodec);
        Assert.DoesNotContain("Checking", model.ReplayVideoCodecStatus);
        var saved = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(AppSettingsStore.SettingsPath))!;
        Assert.Equal(expected, saved.ReplayVideoCodec);
        Assert.Equal(expected, saved.CustomGameSettings["fixture.exe"].ReplayVideoCodec);
    });

    [Fact]
    public void PendingAndUnsupportedSelectionsAreRejectedThenSupportedProbeEnablesBothPickers() => Isolated(() =>
    {
        var model = Model(Settings("H.264"));
        var game = model.CustomGameTabs.Single();
        void AttemptAv1()
        {
            model.SelectedReplayVideoCodec = model.ReplayVideoCodecs[1];
            game.ReplayVideoCodec = "AV1";
        }
        AttemptAv1();
        Assert.Equal("H.264", model.Settings.ReplayVideoCodec);
        Assert.Equal("H.264", game.ReplayVideoCodec);
        model.ApplyReplayAv1ProbeResult("qsv");
        AttemptAv1();
        Assert.Equal("H.264", model.Settings.ReplayVideoCodec);
        Assert.Equal("H.264", game.ReplayVideoCodec);
        model.ApplyReplayAv1ProbeResult("nvenc");
        AttemptAv1();
        Assert.Equal("AV1", model.Settings.ReplayVideoCodec);
        Assert.Equal("AV1", game.ReplayVideoCodec);
    });

    [Fact]
    public void CpuModeForcesH264IndependentlyForGlobalAndPerGameSettings() => Isolated(() =>
    {
        var model = Model(Settings());
        var game = model.CustomGameTabs.Single();
        model.ApplyReplayAv1ProbeResult("amf");
        game.ReplayEncoderMode = "CPU";
        game.ReplayVideoCodec = "AV1";
        Assert.True(game.IsReplayEncoderCpu);
        Assert.Equal("H.264", game.ReplayVideoCodec);
        Assert.Equal("AV1", model.Settings.ReplayVideoCodec);
        game.ReplayEncoderMode = "GPU";
        game.ReplayVideoCodec = "AV1";
        Assert.Equal("AV1", game.ReplayVideoCodec);
        model.SelectedReplayEncoderMode = model.ReplayEncoderModes.Single(mode => mode.Value == "CPU");
        model.SelectedReplayVideoCodec = model.ReplayVideoCodecs[1];
        Assert.Equal("H.264", model.Settings.ReplayVideoCodec);
        Assert.Equal("AV1", game.ReplayVideoCodec);
        Assert.Contains("CPU", model.ReplayVideoCodecStatus);
    });

    [Fact]
    public void DisabledRowsAreGreyAndClosedDropdownNavigationSkipsAv1() => Isolated(() =>
    {
        var model = Model(Settings("H.264"));
        var picker = new CodecPicker { ItemsSource = model.ReplayVideoCodecs, SelectedIndex = 0 };
        picker.Classes.Add("settingsInput");
        picker.Classes.Add("recordingCodecPicker");
        // An unshown window provides the application's real resource/style root.
        var host = new Window { Content = picker };
        try
        {
            var row = picker.AddRow(model.ReplayVideoCodecs[1]);
            Assert.False(row.IsEnabled);
            Assert.Equal(0.5, row.Opacity);

            // Call the control's navigation routine directly. No keyboard events,
            // OS input, visible window or popup are used by this test.
            var move = typeof(ComboBox).GetMethod("MoveSelection", BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly)!;
            Assert.False((bool)move.Invoke(picker, [0, 1, false])!);
            Assert.Equal(0, picker.SelectedIndex);
            model.ApplyReplayAv1ProbeResult("amf");
            Assert.True(row.IsEnabled);
            Assert.Equal(1, row.Opacity);
            Assert.True((bool)move.Invoke(picker, [0, 1, false])!);
            Assert.Equal(1, picker.SelectedIndex);
            model.ApplyReplayAv1ProbeResult(null);
            Assert.False(row.IsEnabled);
            Assert.Equal(0.5, row.Opacity);
        }
        finally { host.Close(); }
    });

    private sealed class CodecPicker : ComboBox
    {
        protected override Type StyleKeyOverride => typeof(ComboBox);
        public ComboBoxItem AddRow(ReplayVideoCodecOption option)
        {
            var row = new ComboBoxItem { DataContext = option };
            LogicalChildren.Add(row);
            ApplyStyling();
            row.ApplyStyling();
            return row;
        }
    }
}
