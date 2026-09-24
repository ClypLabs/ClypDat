using System.Runtime.CompilerServices;
using Avalonia.Controls;

namespace ClypDat.App.Services;

// Closing a ClypDat window through Windows - Alt+F4, the taskbar, End task -
// quits the app, so closing an export, share, confirm or settings dialog that
// way never silently drops the work behind it. A plain message box has no work
// behind it: closing it that way just closes it.
internal static class DialogCloseQuitPolicy
{
    private static readonly ConditionalWeakTable<Window, object> MessageBoxes = new();

    public static void MarkMessageBox(Window window) => MessageBoxes.AddOrUpdate(window, MessageBoxes);

    public static bool IsMessageBox(Window window) => MessageBoxes.TryGetValue(window, out _);

    public static bool QuitsApp(Window window, bool isProgrammatic, WindowCloseReason reason, bool allowRealClose)
        => QuitsApp(IsMessageBox(window), isProgrammatic, reason, allowRealClose);

    internal static bool QuitsApp(bool messageBox, bool isProgrammatic, WindowCloseReason reason, bool allowRealClose)
        => !messageBox && !allowRealClose && !isProgrammatic && reason == WindowCloseReason.WindowClosing;
}
