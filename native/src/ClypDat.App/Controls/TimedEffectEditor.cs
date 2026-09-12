using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using ClypDat.App.Services;
using ClypDat.App.ViewModels;
using ClypDat.Core.Settings;

namespace ClypDat.App.Controls;

/// <summary>
/// Inspector card for text and blur. Placement happens on the video and timing
/// on the Video track; this card adds effects, lists them and edits their
/// style. Continuous edits (typing, sliders) apply live and save once the user
/// pauses. The fields are rebuilt only when the selection changes, so typing
/// never loses focus to a refresh.
/// </summary>
public sealed class TimedEffectEditor : StackPanel
{
    private static readonly string[] Swatches = ["#FFFFFF", "#000000", "#FFD400", "#FF3B30", "#34C759", "#0A84FF"];
    private static readonly IBrush TextBadge = new SolidColorBrush(Color.FromRgb(124, 92, 214));
    private static readonly IBrush BlurBadge = new SolidColorBrush(Color.FromRgb(52, 118, 204));
    private static readonly IBrush SelectedRow = new SolidColorBrush(Color.FromArgb(70, 19, 200, 181));
    private static string[]? _fonts;
    private readonly StackPanel _list = new() { Spacing = 2 };
    private readonly TextBlock _hint;
    private readonly StackPanel _fields = new() { Spacing = 10 };
    private readonly TextBlock _error = new() { Foreground = Brushes.OrangeRed, TextWrapping = TextWrapping.Wrap, FontSize = 11 };
    private readonly DispatcherTimer _commit = new() { Interval = TimeSpan.FromMilliseconds(400) };
    private readonly Dictionary<Guid, (Border Row, TextBlock Title, TextBlock Time, Button Eye)> _rows = [];
    private readonly List<Action> _sync = [];
    private MainWindowViewModel? _model;
    private Guid? _builtFor;
    private bool _syncing;
    private TextBox? _caption;

    public TimedEffectEditor()
    {
        Spacing = 10;
        _hint = Muted("Add text or blur, then drag it on the video to place it. Drag its clip on the Video track to change when it shows.");
        var header = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        header.Children.Add(new TextBlock { Text = "TEXT & BLUR", Classes = { "editorCardLabel" }, VerticalAlignment = VerticalAlignment.Center });
        var buttons = new Grid { ColumnDefinitions = new ColumnDefinitions("*,*"), ColumnSpacing = 8 };
        buttons.Children.Add(AddButton("+ Text", false, 0));
        buttons.Children.Add(AddButton("+ Blur", true, 1));
        Children.Add(header);
        Children.Add(buttons);
        Children.Add(_list);
        Children.Add(_hint);
        Children.Add(_fields);
        Children.Add(_error);
        _commit.Tick += (_, _) => Flush();
        DataContextChanged += (_, _) => Connect();
        DetachedFromVisualTree += (_, _) => Flush();
    }

    private Button AddButton(string label, bool blur, int column)
    {
        var button = new Button { Content = label, HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Center };
        Grid.SetColumn(button, column);
        ToolTip.SetTip(button, blur ? "Blur a region from the playhead for 3 seconds" : "Add a caption at the playhead for 3 seconds");
        button.Click += (_, _) => Run(() => _model?.RequestAddTimedEffect(blur));
        return button;
    }

    private void Connect()
    {
        if (_model is not null)
        {
            _model.TimedEffectsChanged -= OnChanged;
            _model.TimedEffectCaptionFocusRequested -= OnCaptionFocusRequested;
        }
        _model = DataContext as MainWindowViewModel;
        if (_model is not null)
        {
            _model.TimedEffectsChanged += OnChanged;
            _model.TimedEffectCaptionFocusRequested += OnCaptionFocusRequested;
        }
        _builtFor = null;
        Refresh();
    }

    private void OnChanged(object? sender, EventArgs e) => Refresh();

    private void OnCaptionFocusRequested(object? sender, EventArgs e) => Dispatcher.UIThread.Post(() =>
    {
        if (_caption is null) return;
        _caption.Focus();
        _caption.SelectAll();
    }, DispatcherPriority.Background);

    private void Run(Action action)
    {
        try { action(); _error.Text = ""; }
        catch (Exception e) { _error.Text = e.Message; }
    }

    private TimedVideoEffect? Selected => _model?.SelectedTimedEffect;

