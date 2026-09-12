using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using ClypDat.App.Services;
using ClypDat.App.ViewModels;
using ClypDat.Core.Settings;

namespace ClypDat.App.Controls;

public sealed class TimedEffectEditor : StackPanel
{
    public bool Blur { get; set; }
    private MainWindowViewModel? _model;
    private readonly ComboBox _list = new() { HorizontalAlignment = HorizontalAlignment.Stretch };
    private readonly StackPanel _fields = new() { Spacing = 6 };
    private readonly TextBlock _error = new() { Foreground = Brushes.OrangeRed, TextWrapping = TextWrapping.Wrap };
    private bool _refreshing;
    public TimedEffectEditor()
    {
        Spacing = 8;
        DataContextChanged += (_, _) => Connect();
        DetachedFromVisualTree += (_, _) => { if (_model is not null) _model.TimedEffectsChanged -= Refresh; };
        AttachedToVisualTree += (_, _) => Connect();
    }
    private void Connect()
    {
        if (_model is not null) _model.TimedEffectsChanged -= Refresh;
        _model = DataContext as MainWindowViewModel;
        Children.Clear();
        Children.Add(new TextBlock { Text = Blur ? "BLUR" : "TEXT", FontWeight = FontWeight.Bold });
        var add = new Button { Content = Blur ? "Add Blur" : "Add Text" };
        add.Click += (_, _) => Run(() => { if (_model is null) return; _model.IsPlaying = false; _model.AddTimedEffect(Blur); });
        Children.Add(add); Children.Add(_list); Children.Add(_fields); Children.Add(_error);
        if (_model is not null) _model.TimedEffectsChanged += Refresh;
        _list.SelectionChanged -= Select;
        _list.SelectionChanged += Select;
        Refresh(this, EventArgs.Empty);
    }
    private void Run(Action action)
    {
        try { action(); _error.Text = ""; }
        catch (Exception e) { _error.Text = e.Message; }
    }
    private void Select(object? sender, SelectionChangedEventArgs args)
    {
        if (_refreshing || _model is null) return;
        var list = Blur ? _model.BlurEffects : _model.TextEffects;
        if (_list.SelectedIndex >= 0 && _list.SelectedIndex < list.Count) _model.SelectedTimedEffectId = list[_list.SelectedIndex].Id;
        BuildFields();
    }
    private TimedVideoEffect? Selected => (_model is null ? null : (Blur ? _model.BlurEffects : _model.TextEffects).FirstOrDefault(e => e.Id == _model.SelectedTimedEffectId));
    private void Refresh(object? sender, EventArgs args)
    {
        if (_model is null) return;
        _refreshing = true;
        var list = Blur ? _model.BlurEffects : _model.TextEffects;
        _list.ItemsSource = list.Select((e, i) => $"{i + 1}. {(Blur ? "Blur" : e.Text.Replace('\n', ' '))} ({e.Start:0.##}–{e.End:0.##}s)").ToArray();
        _list.SelectedIndex = list.ToList().FindIndex(e => e.Id == _model.SelectedTimedEffectId);
        _refreshing = false;
        BuildFields();
    }
    private void Change(Func<TimedVideoEffect, TimedVideoEffect> update) => Run(() => { if (Selected is { } e) _model!.UpdateTimedEffect(update(e), Blur); });
    private void BuildFields()
    {
        _fields.Children.Clear();
        if (Selected is not { } e) return;
        var visible = new CheckBox { Content = "Visible", IsChecked = e.Visible };
        visible.IsCheckedChanged += (_, _) => Change(x => x with { Visible = visible.IsChecked == true });
        _fields.Children.Add(visible);
        Number("Start (seconds)", e.Start, 0, 86400, v => Change(x => x with { Start = v }));
        Button("Start at playhead", () => Change(x => x with { Start = _model!.CurrentTime.TotalSeconds }));
        Number("End (seconds)", e.End, 0, 86400, v => Change(x => x with { End = v }));
        Button("End at playhead", () => Change(x => x with { End = _model!.CurrentTime.TotalSeconds }));
        Number("X (%)", e.X * 100, 0, 99, v => Change(x => x with { X = v / 100 }));
        Number("Y (%)", e.Y * 100, 0, 99, v => Change(x => x with { Y = v / 100 }));
        Number("Width (%)", e.Width * 100, 1, 100, v => Change(x => x with { Width = v / 100 }));
        Number("Height (%)", e.Height * 100, 1, 100, v => Change(x => x with { Height = v / 100 }));
        if (Blur)
        {
            _fields.Children.Add(new TextBlock { Text = "Strength" });
            var strength = new Slider { Minimum = 1, Maximum = 100, Value = e.Strength };
            strength.PointerReleased += (_, _) => Change(x => x with { Strength = strength.Value });
            strength.LostFocus += (_, _) => { if (strength.Value != e.Strength) Change(x => x with { Strength = strength.Value }); };
            _fields.Children.Add(strength);
        }
        else
        {
            Text("Caption", e.Text, v => Change(x => x with { Text = v }), true);
            var fonts = new ComboBox { ItemsSource = FontManager.Current.SystemFonts.Select(f => f.Name).Order().ToArray(), SelectedItem = e.Font };
            fonts.SelectionChanged += (_, _) => { if (fonts.SelectedItem is string font) Change(x => x with { Font = font }); };
            _fields.Children.Add(fonts);
            Number("Font size (1080p)", e.FontSize, 8, 300, v => Change(x => x with { FontSize = v }));
            var bold = new CheckBox { Content = "Bold", IsChecked = e.Bold };
            bold.IsCheckedChanged += (_, _) => Change(x => x with { Bold = bold.IsChecked == true });
            _fields.Children.Add(bold);
            Text("Text colour", e.Colour, v => Change(x => x with { Colour = v }));
            Number("Black outline", e.Outline, 0, 12, v => Change(x => x with { Outline = v }));
            Text("Background colour", e.Background, v => Change(x => x with { Background = v }));
            Number("Background opacity (%)", e.BackgroundOpacity * 100, 0, 100, v => Change(x => x with { BackgroundOpacity = v / 100 }));
            var alignment = new ComboBox { ItemsSource = new[] { "Left", "Center", "Right" }, SelectedItem = e.Alignment };
            alignment.SelectionChanged += (_, _) => { if (alignment.SelectedItem is string value) Change(x => x with { Alignment = value }); };
            _fields.Children.Add(alignment);
        }
        Button("Duplicate", () => Run(() =>
        {
            _model!.DuplicateTimedEffect(e, Blur);
        }));
        Button("Delete", () => { (Blur ? _model!.BlurEffects : _model!.TextEffects).Remove(e); _model.NotifyTimedEffectsChanged(); });
    }
    private void Button(string label, Action action) { var b = new Button { Content = label }; b.Click += (_, _) => action(); _fields.Children.Add(b); }
    private void Number(string label, double value, double min, double max, Action<double> update)
    {
        _fields.Children.Add(new TextBlock { Text = label });
        var box = new NumericUpDown { Value = (decimal)value, Minimum = (decimal)min, Maximum = (decimal)max, Increment = .1m };
        box.LostFocus += (_, _) => { if (box.Value is { } v && (double)v != value) update((double)v); };
        _fields.Children.Add(box);
    }
    private void Text(string label, string value, Action<string> update, bool multiline = false)
    {
        _fields.Children.Add(new TextBlock { Text = label });
        var box = new TextBox { Text = value, AcceptsReturn = multiline, TextWrapping = TextWrapping.Wrap, MaxLength = multiline ? TimedEffectState.MaximumCaptionLength : 200 };
        box.LostFocus += (_, _) => { if (box.Text != value) update(box.Text ?? ""); };
        _fields.Children.Add(box);
    }
}
