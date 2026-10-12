using Avalonia.Interactivity;
using Avalonia.Threading;
using ClypDat.App.Services;
using ClypDat.App.ViewModels;

namespace ClypDat.App.Views;

// Match stats wiring that belongs to the window: the editor rail's Stats
// button and the CS2 listener's end-of-match hand-off.
public sealed partial class MainWindow
{
    private void EditorStatsSidebarButton_OnClick(object? sender, RoutedEventArgs e) =>
        ViewModel?.OpenEditorSidebar(EditorSidebarSection.Stats);

    // Raised on the listener's request thread.
    private void Cs2GsiListener_OnMatchCompleted(object? sender, MatchStats match) =>
        Dispatcher.UIThread.Post(() => ViewModel?.OnCs2MatchCompleted(match));
}