    /// <summary>Applies an edit now but saves it only after input pauses.</summary>
    private void Live(Func<TimedVideoEffect, TimedVideoEffect> update)
    {
        if (_syncing || Selected is not { } e) return;
        Run(() => _model!.SetTimedEffect(update(e), persist: false));
        _commit.Stop();
        _commit.Start();
    }

    /// <summary>Applies and saves a discrete edit (a toggle, a swatch).</summary>
    private void Apply(Func<TimedVideoEffect, TimedVideoEffect> update)
    {
        if (_syncing || Selected is not { } e) return;
        _commit.Stop();
        Run(() => _model!.SetTimedEffect(update(e), persist: true));
    }

    private void Flush()
    {
        if (!_commit.IsEnabled) return;
        _commit.Stop();
        Run(() => _model?.CommitTimedEffects());
    }

    private void Refresh()
    {
        if (_model is not { } model) { _list.Children.Clear(); _rows.Clear(); _fields.Children.Clear(); return; }
        RefreshList(model);
        _hint.IsVisible = model.TextEffects.Count == 0 && model.BlurEffects.Count == 0;
        if (_builtFor != model.SelectedTimedEffectId || (Selected is null) != (_fields.Children.Count == 0))
        {
            Flush();
            BuildFields();
        }
        else Sync();
    }

    private void RefreshList(MainWindowViewModel model)
    {
        var items = model.TextEffects.Select(e => (Effect: e, Blur: false)).Concat(model.BlurEffects.Select(e => (Effect: e, Blur: true)))
            .OrderBy(item => item.Effect.Start).ToList();
        if (!items.Select(item => item.Effect.Id).SequenceEqual(_rows.Keys))
        {
            _list.Children.Clear();
            _rows.Clear();
            foreach (var (effect, blur) in items) _rows[effect.Id] = BuildRow(effect.Id, blur);
            foreach (var row in _rows.Values) _list.Children.Add(row.Row);
        }
        foreach (var (effect, blur) in items)
        {
            var row = _rows[effect.Id];
            row.Title.Text = blur ? (effect.Shape == "Rectangle" ? "Blur" : $"Blur · {effect.Shape}") : string.IsNullOrWhiteSpace(effect.Text) ? "(empty caption)" : effect.Text.Replace('\n', ' ');
            row.Time.Text = $"{Timecode(effect.Start)} – {Timecode(effect.End)}";
            row.Eye.Content = effect.Visible ? "Hide" : "Show";
            row.Row.Background = effect.Id == model.SelectedTimedEffectId ? SelectedRow : Brushes.Transparent;
            row.Row.Opacity = effect.Visible ? 1 : .55;
        }
    }

    private (Border Row, TextBlock Title, TextBlock Time, Button Eye) BuildRow(Guid id, bool blur)
    {
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto,Auto"), ColumnSpacing = 8 };
        var badge = new Border
        {
            Width = 20, Height = 20, CornerRadius = new CornerRadius(4), Background = blur ? BlurBadge : TextBadge, VerticalAlignment = VerticalAlignment.Center,
            Child = new TextBlock { Text = blur ? "B" : "T", FontSize = 11, FontWeight = FontWeight.Bold, Foreground = Brushes.White,
                HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center }
        };
        var title = new TextBlock { FontSize = 12, FontWeight = FontWeight.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis,
            Foreground = AppThemeService.Brush("Text_E6EEF7", "#E6EEF7") };
        var time = new TextBlock { FontSize = 10, Foreground = AppThemeService.Brush("Text_8EA1B6", "#8EA1B6"), FontFamily = new FontFamily("Consolas") };
        var text = new StackPanel { Spacing = 1, VerticalAlignment = VerticalAlignment.Center, Children = { title, time } };
        Grid.SetColumn(text, 1);
        var eye = new Button { Classes = { "linkButton" }, Padding = new Thickness(4, 1), FontSize = 11, VerticalAlignment = VerticalAlignment.Center };
        eye.Click += (_, _) => Run(() =>
        {
            if (_model?.FindTimedEffect(id, out _) is { } effect) _model.SetTimedEffect(effect with { Visible = !effect.Visible }, persist: true);
        });
        Grid.SetColumn(eye, 2);
        var delete = new Button { Content = "✕", Classes = { "linkButton" }, Padding = new Thickness(4, 1), FontSize = 11, VerticalAlignment = VerticalAlignment.Center };
        ToolTip.SetTip(delete, "Delete (Del)");
        delete.Click += (_, _) => Run(() => _model?.RemoveTimedEffect(id));
        Grid.SetColumn(delete, 3);
        grid.Children.Add(badge);
        grid.Children.Add(text);
        grid.Children.Add(eye);
        grid.Children.Add(delete);
        var row = new Border { Padding = new Thickness(6, 4), CornerRadius = new CornerRadius(6), Cursor = new Cursor(StandardCursorType.Hand), Child = grid };
        row.PointerPressed += (sender, e) =>
        {
            if (_model?.FindTimedEffect(id, out _) is not { } effect) return;
            _model.SelectTimedEffect(id);
            var now = _model.CurrentTime.TotalSeconds;
            if (now < effect.Start || now >= effect.End) _model.RequestTimedEffectSeek(TimeSpan.FromSeconds(effect.Start));
            e.Handled = true;
        };
        return (row, title, time, eye);
    }

