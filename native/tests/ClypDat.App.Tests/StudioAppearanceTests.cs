using System.Reflection;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Avalonia.VisualTree;
using ClypDat.App.Controls;
using ClypDat.App.Services;
using ClypDat.App.ViewModels;
using ClypDat.App.Views;
using ClypDat.Core.Settings;
using Xunit;

namespace ClypDat.App.Tests;

public sealed class StudioAppearanceTests
{
    private static readonly string[] Presets =
    {
        "System", "ClypDat Blue", "Berry", "Emerald", "Rose", "Amber",
        "Light", "Light Blue", "Light Berry", "Light Emerald", "Light Rose", "Light Amber"
    };

    [Fact]
    public void PaletteSwitchingPreservesBrushesAndReadableLabels() => Run(() =>
    {
        var app = Application.Current!;
        var keys = new[] { "AppBgBrush", "PanelBgBrush", "SurfaceBrush", "SurfaceRaisedBrush", "TextBrush", "TextSubtleBrush", "AccentBrush" };
        var originals = keys.ToDictionary(key => key, key => Brush(key));
        foreach (var preset in Presets)
        {
            AppThemeService.Apply(app, preset, Colors.Gold, false);
            foreach (var key in keys) Assert.Same(originals[key], Brush(key));
            Assert.True(Contrast(Brush("TextBrush").Color, Brush("SurfaceBrush").Color) >= 4.5, preset);
            Assert.True(Contrast(Brush("TextSubtleBrush").Color, Brush("SurfaceBrush").Color) >= 4.5, preset);
        }
        foreach (var baseColor in new[] { "#104A37", "#503080", "#EEF3F9", "#151515" })
        {
            var custom = new CustomThemeSettings { BaseColor = baseColor, AccentColor = "#F5C518" };
            AppThemeService.Apply(app, "System", Colors.Blue, false, custom);
            Assert.Same(originals["AppBgBrush"], Brush("AppBgBrush"));
            Assert.True(Contrast(Brush("TextBrush").Color, Brush("SurfaceBrush").Color) >= 4.5, baseColor);
            Assert.True(Contrast(Brush("AccentForegroundBrush").Color, Brush("AccentBrush").Color) >= 4.5, baseColor);
        }
        AppThemeService.Apply(app, "Light", Colors.Blue, false);
        Assert.Equal(Color.Parse("#F5F6F8"), Brush("AppBgBrush").Color);
        Assert.Equal(Colors.White, Brush("PanelBgBrush").Color);
        Assert.Equal(Color.Parse("#EBEEF2"), Brush("SurfaceRaisedBrush").Color);
        AppThemeService.Apply(app, "System", Colors.Gold, true);
        Assert.Equal(Colors.Gold, Brush("AccentBrush").Color);
        Assert.Equal(Color.Parse("#101216"), Brush("AppBgBrush").Color);
        AppThemeService.Apply(app, "System", Colors.Blue, false);
    });

    [Fact]
    public void DialogsWrapAndRenderAtEveryScaleWithCustomFonts() => Run(() =>
    {
        var app = Application.Current!;
        var originalFont = app.Resources["ClypDatFontFamily"];
        try
        {
            foreach (var font in new[] { "Inter", "Courier New" })
            foreach (var theme in Presets)
            {
                app.Resources["ClypDatFontFamily"] = new FontFamily(font);
                AppThemeService.Apply(app, theme, Colors.Blue, false);
                var (window, body) = DialogComposition.Create("A long dialog title that must wrap instead of colliding with the close button");
                var description = new TextBlock { Classes = { "metadata" }, Text = string.Join(' ', Enumerable.Repeat("Long setting description", 12)) };
                var action = new Button { Content = "Save changes", Classes = { "primaryButton" } };
                body.Children.Add(description);
                body.Children.Add(new StackPanel { Classes = { "dialogActions" }, Children = { action } });
                Layout(window, new Size(440, 600));
                var shell = Assert.IsType<Border>(window.Content);
                Assert.Equal(new CornerRadius(16), shell.CornerRadius);
                Assert.True(description.Bounds.Height > 36);
                Assert.True(description.Bounds.Width <= 392.1, $"description={description.Bounds}; body={body.Bounds}; margin={body.Margin}; window={window.Bounds}");
                Assert.True(action.Bounds.Height >= 36);
                Assert.Equal(AppThemeService.FontFamily, description.FontFamily);
                foreach (var scale in new[] { 1d, 1.5d, 2d })
                {
                    using var bitmap = new RenderTargetBitmap(new PixelSize((int)(440 * scale), (int)(600 * scale)), new Vector(96 * scale, 96 * scale));
                    bitmap.Render(shell);
                    Assert.Equal((int)(440 * scale), bitmap.PixelSize.Width);
                }
                window.Close();
            }
        }
        finally
        {
            app.Resources["ClypDatFontFamily"] = originalFont;
            AppThemeService.Apply(app, "System", Colors.Blue, false);
        }
    });

