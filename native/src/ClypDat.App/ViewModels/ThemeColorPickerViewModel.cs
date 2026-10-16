using Avalonia;
using Avalonia.Media;
using ClypDat.App.Controls;
using ClypDat.Core.Settings;

namespace ClypDat.App.ViewModels;

/// <summary>
/// One colour, and the four ways the theme editor lets you state it: the
/// field, the hue strip, a hex box and three RGB boxes. They all drive each
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
    private double _hue;
    private Point _position;
    private ColorFieldModel? _model = ColorFieldModel.Hsv;
    private string _hexText;
    private string _redText;
    private string _greenText;
    private string _blueText;
    private string _error = string.Empty;
    private bool _updating;
    private Color _shownColor;
    private Color _currentColor;
    private ThemeColor _currentPick;
    private double _currentHue;

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
        _hue = _color.ToHsv().H;
        _hexText = seed.Hex;
        _redText = seed.Red.ToString();
        _greenText = seed.Green.ToString();
        _blueText = seed.Blue.ToString();
        _shownColor = _color;
        _position = ColorFieldModel.Hsv.Locate(_color);
        MarkCurrent();
    }

    /// <summary>
    /// What the field shows. Replacing it moves the thumb onto the colour,
    /// rather than the colour under a thumb that stays.
    /// </summary>
    public ColorFieldModel? Model
    {
        get => _model;
        set
        {
            if (!SetProperty(ref _model, value) || value is null) return;
            _updating = true;
            SetProperty(ref _position, value.Locate(_color), nameof(Position));
            _updating = false;
        }
    }

    // The hue is kept rather than read back from the colour. A grey or
    // near-black has no hue of its own - every shade of black converts to hue
    // 0 - so dragging into the bottom of the field would snap the strip to red.
    public double Hue
    {
        get => _hue;
        set { if (_updating || !SetProperty(ref _hue, value)) return; PickFromField(); }
    }

    // Where the thumb is, as fractions of the field. The colour is the model's
    // colour there and nothing else, so the swatch is the pixel under the thumb.
    public Point Position
    {
        get => _position;
        set { if (_updating || !SetProperty(ref _position, value)) return; PickFromField(); }
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

    // Says so when the theme paints the colour picked as a different one - a
    // ground kept dark or light enough for text, an accent lifted until it
    // stands out - which otherwise just changes under the user with no reason
    // given.
    private string _note = string.Empty;
    public string Note { get => _note; set => SetProperty(ref _note, value); }

    // The "new" and "current" swatches. New is the colour the hex box names,
    // which is the pixel under the thumb.
    // Current is what the editor opened on, and clicking it goes back there.
    public Color ShownColor { get => _shownColor; private set => SetProperty(ref _shownColor, value); }
    public Color CurrentColor { get => _currentColor; private set => SetProperty(ref _currentColor, value); }

    /// <summary>
    /// Makes the colour on show the "current" one. Called once the editor has
    /// finished loading a theme; not from <see cref="Load"/>, which also runs
    /// when Light mode repaints the base and must not move the swatch.
    /// </summary>
    public void MarkCurrent()
    {
        _currentPick = new ThemeColor(_color.R, _color.G, _color.B);
        _currentHue = _hue;
        CurrentColor = _shownColor;
    }

    /// <summary>Goes back to the colour the editor opened on.</summary>
    public void RestoreCurrent() => Set(_currentPick, _currentHue);

    /// <summary>Seeds the controls without reporting a change.</summary>
    /// <param name="hue">
    /// Kept instead of the hue the colour converts to. A grey or near-black has
    /// no hue of its own, and the strip would otherwise jump to red.
    /// </param>
    public void Load(string hex, double? hue = null)
    {
        if (!ThemeColor.TryParseHex(hex, out var color)) return;
        Write(color, locate: true, hue);
    }

    /// <summary>A colour from outside the field - typed, recent, screen: the thumb moves onto it.</summary>
    public void Set(ThemeColor color, double? hue = null)
    {
        Write(color, locate: true, hue);
        _committed(color.Hex);
    }

    private void PickFromField()
    {
        if (_model is null) return;
        var picked = _model.At(_hue, _position.X, _position.Y);
        var color = new ThemeColor(picked.R, picked.G, picked.B);
        Write(color, locate: false);
        _committed(color.Hex);
    }

    private void SetChannel(ref string field, string value)
    {
        if (!SetProperty(ref field, value) || _updating) return;
        if (!int.TryParse(RedText, out var red) || !int.TryParse(GreenText, out var green) || !int.TryParse(BlueText, out var blue) ||
            !ThemeColor.TryFromRgb(red, green, blue, out var color)) { Error = "RGB values must be 0–255."; return; }
        Set(color);
    }

    private void Write(ThemeColor color, bool locate, double? hue = null)
    {
        var value = Color.FromRgb(color.Red, color.Green, color.Blue);
        _updating = true;
        _color = value;
        if (locate)
        {
            var hsv = value.ToHsv();
            SetProperty(ref _hue, hue ?? (hsv.S > 0 && hsv.V > 0 ? hsv.H : _hue), nameof(Hue));
            if (_model is not null) SetProperty(ref _position, _model.Locate(value), nameof(Position));
        }
        SetProperty(ref _hexText, color.Hex, nameof(HexText));
        SetProperty(ref _redText, color.Red.ToString(), nameof(RedText));
        SetProperty(ref _greenText, color.Green.ToString(), nameof(GreenText));
        SetProperty(ref _blueText, color.Blue.ToString(), nameof(BlueText));
        ShownColor = value;
        _updating = false;
        Error = string.Empty;
    }
}
