using Avalonia;
using Avalonia.Media;
using ClypDat.App.Controls;
using ClypDat.App.Services;
using ClypDat.App.ViewModels;
using Xunit;

namespace ClypDat.App.Tests;

public sealed class ColorFieldTests
{
    private static readonly Size Field = new(232, 232);
    private static readonly ColorFieldModel Model = ColorFieldModel.Hsv;

    [Fact]
    public void PointerPositionIsClampedToTheField()
    {
        Assert.Equal(new Point(0, 0), ColorField.At(new Point(0, 0), Field));
        Assert.Equal(new Point(0.5, 0.25), ColorField.At(new Point(116, 58), Field));
        // Captured and dragged past the top and right: held on the edge.
        Assert.Equal(new Point(1, 0), ColorField.At(new Point(400, -25), Field));
    }

    [Fact]
    public void HueRunsLeftToRightBetweenTheRoundedEndsWithoutWrapping()
    {
        // A 264px bar 14px tall: the thumb centre travels from 7 to 257.
        Assert.Equal(0, HueStrip.At(0, 264, 7));
        Assert.Equal(0, HueStrip.At(7, 264, 7));
        Assert.Equal(180, HueStrip.At(132, 264, 7), 6);
        Assert.True(HueStrip.At(257, 264, 7) is > 359 and < 360);
        Assert.True(HueStrip.At(500, 264, 7) is > 359 and < 360);
    }

    // Discord's square: white at the top left, the pure hue at the top right,
    // black along the bottom. Nothing is held back or dimmed.
    [Theory]
    [InlineData(0)]
    [InlineData(120)]
    [InlineData(285)]
    public void FieldIsTheFullHsvSquare(double hue)
    {
        Assert.Equal(Colors.White, Model.At(hue, 0, 0));
        Assert.Equal(new HsvColor(1, hue, 1, 1).ToRgb(), Model.At(hue, 1, 0));
        Assert.Equal(Colors.Black, Model.At(hue, 0, 1));
        Assert.Equal(Colors.Black, Model.At(hue, 1, 1));
        // Value falls steadily down any column.
        var previous = double.MaxValue;
        for (var y = 0.0; y <= 1.0001; y += 0.05)
        {
            var luminance = Luminance(Model.At(hue, 0.7, y));
            Assert.True(luminance <= previous);
            previous = luminance;
        }
    }

    [Theory]
    [InlineData("#150F19")]
    [InlineData("#C02CF0")]
    [InlineData("#5864E8")]
    [InlineData("#FFFFFF")]
    public void LocatingAColourPutsTheThumbOnIt(string hex)
    {
        var color = Color.Parse(hex);
        var point = Model.Locate(color);
        var back = Model.At(color.ToHsv().H, point.X, point.Y);
        Assert.InRange(Math.Abs(back.R - color.R), 0, 1);
        Assert.InRange(Math.Abs(back.G - color.G), 0, 1);
        Assert.InRange(Math.Abs(back.B - color.B), 0, 1);
    }

    [Fact]
    public void ShadesStartAtTheBaseAndStepAwayFromIt()
    {
        var ground = AppThemeService.CustomGround(Color.Parse("#580B50"));
        var surfaces = AppThemeService.CustomSurfaces(ground, light: false);
        Assert.Equal(4, surfaces.Count);
        for (var i = 1; i < surfaces.Count; i++)
            Assert.True(Luminance(surfaces[i]) > Luminance(surfaces[i - 1]));
        Assert.True(Math.Abs(surfaces[0].R - ground.R) <= 1 && Math.Abs(surfaces[0].G - ground.G) <= 1 && Math.Abs(surfaces[0].B - ground.B) <= 1);
    }

    [Theory]
    [InlineData(285)]
    [InlineData(40)]
    public void DrawnPixelsAreThePickedColours(double hue)
    {
        // The field is drawn by the same function a pick runs, so the pixel
        // under the thumb is the colour the swatch shows.
        const int width = 37, height = 41;
        var pixels = new int[width * height];
        Model.Fill(hue, width, height, pixels);
        for (var row = 0; row < height; row++)
        for (var column = 0; column < width; column++)
        {
            var color = Model.At(hue, (column + 0.5) / width, (row + 0.5) / height);
            var expected = unchecked((int)(0xFF000000u | ((uint)color.R << 16) | ((uint)color.G << 8) | color.B));
            Assert.Equal(expected, pixels[row * width + column]);
        }
    }

