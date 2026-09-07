using Avalonia.Controls;
using Avalonia.Interactivity;
using ClypDat.App.ViewModels;

namespace ClypDat.App.Views.Settings;

public sealed partial class ThemesSection : UserControl
{
    public ThemesSection()
    {
        // Clicking away from a text box, and Enter to leave one, are handled
        // for every box in the window by MainWindow rather than section by
        // section.
        InitializeComponent();
    }
    private MainWindowViewModel? ViewModel => DataContext as MainWindowViewModel;
    private void NewCustomThemeButton_OnClick(object? sender, RoutedEventArgs e) => ViewModel?.NewCustomTheme();
    private void EditCustomThemeButton_OnClick(object? sender, RoutedEventArgs e) { if ((sender as Control)?.Tag is ClypDat.Core.Settings.CustomThemeSettings theme) ViewModel?.EditCustomTheme(theme); }
    private void DuplicateCustomThemeButton_OnClick(object? sender, RoutedEventArgs e) { if ((sender as Control)?.Tag is ClypDat.Core.Settings.CustomThemeSettings theme) ViewModel?.DuplicateCustomTheme(theme); }
    private void DeleteCustomThemeButton_OnClick(object? sender, RoutedEventArgs e) { if ((sender as Control)?.Tag is ClypDat.Core.Settings.CustomThemeSettings theme) ViewModel?.DeleteCustomTheme(theme); }
    private void ApplyCustomThemeButton_OnClick(object? sender, RoutedEventArgs e) => ViewModel?.ApplyCustomTheme();
    private void CancelCustomThemeButton_OnClick(object? sender, RoutedEventArgs e) => ViewModel?.CancelCustomTheme();
    // A recent swatch's DataContext is the colour string, so which picker it
    // fills has to come from the handler. Two handlers rather than walking up to
    // the owning ContentControl - this file is a list of one-line dispatches and
    // an ancestor walk would be the only thing in it that needs reading twice.
    private void BaseRecentThemeColorButton_OnClick(object? sender, RoutedEventArgs e) { if ((sender as Control)?.Tag is string color) ViewModel?.UseRecentBaseColor(color); }
    private void AccentRecentThemeColorButton_OnClick(object? sender, RoutedEventArgs e) { if ((sender as Control)?.Tag is string color) ViewModel?.UseRecentAccentColor(color); }

}
