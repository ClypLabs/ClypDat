using System.Runtime.ExceptionServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using ClypDat.App.Services;
using ClypDat.App.ViewModels;
using ClypDat.App.Views;
using Xunit;

namespace ClypDat.App.Tests;

public sealed class ClipOverlayCardRendererTests
{
    [Fact]
    [Trait("Category", "IsolatedSTA")]
    public void RasterUsesCurrentThemeFontDpiAndMeasuredWrapping()
    {
        if (!OperatingSystem.IsWindows()) return;
        AvaloniaTestThread.Run(() =>
        {
            var application = Application.Current!;
            application.Resources["AccentBrush"] = new SolidColorBrush(Colors.Blue);
            application.Resources["ClypDatFontFamily"] = new FontFamily("fonts:Inter#Inter, $Default");
            AssertOnboardingLayout();
            var now = DateTime.UtcNow;
            var presentation = new ClipOverlayPresentation(1, new ClipOverlayEvent(Guid.NewGuid(), 0, now, now,
                80, ClipOverlayKind.Saved, "Clip Saved", null,
                new ClipOverlayTarget("DISPLAY1", new PixelRect(0, 0, 1920, 1080), new PixelRect(0, 0, 1920, 1040),
                    1, ClipOverlayTargetReason.Primary), ClipOverlayPlacement.TopRight, true));

            foreach (var theme in new[] { "Emerald", "Berry", "Light" })
            {
                AppThemeService.Apply(application, theme, Colors.Blue, false);
                foreach (var placement in Enum.GetValues<ClipOverlayPlacement>())
                {
                    foreach (var scaling in new[] { 1d, 1.5d })
                    {
                        var frame = ClipOverlayCardRenderer.Render(presentation with
                        {
                            Event = presentation.Event with
                            {
                                Placement = placement,
                                Target = presentation.Event.Target with { Scaling = scaling }
                            }
                        });
                        Assert.Equal((int)Math.Ceiling(220 * scaling), frame.Width);
                        Assert.Equal((int)Math.Ceiling(58 * scaling), frame.Height);
                        AssertAccentAndSilhouette(application, frame, placement, scaling);
                        AssertTitleFits(application, frame, scaling);
                        AssertPremultiplied(frame);
                    }
                }
            }

            var original = ClipOverlayCardRenderer.Render(presentation);
            application.Resources["ClypDatFontFamily"] = new FontFamily("Courier New");
            var changedFont = ClipOverlayCardRenderer.Render(presentation);
            Assert.False(original.Pixels.AsSpan().SequenceEqual(changedFont.Pixels));
            application.Resources["ClypDatFontFamily"] = new FontFamily("fonts:Inter#Inter, $Default");

            var wrapped = ClipOverlayCardRenderer.Render(presentation with
            {
                Event = presentation.Event with { Detail = new string('W', 80) }
            });
            Assert.True(wrapped.Height > original.Height);
            var scaled = ClipOverlayCardRenderer.Render(presentation with
            {
                Event = presentation.Event with { Target = presentation.Event.Target with { Scaling = 1.5 } }
            });
            Assert.Equal(330, scaled.Width);
            Assert.Equal(87, scaled.Height);
            var failed = ClipOverlayCardRenderer.Render(presentation with
            {
                Event = presentation.Event with { Kind = ClipOverlayKind.Failure }
            });
            Assert.Equal(BrushColor(application, "DangerBrush"), Pixel(failed, 2, 20));

            AppThemeService.Apply(application, "Emerald", Colors.Blue, false);
            var recording = presentation.Event with
            {
                Kind = ClipOverlayKind.GameStarted, Title = "Recording: Doom", Detail = null
            };
            var bare = ClipOverlayCardRenderer.Render(new ClipOverlayPresentation(2, recording));
            var chipped = ClipOverlayCardRenderer.Render(new ClipOverlayPresentation(3,
                recording with { Hotkey = "Ctrl+Shift+F9", HotkeyHint = "to save a clip" }));
            Assert.True(chipped.Height > bare.Height, "The keycap row has to add a second line.");
            Assert.False(bare.Pixels.AsSpan().SequenceEqual(chipped.Pixels));

            // The regression this design restores: a long game name widens
            // the card instead of wrapping onto a second title line.
            var shortTitle = ClipOverlayCardRenderer.Render(new ClipOverlayPresentation(4,
                recording with { Hotkey = "Insert", HotkeyHint = "to save a clip" }));
            var longTitle = ClipOverlayCardRenderer.Render(new ClipOverlayPresentation(5,
                recording with { Title = "Recording: HELLDIVERS™ 2", Hotkey = "Insert", HotkeyHint = "to save a clip" }));
            Assert.True(longTitle.Width > shortTitle.Width, "The card has to size itself to the title.");
            Assert.Equal(shortTitle.Height, longTitle.Height);

            var richNotification = recording with
            {
                Title = "Recording: HELLDIVERS™ 2",
                Hotkey = "Ctrl+Shift+F9",
                HotkeyHint = "to save a clip"
            };
            foreach (var placement in Enum.GetValues<ClipOverlayPlacement>())
            foreach (var scaling in new[] { 1d, 1.5d })
            {
                var frame = ClipOverlayCardRenderer.Render(new ClipOverlayPresentation(6, richNotification with
                {
                    Placement = placement,
                    Target = richNotification.Target with { Scaling = scaling }
                }));
                Assert.True(frame.Width > 220 * scaling, "Long titles must remain on one line.");
                Assert.True(frame.Height > 58 * scaling, "Hotkey chips must add a row.");
                AssertAccentAndSilhouette(application, frame, placement, scaling);
            }
            SpotifyFrameChecks.Run();
        }, TimeSpan.FromSeconds(60), "Offscreen rasterization timed out.");
    }

