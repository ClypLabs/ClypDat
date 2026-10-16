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

    // A vivid pick paints a deep ground in its hue, never a neon or mid-tone
    // one; pale picks up to white paint a light ground; and between them the
    // ground changes in small steps, never a jump.
    [Fact]
    public void GroundIsDarkForVividPicksLightForPaleOnesAndChangesSmoothly()
    {
        Assert.Equal(Color.Parse("#0D1116"), AppThemeService.CustomGround(Color.Parse("#0D1116")));
        foreach (var hex in new[] { "#F315F0", "#FF00FF", "#00D08A", "#C02CF0", "#808080" })
        {
            var painted = AppThemeService.CustomGround(Color.Parse(hex));
            Assert.False(AppThemeService.CustomLight(painted), hex);
            Assert.True(Luminance(painted) <= 0.042, hex);
        }
        foreach (var hex in new[] { "#FFFFFF", "#FDEFFF", "#F0F0F0" })
        {
            var painted = AppThemeService.CustomGround(Color.Parse(hex));
            Assert.True(AppThemeService.CustomLight(painted), hex);
            Assert.True(Luminance(painted) >= 0.61, hex);
        }
        // Down the left half of the field from a pure hue to white.
        var previous = AppThemeService.CustomGround(new HsvColor(1, 300, 1, 1).ToRgb());
        for (var step = 1; step <= 100; step++)
        {
            var pick = new HsvColor(1, 300, 1 - step / 100.0, 1).ToRgb();
            var painted = AppThemeService.CustomGround(pick);
            Assert.True(Math.Abs(Luminance(painted) - Luminance(previous)) < 0.1, $"{pick} -> {painted}");
            previous = painted;
        }
        Assert.True(AppThemeService.CustomLight(Colors.White));
        Assert.False(AppThemeService.CustomLight(Colors.Black));

        // Across the field's top edge, which used to land on one ground: more
        // saturated picks paint a more saturated page.
        var lastSaturation = -1.0;
        for (var x = 0.0; x <= 1.0001; x += 0.05)
        {
            var painted = AppThemeService.CustomGround(new HsvColor(1, 300, x, 1).ToRgb());
            if (AppThemeService.CustomLight(painted)) continue;
            var saturationNow = painted.ToHsl().S;
            Assert.True(saturationNow > lastSaturation + 0.01, $"x {x:0.00}: {painted}");
            lastSaturation = saturationNow;
        }

        // An accent that would vanish on the ground is lifted until it reads.
        var ground = Color.Parse("#0D1116");
        var accent = AppThemeService.CustomAccent(Color.Parse("#1A1030"), ground);
        Assert.True(AppThemeService.ContrastRatio(accent, ground) >= 2);
    }

    // Text on an accent stays white, however bright the accent, until it is
    // close to white: a pastel or an off-white.
    [Theory]
    [InlineData("#00D08A", "#FFFFFF")]
    [InlineData("#00F5A0", "#FFFFFF")]
    [InlineData("#FFFF00", "#FFFFFF")]
    [InlineData("#FFC0CB", "#000000")]
    [InlineData("#F0F0F0", "#000000")]
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
