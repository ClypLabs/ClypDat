using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia;
using Avalonia.VisualTree;
using ClypDat.App.ViewModels;

namespace ClypDat.App.Views.Settings;

public sealed partial class ThemesSection : UserControl
{
    public ThemesSection()
    {
        InitializeComponent();
        AddHandler(PointerPressedEvent, ThemeTextBox_OnAnyPointerPressed, RoutingStrategies.Tunnel);
        AddHandler(KeyDownEvent, ThemeTextBox_OnKeyDown, RoutingStrategies.Tunnel);
    }
    private MainWindowViewModel? ViewModel => DataContext as MainWindowViewModel;
    private void NewCustomThemeButton_OnClick(object? sender, RoutedEventArgs e) => ViewModel?.NewCustomTheme();
    private void EditCustomThemeButton_OnClick(object? sender, RoutedEventArgs e) { if ((sender as Control)?.Tag is ClypDat.Core.Settings.CustomThemeSettings theme) ViewModel?.EditCustomTheme(theme); }
    private void DuplicateCustomThemeButton_OnClick(object? sender, RoutedEventArgs e) { if ((sender as Control)?.Tag is ClypDat.Core.Settings.CustomThemeSettings theme) ViewModel?.DuplicateCustomTheme(theme); }
    private void DeleteCustomThemeButton_OnClick(object? sender, RoutedEventArgs e) { if ((sender as Control)?.Tag is ClypDat.Core.Settings.CustomThemeSettings theme) ViewModel?.DeleteCustomTheme(theme); }
    private void ApplyCustomThemeButton_OnClick(object? sender, RoutedEventArgs e) => ViewModel?.ApplyCustomTheme();
    private void CancelCustomThemeButton_OnClick(object? sender, RoutedEventArgs e) => ViewModel?.CancelCustomTheme();
    private void SelectBaseThemeColorButton_OnClick(object? sender, RoutedEventArgs e) => ViewModel?.SelectBaseThemeColor();
    private void SelectAccentThemeColorButton_OnClick(object? sender, RoutedEventArgs e) => ViewModel?.SelectAccentThemeColor();
    private void UsePickerColorButton_OnClick(object? sender, RoutedEventArgs e) => ViewModel?.UsePickerColor();
    private void RecentThemeColorButton_OnClick(object? sender, RoutedEventArgs e) { if ((sender as Control)?.Tag is string color) ViewModel?.UseRecentThemeColor(color); }

    private void ThemeTextBox_OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || e.Source is not TextBox) return;
        e.Handled = true;
        Focus();
    }

    private void ThemeTextBox_OnAnyPointerPressed(object? sender, PointerEventArgs e)
    {
        if (e.Source is not Visual source || source.FindAncestorOfType<TextBox>() is not null) return;
        Focus();
    }
}
