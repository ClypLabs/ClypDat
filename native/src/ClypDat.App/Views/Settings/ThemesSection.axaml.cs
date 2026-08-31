using Avalonia.Controls;
using Avalonia.Interactivity;
using ClypDat.App.ViewModels;

namespace ClypDat.App.Views.Settings;

public sealed partial class ThemesSection : UserControl
{
    public ThemesSection() => InitializeComponent();
    private MainWindowViewModel? ViewModel => DataContext as MainWindowViewModel;
    private void NewCustomThemeButton_OnClick(object? sender, RoutedEventArgs e) => ViewModel?.NewCustomTheme();
    private void EditCustomThemeButton_OnClick(object? sender, RoutedEventArgs e) { if ((sender as Control)?.Tag is ClypDat.Core.Settings.CustomThemeSettings theme) ViewModel?.EditCustomTheme(theme); }
    private void DuplicateCustomThemeButton_OnClick(object? sender, RoutedEventArgs e) { if ((sender as Control)?.Tag is ClypDat.Core.Settings.CustomThemeSettings theme) ViewModel?.DuplicateCustomTheme(theme); }
    private void DeleteCustomThemeButton_OnClick(object? sender, RoutedEventArgs e) { if ((sender as Control)?.Tag is ClypDat.Core.Settings.CustomThemeSettings theme) ViewModel?.DeleteCustomTheme(theme); }
    private void ApplyCustomThemeButton_OnClick(object? sender, RoutedEventArgs e) => ViewModel?.ApplyCustomTheme();
    private void CancelCustomThemeButton_OnClick(object? sender, RoutedEventArgs e) => ViewModel?.CancelCustomTheme();
}
