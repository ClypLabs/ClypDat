using Avalonia.Media;
using ClypDat.Core.Settings;

namespace ClypDat.App.ViewModels;

/// <summary>
/// One colour, and the four ways the theme editor lets you state it: the
/// spectrum, the hue slider, a hex box and three RGB boxes. They all drive each
/// other, so they live together behind one re-entrancy guard.
///
/// There used to be a single instance of this state shared between Base and
/// Accent, with a segmented toggle deciding which one it currently stood for and
/// a separate "Use colour" button copying it across. That copy was the bug: the
/// spectrum previewed the theme live but only "Use colour" wrote the value Apply
/// went on to save, so dragging to a colour and pressing Apply saved the
/// previous one. A picker per colour that writes straight through has nowhere
/// for the two to disagree.
/// </summary>
public sealed class ThemeColorPickerViewModel : ViewModelBase
{
    private readonly Action<string> _committed;
    private Color _color;
    private HsvColor _hsvColor;
    private string _hexText;
    private string _redText;
    private string _greenText;
    private string _blueText;
    private string _error = string.Empty;
    private bool _updating;

    /// <param name="committed">
    /// Called with the new #RRGGBB whenever the colour changes by a user action.
    /// Not called by <see cref="Load"/>, which exists to seed the controls when
    /// the editor opens on a theme that is already saved.
    /// </param>
    public ThemeColorPickerViewModel(string hex, Action<string> committed)
    {
        _committed = committed;
        var seed = ThemeColor.TryParseHex(hex, out var parsed) ? parsed : new ThemeColor(13, 17, 22);
        _color = Color.FromRgb(seed.Red, seed.Green, seed.Blue);
        _hsvColor = _color.ToHsv();
        _hexText = seed.Hex;
        _redText = seed.Red.ToString();
        _greenText = seed.Green.ToString();
        _blueText = seed.Blue.ToString();
    }

    public Color Color { get => _color; set { if (_updating) return; Set(new ThemeColor(value.R, value.G, value.B)); } }

    // The spectrum and hue slider bind here rather than to Color, and the value
    // they send is kept verbatim instead of being recomputed from the RGB it
    // converts to. Hue does not survive that round trip at the edges - every
    // shade of pure black converts to RGB 0,0,0 and back to hue 0 - so dragging
    // into the bottom of the spectrum would throw the hue away and snap the
    // slider to red.
    public HsvColor HsvColor
    {
        get => _hsvColor;
        set
        {
            if (_updating) return;
            var rgb = value.ToRgb();
            Set(new ThemeColor(rgb.R, rgb.G, rgb.B), value);
        }
    }

    public string HexText
    {
        get => _hexText;
        set
        {
            if (!SetProperty(ref _hexText, value) || _updating) return;
            if (!ThemeColor.TryParseHex(value, out var color)) { Error = "Use #RRGGBB."; return; }
            Set(color);
        }
    }

    public string RedText { get => _redText; set => SetChannel(ref _redText, value); }
    public string GreenText { get => _greenText; set => SetChannel(ref _greenText, value); }
    public string BlueText { get => _blueText; set => SetChannel(ref _blueText, value); }
    public string Error { get => _error; private set => SetProperty(ref _error, value); }

    /// <summary>Seeds the controls without reporting a change.</summary>
    public void Load(string hex)
    {
        if (ThemeColor.TryParseHex(hex, out var color)) Write(color);
    }

    public void Set(ThemeColor color, HsvColor? hsv = null)
    {
        Write(color, hsv);
        _committed(color.Hex);
    }

    private void SetChannel(ref string field, string value)
    {
        if (!SetProperty(ref field, value) || _updating) return;
        if (!int.TryParse(RedText, out var red) || !int.TryParse(GreenText, out var green) || !int.TryParse(BlueText, out var blue) ||
            !ThemeColor.TryFromRgb(red, green, blue, out var color)) { Error = "RGB values must be 0–255."; return; }
        Set(color);
    }

    private void Write(ThemeColor color, HsvColor? hsv = null)
    {
        var value = Color.FromRgb(color.Red, color.Green, color.Blue);
        _updating = true;
        SetProperty(ref _color, value, nameof(Color));
        SetProperty(ref _hsvColor, hsv ?? value.ToHsv(), nameof(HsvColor));
        SetProperty(ref _hexText, color.Hex, nameof(HexText));
        SetProperty(ref _redText, color.Red.ToString(), nameof(RedText));
        SetProperty(ref _greenText, color.Green.ToString(), nameof(GreenText));
        SetProperty(ref _blueText, color.Blue.ToString(), nameof(BlueText));
        _updating = false;
        Error = string.Empty;
    }
}
