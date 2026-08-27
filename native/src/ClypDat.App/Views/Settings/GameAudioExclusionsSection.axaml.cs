using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using ClypDat.App.ViewModels;

namespace ClypDat.App.Views.Settings;

public sealed partial class GameAudioExclusionsSection : UserControl
{
    public GameAudioExclusionsSection()
    {
        InitializeComponent();
    }

    // Settings markup lives here, but the handlers still belong to
    // MainWindow - their bodies reach all over its state. These forward
    // to the owning window rather than duplicating any of it.
    private MainWindow? Owner => TopLevel.GetTopLevel(this) as MainWindow;

    private void RefreshProcessesButton_OnClick(object? sender, RoutedEventArgs e)
        => Owner?.RefreshProcessesButton_OnClick(sender, e);

    private void AddSelectedProcessButton_OnClick(object? sender, RoutedEventArgs e)
        => Owner?.AddSelectedProcessButton_OnClick(sender, e);

    private void RemoveExcludedProcessButton_OnClick(object? sender, RoutedEventArgs e)
        => Owner?.RemoveExcludedProcessButton_OnClick(sender, e);

    // Lives here rather than on MainWindow: the text box is inside this
    // control's own name scope, so MainWindow.FindControl can no longer see it.
    private void AddExcludedProcessButton_OnClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not MainWindowViewModel viewModel) return;
        if (this.FindControl<TextBox>("ExcludedProcessTextBox") is not { } textBox) return;
        viewModel.AddExcludedProcess(textBox.Text ?? string.Empty);
        textBox.Text = string.Empty;
    }
}
