using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.VisualTree;
using ClypDat.App.Services;
using ClypDat.App.Views;
using Xunit;

namespace ClypDat.App.Tests;

public sealed class HelpSupportViewTests
{
    [Theory]
    [InlineData(1560, 4, 2)]
    [InlineData(960, 2, 2)]
    [InlineData(640, 2, 1)]
    [InlineData(480, 1, 1)]
    public void CardsHaveVisibleSurfaces_AndWrapWithoutClipping(double width, int supportColumns, int wideColumns)
    {
        AvaloniaTestThread.Run(() =>
        {
            var view = new HelpSupportView();
            var window = new Window
            {
                Content = view, Width = width, Height = 700, ShowActivated = false,
                Position = new PixelPoint(-16000, -16000),
                FontFamily = new FontFamily("Arial")
            };
            try
            {
                window.Show();
                window.UpdateLayout();

                var cards = view.GetVisualDescendants().OfType<Border>()
                    .Where(border => border.Classes.Contains("helpCard")).ToArray();
                Assert.Equal(8, cards.Length);
                foreach (var card in cards)
                {
                    var surface = Assert.IsAssignableFrom<ISolidColorBrush>(card.Background);
                    Assert.Equal(255, surface.Color.A);
                    Assert.NotEqual(AppThemeService.Brush("PanelBgBrush", "#101820").ToString(), surface.ToString());
                    Assert.Equal(new Thickness(1), card.BorderThickness);

                    var button = card.GetVisualDescendants().OfType<Button>().Single(b => ReferenceEquals(b.Tag, card.DataContext));
                    Assert.True(button.Focusable);
                    Assert.True(KeyboardNavigation.GetIsTabStop(button));
                    var position = button.TranslatePoint(default, card)!.Value;
                    Assert.InRange(position.X, 16, card.Bounds.Width - button.Bounds.Width - 15);
                    // Layout rounds to physical pixels at the host's DPI scale.
                    Assert.InRange(card.Bounds.Height - position.Y - button.Bounds.Height, 16, 18);

                    foreach (var text in card.GetVisualDescendants().OfType<TextBlock>())
                    {
                        Assert.Equal(window.FontFamily, text.FontFamily);
                        var point = text.TranslatePoint(default, card)!.Value;
                        Assert.True(point.X + text.Bounds.Width <= card.Bounds.Width - 15);
                    }
                }

                AssertColumns(view.FindControl<ItemsControl>("NewsCards")!, wideColumns);
                AssertColumns(view.FindControl<ItemsControl>("SupportCards")!, supportColumns);
                AssertColumns(view.FindControl<ItemsControl>("SelfHelpCards")!, wideColumns);
            }
            finally { window.Close(); }
        }, TimeSpan.FromSeconds(30), "Help layout did not finish.");
    }

    [Fact]
    public void VisibleCardsFollowThemeChanges_AndBusyActionsKeepTheirPlace()
    {
        AvaloniaTestThread.Run(() =>
        {
            var view = new HelpSupportView();
            var window = new Window
            {
                Content = view, Width = 1280, Height = 800, ShowActivated = false,
                Position = new PixelPoint(-16000, -16000)
            };
            try
            {
                window.Show();
                window.UpdateLayout();
                var card = view.GetVisualDescendants().OfType<Border>().First(b => b.Classes.Contains("helpCard"));
                foreach (var theme in new[] { "Emerald", "Light" })
                {
                    AppThemeService.Apply(Application.Current!, theme, Colors.Blue, useSystemAccent: false);
                    window.UpdateLayout();
                    Assert.Equal(AppThemeService.Brush("SurfaceBrush", "#141D24").ToString(), card.Background!.ToString());
                    Assert.NotEqual(AppThemeService.Brush("PanelBgBrush", "#101820").ToString(), card.Background.ToString());
                }

                var button = card.GetVisualDescendants().OfType<Button>().Single(b => ReferenceEquals(b.Tag, card.DataContext));
                var cardBounds = card.Bounds;
                view.News[0].IsBusy = true;
                window.UpdateLayout();
                Assert.False(button.IsEnabled);
                Assert.True(button.GetVisualDescendants().OfType<ProgressBar>().Single().IsVisible);
                Assert.Equal(cardBounds, card.Bounds);
                view.News[0].IsBusy = false;
                window.UpdateLayout();
                Assert.True(button.IsEnabled);
            }
            finally
            {
                window.Close();
                AppThemeService.Apply(Application.Current!, "System", Colors.Blue, useSystemAccent: false);
            }
        }, TimeSpan.FromSeconds(30), "Help theme and busy-state checks did not finish.");
    }

    private static void AssertColumns(ItemsControl items, int expected)
    {
        var cards = items.GetVisualDescendants().OfType<Border>()
            .Where(border => border.Classes.Contains("helpCard")).ToArray();
        var positions = cards.Select(card => card.TranslatePoint(default, items)!.Value).ToArray();
        Assert.Equal(expected, positions.Count(point => Math.Abs(point.Y - positions[0].Y) < 1));
        if (expected > 1)
            Assert.InRange(positions[1].X - positions[0].X - cards[0].Bounds.Width, 11, 13);
    }
}