    private static uint BrushColor(Application application, string key)
    {
        Assert.True(application.Resources.TryGetResource(key, application.ActualThemeVariant, out var value));
        return Assert.IsType<SolidColorBrush>(value).Color.ToUInt32();
    }

    // The card body must be opaque. Only anti-aliased rounded edges may blend
    // with gameplay; an interior sample must be the exact theme colour.
    private static void AssertFill(uint token, uint pixel)
        => Assert.Equal(token, pixel);

    private static uint Pixel(ClipOverlayFrame frame, int x, int y)
        => BitConverter.ToUInt32(frame.Pixels, (y * frame.Width + x) * 4);

    private static void AssertAccentAndSilhouette(Application application, ClipOverlayFrame frame,
        ClipOverlayPlacement placement, double scaling)
    {
        var left = placement is ClipOverlayPlacement.TopLeft or ClipOverlayPlacement.CenterLeft or ClipOverlayPlacement.BottomLeft;
        var railX = left ? frame.Width - 1 - PixelAt(2, scaling) : PixelAt(2, scaling);
        var fillX = left ? frame.Width - 1 - PixelAt(10, scaling) : PixelAt(10, scaling);
        var y = PixelAt(20, scaling);
        Assert.Equal(BrushColor(application, "AccentBrush"), Pixel(frame, railX, y));
        AssertFill(BrushColor(application, "SurfaceBrush"), Pixel(frame, fillX, y));
        Assert.Equal(0u, Pixel(frame, left ? frame.Width - 1 : 0, 0));
    }

    private static int PixelAt(double dip, double scaling) => (int)Math.Round(dip * scaling);

    // A clipped 150%-DPI canvas used to lose its logical transform, causing
    // TextLayout's next per-run translation to apply DPI twice. "Clip Saved"
    // then extended well beyond its 165-DIP title column.
    private static void AssertTitleFits(Application application, ClipOverlayFrame frame, double scaling)
    {
        var title = BrushColor(application, "TextStrongBrush");
        var start = PixelAt(165, scaling);
        var bottom = Math.Min(frame.Height, PixelAt(45, scaling));
        for (var y = 0; y < bottom; y++)
        for (var x = start; x < frame.Width; x++)
            Assert.NotEqual(title, Pixel(frame, x, y));
    }

