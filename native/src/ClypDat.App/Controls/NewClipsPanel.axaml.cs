using System;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media;

namespace ClypDat.App.Controls;

/// <summary>
/// The "N clips saved" popup frame. Hosted both by MainWindow's in-window
/// overlay and by <see cref="Views.NewClipsDialog"/>, the top-level window used
/// while the editor is open - one tree, so a restyle cannot reach one and miss
/// the other, which is exactly how the editor popup ended up rendering a design
/// four commits out of date.
///
/// The cards themselves are built in code by MainWindow and appended to
/// <see cref="Cards"/>; only the chrome lives here. Everything a host needs goes
/// through the members below - the x:Name fields belong to this control's own
/// namescope and are not reachable from MainWindow.
/// </summary>
public sealed partial class NewClipsPanel : UserControl
{
    public NewClipsPanel() => InitializeComponent();

    public event EventHandler<RoutedEventArgs>? CloseRequested;
    public event EventHandler<RoutedEventArgs>? DeleteRequested;
    public event EventHandler<RoutedEventArgs>? ViewAllRequested;

    /// <summary>Row host the built cards are appended to.</summary>
    public StackPanel Cards => CardsPanel;

    /// <summary>
    /// The frame's own background. Opaque, which is what makes it safe to hand
    /// to WindowTransparencyFallback when this panel is a window's whole
    /// content: a fully opaque brush means no whole-window alpha is applied, so
    /// the popup can never render see-through over what is behind it.
    /// </summary>
    public IBrush? PanelBackground => Card.Background;

    public void SetPanelBackground(IBrush brush) => Card.Background = brush;

    public void SetSummary(string title, string subtitle)
    {
        TitleText.Text = title;
        SubtitleText.Text = subtitle;
    }

    /// <summary>
    /// Sized by the caller from the card grid it just built, so the frame is
    /// exactly as wide as its columns rather than a fixed dialog width.
    /// </summary>
    public void SetCardWidth(double width) => Width = width;

    public void SetDeleteLabel(string label) => DeleteButton.Content = label;

    public void SetPrimaryAction(string label) => ViewAllButton.Content = label;

    private void CloseButton_OnClick(object? sender, RoutedEventArgs e) => CloseRequested?.Invoke(this, e);

    private void DeleteButton_OnClick(object? sender, RoutedEventArgs e) => DeleteRequested?.Invoke(this, e);

    private void ViewAllButton_OnClick(object? sender, RoutedEventArgs e) => ViewAllRequested?.Invoke(this, e);
}