    [Fact]
    public void LibrarySettingsAndEditorFitMinimumAndLargeWindows() => Run(() =>
    {
        Assert.False(AppSettingsStore.Load().ReplayBufferEnabled);
        Assert.StartsWith("ClypDat-UiTests-", AppDataPaths.ProductFolderName);
        var model = new MainWindowViewModel { IsOnboardingVisible = false };
        var window = new MainWindow { DataContext = model };
        foreach (var size in new[] { new Size(1032, 669), new Size(1600, 1000) })
        {
            window.Width = size.Width;
            window.Height = size.Height;
            SetView(model, "IsSettingsVisible", false);
            Layout(window, size);
            var search = window.FindControl<TextBox>("LibrarySearchBox")!;
            Assert.True(search.Bounds.Height >= 36);
            Assert.True(search.Bounds.Width >= 240);
            SetView(model, "IsSettingsVisible", true);
            foreach (var section in new[] { "General", "Replay Buffer", "Audio", "Appearance", "Video Overlays", "Game Detection", "Custom Game Settings", "Auto-Clip", "Game Audio Exclusions", "Import Clips", "Connected Accounts", "Overlays and Notifications", "Discord Rich Presence", "About" })
            {
                model.SelectedSettingsSection = section;
                Layout(window, size);
                var panel = window.FindControl<ClypDat.App.Views.Settings.SettingsPanel>("SettingsPanelView")!;
                Assert.InRange(panel.Bounds.Right, 1, size.Width);
                Assert.True(panel.Bounds.Height > 0, section);
                Assert.True(panel.FindControl<StackPanel>("SettingsContent")!.Bounds.Width <= 960);
                Assert.Equal(panel.Bounds.Width >= 1120, panel.FindControl<Panel>("SettingsStatusHost")!.IsVisible);
            }
            SetView(model, "IsSettingsVisible", false);
            SetView(model, "IsEditorVisible", true);
            Layout(window, size);
            var editor = window.FindControl<Grid>("EditorPanelRoot")!;
            Assert.True(editor.Bounds.Width > 600);
            Assert.True(window.FindControl<Grid>("EditorVideoHost")!.Bounds.Width > 300);
            SetView(model, "IsEditorVisible", false);
        }
        window.Close();
    });

    [Fact]
    public void ProgressDialogRetainsCancellationAction() => Run(() =>
    {
        var canceled = false;
        var method = typeof(MainWindow).GetMethod("CreateProgressDialog", BindingFlags.Static | BindingFlags.NonPublic)!;
        var result = ((Window Window, ProgressBar Bar, TextBlock Status, TextBlock Percent, TextBlock Eta))method.Invoke(null, new object[] { "Exporting clip", "Preparing export", (Action)(() => canceled = true) })!;
        Layout(result.Window, new Size(440, 400));
        result.Bar.Value = 50;
        Assert.Equal(50, result.Bar.Value);
        var cancel = result.Window.GetVisualDescendants().OfType<Button>().Single(button => Equals(button.Content, "Cancel"));
        cancel.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
        Assert.True(canceled);
        Assert.False(cancel.IsEnabled);
        Assert.Equal("Cancelling...", result.Status.Text);
        result.Window.Close();
    });

    [Fact]
    public void LibraryCardsKeepThumbnailsAndBusyLabelsInsideTheirBounds() => Run(() =>
    {
        var model = new MainWindowViewModel { IsOnboardingVisible = false };
        var window = new MainWindow { DataContext = model };
        var root = AppSettingsStore.Load().LibraryFolder;
        var clips = Enumerable.Range(0, 6).Select(index => new ClipCardViewModel(new MediaFileInfo(
            $"Fixture clip {index} with a long descriptive title", Path.Combine(root, $"fixture-{index}.mp4"),
            DateTimeOffset.UtcNow.AddMinutes(-index), TimeSpan.FromSeconds(45), 1024, string.Empty,
            Array.Empty<MediaTrackInfo>(), 1920, 1080, 60), root)).ToArray();
        clips[0].BusyOverlayText = "Preparing clip";
        clips[1].BusyOverlayText = "Clip unavailable";
        var items = window.FindControl<ItemsControl>("LibraryItemsControl")!;
        items.ItemsSource = new[] { new LibraryGridRow(clips.Take(3).ToArray(), 0), new LibraryGridRow(clips.Skip(3).ToArray(), 1) };
        SetView(model, "IsInitialLibraryLoadComplete", true);
        Layout(window, new Size(1032, 669));
        var cards = items.GetVisualDescendants().OfType<Border>().Where(border => border.Classes.Contains("libraryClipCardSurface")).ToArray();
        Assert.NotEmpty(cards);
        foreach (var card in cards)
        {
            Assert.True(card.Bounds.Width > 0);
            var thumbnail = Assert.Single(card.GetVisualDescendants().OfType<AspectRatioBox>());
            Assert.True(thumbnail.Bounds.Width <= card.Bounds.Width);
            Assert.InRange(thumbnail.Bounds.Width / thumbnail.Bounds.Height, 1.76, 1.79);
            Assert.Equal(new CornerRadius(12), card.CornerRadius);
        }
        SetView(model, "IsInitialLibraryLoadComplete", false);
        Layout(window, new Size(1032, 669));
        Assert.True(window.FindControl<Control>("LibraryLoadingTilesOverlay")!.IsVisible);
        window.Close();
    });