    private static void AssertPremultiplied(ClipOverlayFrame frame)
    {
        for (var i = 0; i < frame.Pixels.Length; i += 4)
        {
            var alpha = frame.Pixels[i + 3];
            Assert.True(frame.Pixels[i] <= alpha && frame.Pixels[i + 1] <= alpha && frame.Pixels[i + 2] <= alpha);
        }
    }

    private static void AssertOnboardingLayout()
    {
        Dispatcher.UIThread.VerifyAccess();
        var viewModel = new MainWindowViewModel();
        viewModel.IsOnboardingVisible = true;
        Assert.Equal(6, viewModel.ReplayDurationPresets.Count);

        foreach (var scaling in new[] { 1d, 1.5d, 2d })
        {
            // Exercise real physical viewport sizes, then convert them back
            // through the active render scale to layout DIPs.
            foreach (var logicalAvailable in new[] { new Size(1032, 669), new Size(1312, 852) })
            {
                var physicalAvailable = new PixelSize(
                    (int)Math.Round(logicalAvailable.Width * scaling),
                    (int)Math.Round(logicalAvailable.Height * scaling));
                var available = new Size(physicalAvailable.Width / scaling, physicalAvailable.Height / scaling);
                var window = new MainWindow { DataContext = viewModel };
                window.Measure(available);
                window.Arrange(new Rect(available));
                Dispatcher.UIThread.RunJobs();

                var dialog = window.FindControl<Border>("OnboardingDialog");
                var body = window.FindControl<Grid>("OnboardingBody");
                var footer = window.FindControl<StackPanel>("OnboardingFooter");
                var pills = window.FindControl<ListBox>("OnboardingReplayDurationPills");
                var qualityPresets = window.FindControl<ListBox>("OnboardingQualityPresets");
                Assert.NotNull(dialog);
                Assert.NotNull(body);
                Assert.NotNull(footer);
                Assert.NotNull(pills);
                Assert.NotNull(qualityPresets);
                Assert.Equal(640, dialog.Width);
                Assert.Equal(500, dialog.Height);
                Assert.True(dialog.Bounds.Width <= available.Width, $"Dialog overflows width at {scaling:0}% scale.");
                Assert.True(dialog.Bounds.Height <= available.Height, $"Dialog overflows height at {scaling:0}% scale.");

                var footerTop = PositionIn(dialog, footer).Y;
                var stationaryFooter = footer.Bounds;
                foreach (var (step, cardName) in new[]
                {
                    ("Capture", "OnboardingCaptureCard"),
                    ("Quality", "OnboardingQualityCard"),
                    ("Audio", "OnboardingAudioCard"),
                    ("Startup", "OnboardingStartupCard")
                })
                {
                    viewModel.OnboardingStep = step;
                    window.InvalidateMeasure();
                    window.Measure(available);
                    window.Arrange(new Rect(available));
                    Dispatcher.UIThread.RunJobs();

                    var card = window.FindControl<Border>(cardName);
                    Assert.NotNull(card);
                    var cardTop = PositionIn(dialog, card).Y;
                    var cardBottom = cardTop + card.Bounds.Height;
                    Assert.True(cardTop >= PositionIn(dialog, body).Y, $"{step} card leaves body at {scaling:0}% scale.");
                    Assert.True(cardBottom <= footerTop - 12, $"{step} card reaches footer at {scaling:0}% scale.");
                    Assert.Equal(stationaryFooter, footer.Bounds);
                }

                viewModel.OnboardingStep = "Quality";
                var qualityCard = window.FindControl<Border>("OnboardingQualityCard");
                Assert.NotNull(qualityCard);
                // A detached Window has no top-level layout manager to
                // propagate IsVisible. Make the quality page visible before
                // measuring its real controls and application styles.
                var qualityPage = Assert.IsType<StackPanel>(qualityCard.Parent);
                qualityPage.IsVisible = true;
                qualityPage.Measure(body.Bounds.Size);
                qualityPage.Arrange(new Rect(body.Bounds.Size));
                window.InvalidateMeasure();
                window.Measure(available);
                window.Arrange(new Rect(available));
                Dispatcher.UIThread.RunJobs();

                Assert.True(qualityCard.Bounds.Width > 0 && qualityCard.Bounds.Height > 0);
                Assert.True(qualityPresets.Bounds.Width > 0 && qualityPresets.Bounds.Height > 0);
                Assert.True(BoundsIn(dialog, body).Contains(BoundsIn(dialog, qualityCard)), $"Quality card leaves body at {scaling:0}% scale.");

                var presetCards = qualityPresets.GetVisualDescendants().OfType<ListBoxItem>().ToArray();
                Assert.Equal(4, presetCards.Length);
                Assert.All(presetCards, presetCard =>
                {
                    Assert.True(presetCard.Bounds.Width > 0 && presetCard.Bounds.Height > 0);
                    Assert.True(BoundsIn(dialog, qualityCard).Contains(BoundsIn(dialog, presetCard)), $"Quality preset leaves its card at {scaling:0}% scale.");
                    Assert.All(presetCard.GetVisualDescendants().OfType<TextBlock>(), textBlock =>
                        Assert.True(BoundsIn(presetCard, textBlock).Bottom <= presetCard.Bounds.Height, $"Preset text clips at {scaling:0}% scale."));
                });
                Assert.All(presetCards.Skip(1), presetCard => Assert.Equal(presetCards[0].Bounds.Size, presetCard.Bounds.Size));

                foreach (var preset in viewModel.ReplayQualityPresets)
                {
                    viewModel.SelectedReplayQualityPreset = preset;
                    window.InvalidateMeasure();
                    window.Measure(available);
                    window.Arrange(new Rect(available));
                    Dispatcher.UIThread.RunJobs();
                    Assert.Same(preset, qualityPresets.SelectedItem);
                }

                var qualityInputs = qualityCard.GetVisualDescendants().OfType<ComboBox>().ToArray();
                Assert.Equal(2, qualityInputs.Length);
                Assert.All(qualityInputs, input =>
                {
                    Assert.True(input.Bounds.Width > 0 && input.Bounds.Height > 0);
                    Assert.True(BoundsIn(dialog, qualityCard).Contains(BoundsIn(dialog, input)), $"Quality input leaves its card at {scaling:0}% scale.");
                });
                Assert.InRange(Math.Abs(qualityInputs[0].Bounds.Width - qualityInputs[1].Bounds.Width), 0, 1);

                viewModel.OnboardingStep = "Capture";
                window.InvalidateMeasure();
                window.Measure(available);
                window.Arrange(new Rect(available));
                Dispatcher.UIThread.RunJobs();
                var bodyBounds = BoundsIn(dialog, body);
                Assert.Equal(6, pills.ItemCount);
                Assert.True(bodyBounds.Contains(BoundsIn(dialog, pills)), $"Replay options leave body at {scaling:0}% scale.");

                foreach (var action in dialog.GetVisualDescendants().OfType<Button>()
                    .Where(button => button.Classes.Contains("onboardingAction")))
                {
                    Assert.Equal(HorizontalAlignment.Center, action.HorizontalContentAlignment);
                    Assert.Equal(VerticalAlignment.Center, action.VerticalContentAlignment);
                }
            }
        }
    }

    private static Point PositionIn(Visual ancestor, Visual control)
        => control.TranslatePoint(default, ancestor) ?? throw new Xunit.Sdk.XunitException("Control is detached from walkthrough dialog.");

    private static Rect BoundsIn(Visual ancestor, Visual control)
    {
        var position = PositionIn(ancestor, control);
        return new Rect(position, control.Bounds.Size);
    }
}