    private void BuildFields()
    {
        _fields.Children.Clear();
        _sync.Clear();
        _caption = null;
        _builtFor = _model?.SelectedTimedEffectId;
        if (Selected is not { } e || _model is null) return;
        var blur = _model.IsBlurEffect(e.Id);
        _fields.Children.Add(new Border { Height = 1, Background = AppThemeService.Brush("Surface_263743", "#263743"), Margin = new Thickness(0, 2) });
        if (blur) BuildBlurFields();
        else BuildTextFields();
        BuildTiming();
        var footer = new Grid { ColumnDefinitions = new ColumnDefinitions("*,*"), ColumnSpacing = 8 };
        var duplicate = new Button { Content = "Duplicate", HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Center };
        ToolTip.SetTip(duplicate, "Duplicate (Ctrl+D)");
        duplicate.Click += (_, _) => Run(() => { if (Selected is { } s) _model!.DuplicateTimedEffect(s.Id); });
        var delete = new Button { Content = "Delete", HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Center };
        ToolTip.SetTip(delete, "Delete (Del)");
        delete.Click += (_, _) => Run(() => { if (Selected is { } s) _model!.RemoveTimedEffect(s.Id); });
        Grid.SetColumn(delete, 1);
        footer.Children.Add(duplicate);
        footer.Children.Add(delete);
        _fields.Children.Add(footer);
        Sync();
    }

    private void BuildTextFields()
    {
        var caption = new TextBox { AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, MinHeight = 56, MaxLength = TimedEffectState.MaximumCaptionLength, Watermark = "Caption" };
        caption.TextChanged += (_, _) => Live(x => x with { Text = caption.Text ?? "" });
        _caption = caption;
        _sync.Add(() => { if (!caption.IsFocused && caption.Text != Selected?.Text) caption.Text = Selected?.Text; });
        _fields.Children.Add(caption);

        var fontRow = new Grid { ColumnDefinitions = new ColumnDefinitions("*,78"), ColumnSpacing = 6 };
        var font = new ComboBox { ItemsSource = _fonts ??= FontManager.Current.SystemFonts.Select(f => f.Name).Distinct().Order().ToArray(), HorizontalAlignment = HorizontalAlignment.Stretch };
        font.SelectionChanged += (_, _) => { if (font.SelectedItem is string name) Apply(x => x with { Font = name }); };
        _sync.Add(() => { if (!Equals(font.SelectedItem, Selected?.Font)) font.SelectedItem = Selected?.Font; });
        var size = new NumericUpDown { Minimum = 8, Maximum = 300, Increment = 2, FormatString = "0", ShowButtonSpinner = false };
        ToolTip.SetTip(size, "Font size at 1080p. Drag a text corner on the video to scale it.");
        size.ValueChanged += (_, _) => { if (size.Value is { } v) Live(x => x with { FontSize = Math.Clamp((double)v, 8, 300) }); };
        _sync.Add(() => { if (!size.IsKeyboardFocusWithin && Selected is { } s && size.Value != (decimal)Math.Round(s.FontSize)) size.Value = (decimal)Math.Round(s.FontSize); });
        Grid.SetColumn(size, 1);
        fontRow.Children.Add(font);
        fontRow.Children.Add(size);
        _fields.Children.Add(fontRow);

        var style = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4 };
        var bold = new ToggleButton { Content = "B", FontWeight = FontWeight.Bold, MinWidth = 32 };
        ToolTip.SetTip(bold, "Bold");
        bold.IsCheckedChanged += (_, _) => Apply(x => x with { Bold = bold.IsChecked == true });
        _sync.Add(() => bold.IsChecked = Selected?.Bold == true);
        style.Children.Add(bold);
        style.Children.Add(new Border { Width = 8 });
        foreach (var alignment in new[] { "Left", "Center", "Right" })
        {
            var button = new ToggleButton { Content = alignment, FontSize = 11 };
            button.Click += (_, _) => Apply(x => x with { Alignment = alignment });
            _sync.Add(() => button.IsChecked = Selected?.Alignment == alignment);
            style.Children.Add(button);
        }
        _fields.Children.Add(style);

