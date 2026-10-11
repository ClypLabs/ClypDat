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

    [Fact]
    public void PointerPositionIsClampedToTheField()
    {
        Assert.Equal(new Point(0, 0), ColorField.At(new Point(0, 0), Field));
        Assert.Equal(new Point(0.5, 0.25), ColorField.At(new Point(116, 58), Field));
        // Captured and dragged past the top and right: held on the edge.
        Assert.Equal(new Point(1, 0), ColorField.At(new Point(400, -25), Field));
    }

    [Fact]
    public void HueRunsFromTopToBottomWithoutWrapping()
    {
        Assert.True(HueStrip.At(0, 232) is > 359 and < 360);
        Assert.Equal(180, HueStrip.At(116, 232), 6);
        Assert.Equal(0, HueStrip.At(232, 232));
        Assert.Equal(0, HueStrip.At(500, 232));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void EveryBaseInTheFieldIsPaintedAsShown(bool light)
    {
        // The recording that prompted this: the top of the old field showed
        // #4D3A52 and the theme painted #2E1E33. Every point must now paint as
        // the colour the field gives for it.
        var model = AppThemeService.CustomBaseField(light);
        var wrong = new List<string>();
        for (var hue = 0; hue < 360; hue += 5)
        for (var x = 0.0; x <= 1.0001; x += 0.05)
        for (var y = 0.0; y <= 1.0001; y += 0.05)
        {
            var color = model.At(hue, x, y);
            var painted = AppThemeService.CustomGround(color, light);
            if (painted != color) wrong.Add($"h{hue} x{x:0.00} y{y:0.00}: {color} painted {painted}");
        }
        Assert.True(wrong.Count == 0, string.Join("\n", wrong.Take(20)));
    }

    [Theory]
    [InlineData("#0D1116", false)]
    [InlineData("#2E1E33", false)]
    [InlineData("#050505", false)]
    [InlineData("#F4F1EC", true)]
    [InlineData("#9E9EA0", true)]
    public void EveryAccentInTheFieldIsPaintedAsShown(string groundHex, bool light)
    {
        var ground = AppThemeService.CustomGround(Color.Parse(groundHex), light);
        var model = AppThemeService.CustomAccentField(ground);
        var wrong = new List<string>();
        for (var hue = 0; hue < 360; hue += 5)
        for (var x = 0.0; x <= 1.0001; x += 0.05)
        for (var y = 0.0; y <= 1.0001; y += 0.05)
        {
            var color = model.At(hue, x, y);
            var painted = AppThemeService.CustomAccent(color, ground);
            if (painted != color) wrong.Add($"h{hue} x{x:0.00} y{y:0.00}: {color} painted {painted}");
        }
        Assert.True(wrong.Count == 0, string.Join("\n", wrong.Take(20)));
    }

    [Theory]
    [InlineData("#2E1E33", "#3030C0", false)]
    [InlineData("#0D1116", "#5A1A8C", false)]
    [InlineData("#F4F1EC", "#E8E060", true)]
    public void AnAccentThatDoesNotReadIsFittedToTheFieldUnderTheThumb(string baseHex, string accentHex, bool light)
    {
        // What the editor does when the ground leaves an accent outside the
        // field: the accent becomes the field's colour where the painter would
        // have taken it, so the swatch is the pixel under the thumb and the
        // theme paints it unchanged.
        var ground = AppThemeService.CustomGround(Color.Parse(baseHex), light);
        var pick = Color.Parse(accentHex);
        var painted = AppThemeService.CustomAccent(pick, ground);
        Assert.NotEqual(pick, painted);

        var picker = new ThemeColorPickerViewModel(accentHex, _ => { }) { Model = AppThemeService.CustomAccentField(ground) };
        var fitted = picker.LoadAt(picker.Model!.Locate(painted));

        Assert.Equal(picker.Model.At(picker.Hue, picker.Position.X, picker.Position.Y), fitted);
        Assert.Equal(fitted, AppThemeService.CustomAccent(fitted, ground));
        Assert.Equal(fitted, picker.ShownColor);
    }

    [Fact]
    public void ShadesStartAtTheBaseAndStepAwayFromIt()
    {
        var ground = AppThemeService.CustomGround(Color.Parse("#580B50"), light: false);
        var surfaces = AppThemeService.CustomSurfaces(ground, light: false);
        Assert.Equal(4, surfaces.Count);
        for (var i = 1; i < surfaces.Count; i++)
            Assert.True(Luminance(surfaces[i]) > Luminance(surfaces[i - 1]));
        Assert.True(Math.Abs(surfaces[0].R - ground.R) <= 1 && Math.Abs(surfaces[0].G - ground.G) <= 1 && Math.Abs(surfaces[0].B - ground.B) <= 1);
    }

    [Theory]
    [InlineData(false, 285)]
    [InlineData(true, 40)]
    public void DrawnPixelsAreThePickedColours(bool light, double hue)
    {
        // The field is drawn by the same function a pick runs, so the pixel
        // under the thumb is the colour the swatch shows.
        var model = AppThemeService.CustomBaseField(light);
        const int width = 37, height = 41;
        var pixels = new int[width * height];
        model.Fill(hue, width, height, pixels);
        for (var row = 0; row < height; row++)
        for (var column = 0; column < width; column++)
        {
            var color = model.At(hue, (column + 0.5) / width, (row + 0.5) / height);
            var expected = unchecked((int)(0xFF000000u | ((uint)color.R << 16) | ((uint)color.G << 8) | color.B));
            Assert.Equal(expected, pixels[row * width + column]);
        }
    }

    [Fact]
    public void FieldBrightensUpAndSaturatesAcross()
    {
        var model = AppThemeService.CustomBaseField(light: false);
        var previous = -1.0;
        for (var y = 1.0; y >= -0.0001; y -= 0.01)
        {
            var lightness = ColorFieldModel.Lightness(Luminance(model.At(285, 0.5, y)));
            Assert.True(lightness >= previous);
            previous = lightness;
        }
        Assert.Equal(model.At(285, 0, 0.5).R, model.At(285, 0, 0.5).G);
        Assert.NotEqual(model.At(285, 1, 0.5).R, model.At(285, 1, 0.5).G);
    }

    [Fact]
    public void PickingFromTheFieldCommitsTheColourUnderTheThumb()
    {
        var committed = new List<string>();
        var picker = new ThemeColorPickerViewModel("#0D1116", committed.Add) { Model = AppThemeService.CustomBaseField(light: false) };
        picker.Hue = 285;
        picker.Position = new Point(0.4, 0);

        var expected = picker.Model!.At(285, 0.4, 0);
        Assert.Equal(expected, picker.ShownColor);
        Assert.Equal($"#{expected.R:X2}{expected.G:X2}{expected.B:X2}", committed[^1]);
        // Picking does not move the thumb or the hue off where they were put.
        Assert.Equal(new Point(0.4, 0), picker.Position);
        Assert.Equal(285, picker.Hue);
    }

    [Fact]
    public void ChangingHueRepicksAtTheSamePoint()
    {
        var picker = new ThemeColorPickerViewModel("#0D1116", _ => { }) { Model = AppThemeService.CustomBaseField(light: false) };
        picker.Position = new Point(0.8, 0.2);
        picker.Hue = 120;
        Assert.Equal(picker.Model!.At(120, 0.8, 0.2), picker.ShownColor);
        Assert.Equal(new Point(0.8, 0.2), picker.Position);
    }

    [Fact]
    public void TypedGreyKeepsTheHue()
    {
        var picker = new ThemeColorPickerViewModel("#0D1116", _ => { }) { Model = AppThemeService.CustomBaseField(light: false) };
        picker.Hue = 200;
        picker.HexText = "#101010";
        Assert.Equal(200, picker.Hue);
        Assert.Equal(0, picker.Position.X, 6);
    }

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