    private static void SetView(MainWindowViewModel model, string property, bool value) =>
        typeof(MainWindowViewModel).GetProperty(property)!.SetValue(model, value);

    [Fact]
    public void SharingOverlayAndUpdateWindowsUseSharedChrome() => Run(() =>
    {
        var main = new MainWindow();
        var notes = new[] { "A long release note describing updated controls, wrapping text, keyboard focus, and clip library behaviour across every supported theme." };
        var update = new AppUpdateInfo(new Version(1, 0), new Version(1, 1), "v1.1", "https://example.invalid/fixture", notes, notes);
        var createUpdate = typeof(MainWindow).GetMethod("CreateUpdateDialog", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var windows = new Window[]
        {
            new ShareDialog(), new SpotifyOverlayDialog(), new ClypDat.App.Views.Settings.VideoOverlayDialog(),
            (Window)createUpdate.Invoke(main, new object[] { update })!
        };
        foreach (var window in windows)
        {
            Layout(window, new Size(window.Width, 700));
            var shell = Assert.IsType<Border>(window.Content);
            Assert.Equal(new CornerRadius(16), shell.CornerRadius);
            Assert.NotEmpty(window.GetVisualDescendants().OfType<Button>());
            foreach (var scale in new[] { 1d, 1.5d, 2d })
            {
                using var bitmap = new RenderTargetBitmap(new PixelSize((int)(window.Width * scale), (int)(700 * scale)), new Vector(96 * scale, 96 * scale));
                bitmap.Render(shell);
            }
            window.Close();
        }
        main.Close();
    });

    [Theory]
    [InlineData("Rename clip", "Rename", false)]
    [InlineData("Delete clips?", "Delete", true)]
    [InlineData("Something went wrong", "OK", false)]
    public void ModalActionsKeepLabelsAndLayout(string title, string action, bool destructive) => Run(() =>
    {
        var factory = typeof(MainWindow).GetMethod("CreateDialog", BindingFlags.Static | BindingFlags.NonPublic)!;
        var window = (Window)factory.Invoke(null, new object?[] { title, "A detailed message that wraps across multiple lines without obscuring the actions below it.", true, action, destructive, null })!;
        Layout(window, new Size(440, 400));
        var buttons = window.GetVisualDescendants().OfType<Button>().ToArray();
        Assert.Contains(buttons, button => Equals(button.Content, "Cancel"));
        var confirm = Assert.Single(buttons, button => Equals(button.Content, action));
        Assert.True(confirm.Bounds.Width >= 104);
        Assert.True(confirm.Bounds.Height >= 36);
        window.Close();
    });

    private static void Layout(Window window, Size size)
    {
        Dispatcher.UIThread.RunJobs();
        window.InvalidateMeasure();
        window.Measure(size);
        window.Arrange(new Rect(size));
        // An unshown native window keeps its platform client size. Constrain
        // the actual view explicitly, without opening or moving a window.
        if (window.Content is Control content)
        {
            content.InvalidateMeasure();
            content.Measure(size);
            content.Arrange(new Rect(size));
        }
    }

    private static SolidColorBrush Brush(string key)
    {
        Assert.True(Application.Current!.TryFindResource(key, out var value), key);
        return Assert.IsType<SolidColorBrush>(value);
    }

    private static double Contrast(Color a, Color b)
    {
        static double L(Color c)
        {
            static double Linear(byte channel) { var v = channel / 255d; return v <= .04045 ? v / 12.92 : Math.Pow((v + .055) / 1.055, 2.4); }
            return .2126 * Linear(c.R) + .7152 * Linear(c.G) + .0722 * Linear(c.B);
        }
        var x = L(a); var y = L(b);
        return (Math.Max(x, y) + .05) / (Math.Min(x, y) + .05);
    }

    private static void Run(Action action) => AvaloniaTestThread.Run(action, TimeSpan.FromMinutes(2), "Studio appearance fixture timed out.");
}
