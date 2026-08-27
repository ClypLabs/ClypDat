using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using ClypDat.App.ViewModels;

namespace ClypDat.App.Views.Settings;

public sealed partial class GeneralSection : UserControl
{
    public GeneralSection()
    {
        InitializeComponent();
    }

    // Settings markup lives here, but the handlers still belong to
    // MainWindow - their bodies reach all over its state. These forward
    // to the owning window rather than duplicating any of it.
    private MainWindow? Owner => TopLevel.GetTopLevel(this) as MainWindow;

    private void LicenseLinkText_OnPointerPressed(object? sender, PointerPressedEventArgs e)
        => Owner?.LicenseLinkText_OnPointerPressed(sender, e);

    private void RenameAllClipsButton_OnClick(object? sender, RoutedEventArgs e)
        => Owner?.RenameAllClipsButton_OnClick(sender, e);
}