        _fields.Children.Add(Label("Text colour"));
        _fields.Children.Add(ColourRow(e => e.Colour, (e, v) => e with { Colour = v }));
        _fields.Children.Add(SliderRow("Outline", 0, 12, 0.5, e => e.Outline, (e, v) => e with { Outline = v }, v => v.ToString("0.#", CultureInfo.CurrentCulture)));
        _fields.Children.Add(Label("Background"));
        _fields.Children.Add(ColourRow(e => e.Background, (e, v) => e with { Background = v, BackgroundOpacity = e.BackgroundOpacity > 0 ? e.BackgroundOpacity : .6 }));
        _fields.Children.Add(SliderRow("Background opacity", 0, 100, 1, e => e.BackgroundOpacity * 100, (e, v) => e with { BackgroundOpacity = v / 100 }, v => $"{v:0}%"));
    }

    private void BuildBlurFields()
    {
        _fields.Children.Add(Muted("Drag the box on the video to cover what should be hidden. Drag its edges to resize."));
        _fields.Children.Add(Label("Shape"));
        var shapes = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4 };
        foreach (var shape in TimedEffectState.BlurShapes)
        {
            var button = new ToggleButton { Content = shape, FontSize = 11 };
            button.Click += (_, _) => Apply(x => x with { Shape = shape });
            _sync.Add(() => button.IsChecked = Selected?.Shape == shape);
            shapes.Children.Add(button);
        }
        _fields.Children.Add(shapes);
        _fields.Children.Add(SliderRow("Strength", 1, 100, 1, e => e.Strength, (e, v) => e with { Strength = v }, v => $"{v:0}"));
    }

    private void BuildTiming()
    {
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("40,*,Auto"), RowDefinitions = new RowDefinitions("Auto,Auto"), ColumnSpacing = 6, RowSpacing = 6 };
        void Row(int row, string label, Func<TimedVideoEffect, double> read, Func<TimedVideoEffect, double, TimedVideoEffect> write)
        {
            var name = Label(label);
            name.VerticalAlignment = VerticalAlignment.Center;
            Grid.SetRow(name, row);
            var box = new TextBox { FontFamily = new FontFamily("Consolas"), FontSize = 12 };
            void Commit()
            {
                if (_syncing || Selected is not { } e) return;
                if (ParseTimecode(box.Text) is { } seconds) Apply(x => write(x, seconds));
                else box.Text = Timecode(read(e));
            }
            box.LostFocus += (_, _) => Commit();
            box.KeyDown += (_, args) => { if (args.Key == Key.Enter) { Commit(); args.Handled = true; } };
            _sync.Add(() => { if (!box.IsFocused && Selected is { } e) box.Text = Timecode(read(e)); });
            Grid.SetRow(box, row);
            Grid.SetColumn(box, 1);
            var playhead = new Button { Content = "⌖ Playhead", FontSize = 11, Padding = new Thickness(8, 4) };
            ToolTip.SetTip(playhead, $"Set {label.ToLowerInvariant()} to the playhead");
            playhead.Click += (_, _) => Apply(x => write(x, _model!.CurrentTime.TotalSeconds));
            Grid.SetRow(playhead, row);
            Grid.SetColumn(playhead, 2);
            grid.Children.Add(name);
            grid.Children.Add(box);
            grid.Children.Add(playhead);
        }
        var duration = () => _model?.Duration.TotalSeconds ?? 0;
        Row(0, "Start", e => e.Start, (e, v) => e with { Start = Math.Clamp(v, 0, Math.Max(0, e.End - .1)) });
        Row(1, "End", e => e.End, (e, v) => e with { End = Math.Clamp(v, e.Start + .1, Math.Max(e.Start + .1, duration())) });
        _fields.Children.Add(grid);
    }

    private Control SliderRow(string label, double min, double max, double step, Func<TimedVideoEffect, double> read,
        Func<TimedVideoEffect, double, TimedVideoEffect> write, Func<double, string> format)
    {
        var value = new TextBlock { FontSize = 12, Foreground = AppThemeService.Brush("Text_D6E2F0", "#D6E2F0") };
        var header = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        header.Children.Add(Label(label));
        Grid.SetColumn(value, 1);
        header.Children.Add(value);
        var slider = new Slider { Minimum = min, Maximum = max, SmallChange = step, TickFrequency = step, IsSnapToTickEnabled = true };
        slider.ValueChanged += (_, _) =>
        {
            value.Text = format(slider.Value);
            Live(x => write(x, slider.Value));
        };
        _sync.Add(() =>
        {
            if (Selected is not { } e) return;
            var current = Math.Clamp(read(e), min, max);
            if (Math.Abs(slider.Value - current) > step / 2) slider.Value = current;
            value.Text = format(current);
        });
        return new StackPanel { Spacing = 2, Children = { header, slider } };
    }

    private Control ColourRow(Func<TimedVideoEffect, string> read, Func<TimedVideoEffect, string, TimedVideoEffect> write)
    {
        var row = new WrapPanel { Orientation = Orientation.Horizontal };
        var swatches = new List<(Button Button, string Colour)>();
        foreach (var colour in Swatches)
        {
            var swatch = new Button
            {
                Width = 22, Height = 22, Padding = new Thickness(0), Margin = new Thickness(0, 0, 6, 6), CornerRadius = new CornerRadius(11),
                Background = new SolidColorBrush(Color.Parse(colour)), BorderBrush = Brushes.White
            };
            ToolTip.SetTip(swatch, colour);
            swatch.Click += (_, _) => Apply(x => write(x, colour));
            swatches.Add((swatch, colour));
            row.Children.Add(swatch);
        }
        var hex = new TextBox { Width = 84, FontFamily = new FontFamily("Consolas"), FontSize = 12, MaxLength = 9, Margin = new Thickness(0, 0, 0, 6) };
        void Commit()
        {
            if (_syncing || Selected is not { } e) return;
            var text = (hex.Text ?? "").Trim();
            if (!text.StartsWith('#')) text = "#" + text;
            if (Color.TryParse(text, out _)) Apply(x => write(x, text.ToUpperInvariant()));
            else hex.Text = read(e);
        }
        hex.LostFocus += (_, _) => Commit();
        hex.KeyDown += (_, args) => { if (args.Key == Key.Enter) { Commit(); args.Handled = true; } };
        row.Children.Add(hex);
        _sync.Add(() =>
        {
            if (Selected is not { } e) return;
            var current = read(e);
            foreach (var (button, colour) in swatches)
                button.BorderThickness = new Thickness(string.Equals(colour, current, StringComparison.OrdinalIgnoreCase) ? 2 : 0);
            if (!hex.IsFocused) hex.Text = current;
        });
        return row;
    }

    private void Sync()
    {
        _syncing = true;
        try { foreach (var action in _sync) action(); }
        finally { _syncing = false; }
    }

    private static TextBlock Label(string text) => new() { Text = text, FontSize = 12, Foreground = AppThemeService.Brush("Text_8EA1B6", "#8EA1B6") };
    private static TextBlock Muted(string text) => new() { Text = text, FontSize = 11, TextWrapping = TextWrapping.Wrap, Foreground = AppThemeService.Brush("Text_6B7C8C", "#6B7C8C") };

    public static string Timecode(double seconds)
    {
        seconds = Math.Max(0, seconds);
        var minutes = (int)(seconds / 60);
        return $"{minutes}:{(seconds - minutes * 60).ToString("00.00", CultureInfo.InvariantCulture)}";
    }

    /// <summary>Accepts m:ss.ff, h:mm:ss.ff or plain seconds.</summary>
    public static double? ParseTimecode(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var total = 0d;
        foreach (var part in text.Trim().Split(':'))
        {
            if (!double.TryParse(part.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out var value) || value < 0) return null;
            total = total * 60 + value;
        }
        return double.IsFinite(total) ? total : null;
    }
}