    [Fact]
    public void PickingFromTheFieldCommitsTheColourUnderTheThumb()
    {
        var committed = new List<string>();
        var picker = new ThemeColorPickerViewModel("#0D1116", committed.Add);
        picker.Hue = 285;
        picker.Position = new Point(0.4, 0);

        var expected = Model.At(285, 0.4, 0);
        Assert.Equal(expected, picker.ShownColor);
        Assert.Equal($"#{expected.R:X2}{expected.G:X2}{expected.B:X2}", committed[^1]);
        // Picking does not move the thumb or the hue off where they were put.
        Assert.Equal(new Point(0.4, 0), picker.Position);
        Assert.Equal(285, picker.Hue);
    }

    [Fact]
    public void ChangingHueRepicksAtTheSamePoint()
    {
        var picker = new ThemeColorPickerViewModel("#0D1116", _ => { });
        picker.Position = new Point(0.8, 0.2);
        picker.Hue = 120;
        Assert.Equal(Model.At(120, 0.8, 0.2), picker.ShownColor);
        Assert.Equal(new Point(0.8, 0.2), picker.Position);
    }

    [Fact]
    public void TypedGreyKeepsTheHue()
    {
        var picker = new ThemeColorPickerViewModel("#0D1116", _ => { });
        picker.Hue = 200;
        picker.HexText = "#101010";
        Assert.Equal(200, picker.Hue);
        Assert.Equal(0, picker.Position.X, 6);
    }

    // The app is painted in the colour picked with its saturation softened:
    // same hue and lightness, muted picks untouched, vivid ones washed out.
    // It turns light where black text starts to read better than white - once,
    // part way between black and white, not at a switch.
    [Fact]
    public void TheGroundIsTheSoftenedPickAndLightFollowsIt()
    {
        foreach (var hex in new[] { "#0D1116", "#2E1E33", "#FFFFFF", "#000000", "#808080" })
            Assert.Equal(Color.Parse(hex), AppThemeService.CustomGround(Color.Parse(hex)));
        foreach (var hex in new[] { "#FF00FF", "#C02CF0", "#00D08A", "#5E1066" })
        {
            var pick = Color.Parse(hex);
            var softened = AppThemeService.CustomGround(pick);
            Assert.True(softened.ToHsl().S < pick.ToHsl().S - 0.1, hex);
            Assert.True(softened.ToHsl().S <= 0.42, hex);
            // Rounding back to 8-bit channels nudges the hue of a muted colour.
            Assert.InRange(Math.Abs(pick.ToHsl().H - softened.ToHsl().H), 0, 3);
            Assert.Equal(pick.ToHsl().L, softened.ToHsl().L, 1);
        }
        Assert.True(AppThemeService.CustomLight(AppThemeService.CustomGround(Color.Parse("#FDEFFF"))));
        Assert.True(AppThemeService.CustomLight(Colors.White));
        Assert.False(AppThemeService.CustomLight(Color.Parse("#0D1116")));
        Assert.False(AppThemeService.CustomLight(Colors.Black));

        // An accent that would vanish on the ground is lifted until it reads.
        var ground = Color.Parse("#0D1116");
        var accent = AppThemeService.CustomAccent(Color.Parse("#1A1030"), ground);
        Assert.True(AppThemeService.ContrastRatio(accent, ground) >= 2);
    }

    // Text on an accent stays white until the accent is pale enough to read
    // as pastel or white.
    [Theory]
    [InlineData("#00D08A", "#FFFFFF")]
    [InlineData("#5864E8", "#FFFFFF")]
    [InlineData("#D85E61", "#FFFFFF")]
    [InlineData("#FFFFFF", "#000000")]
    [InlineData("#FFF59D", "#000000")]
    [InlineData("#000000", "#FFFFFF")]
    public void AccentTextIsWhiteUnlessTheAccentIsPale(string accent, string text)
        => Assert.Equal(Color.Parse(text), AppThemeService.AccentForeground(Color.Parse(accent)));

    private static double Luminance(Color color)
    {
        static double Linear(byte channel)
        {
            var value = channel / 255d;
            return value <= .04045 ? value / 12.92 : Math.Pow((value + .055) / 1.055, 2.4);
        }
        return .2126 * Linear(color.R) + .7152 * Linear(color.G) + .0722 * Linear(color.B);
    }
}
