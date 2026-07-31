using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;

namespace ClypDat.App.Views;

public sealed partial class EditorToolsPanel : UserControl
{
    public event EventHandler? CloseRequested;
    public event EventHandler? TitleSubmitted;
    public event EventHandler? SaveTrimRequested;
    public event EventHandler? ExportRequested;
    public event EventHandler? ShareRequested;

    public EditorToolsPanel() => InitializeComponent();

    private void CloseButton_OnClick(object? sender, RoutedEventArgs e) => CloseRequested?.Invoke(this, EventArgs.Empty);
    private void SaveTrimButton_OnClick(object? sender, RoutedEventArgs e) => SaveTrimRequested?.Invoke(this, EventArgs.Empty);
    private void ExportButton_OnClick(object? sender, RoutedEventArgs e) => ExportRequested?.Invoke(this, EventArgs.Empty);
    private void ShareButton_OnClick(object? sender, RoutedEventArgs e) => ShareRequested?.Invoke(this, EventArgs.Empty);

    private void TitleBox_OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        e.Handled = true;
        TitleSubmitted?.Invoke(this, EventArgs.Empty);
    }

    private void Panel_OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape) return;
        e.Handled = true;
        CloseRequested?.Invoke(this, EventArgs.Empty);
    }
}
