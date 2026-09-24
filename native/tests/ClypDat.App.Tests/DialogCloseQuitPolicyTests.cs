using Avalonia.Controls;
using ClypDat.App.Services;
using ClypDat.Capture.Abstractions;
using Xunit;

namespace ClypDat.App.Tests;

// Alt+F4 on a dialog quits ClypDat so no work behind it is dropped; a message
// box has none, so closing one that way only closes it. And a save refused
// because another is running raises no message box at all.
public sealed class DialogCloseQuitPolicyTests
{
    [Theory]
    [InlineData(false, false, WindowCloseReason.WindowClosing, false, true)]  // Alt+F4 on a confirm or progress dialog
    [InlineData(true, false, WindowCloseReason.WindowClosing, false, false)]  // Alt+F4 on a message box
    [InlineData(false, true, WindowCloseReason.WindowClosing, false, false)]  // its own OK, Cancel, close or Esc
    [InlineData(false, false, WindowCloseReason.OwnerWindowClosing, false, false)]
    [InlineData(false, false, WindowCloseReason.WindowClosing, true, false)]  // the app is already quitting
    public void OnlyWorkBearingDialogsQuitTheApp(bool messageBox, bool programmatic, WindowCloseReason reason, bool allowRealClose, bool quits)
        => Assert.Equal(quits, DialogCloseQuitPolicy.QuitsApp(messageBox, programmatic, reason, allowRealClose));

    [Fact]
    public void MessageBoxMarkBelongsToThatWindowOnly()
    {
        AvaloniaTestThread.Run(() =>
        {
            var message = new Window();
            var confirm = new Window();
            DialogCloseQuitPolicy.MarkMessageBox(message);
            Assert.True(DialogCloseQuitPolicy.IsMessageBox(message));
            Assert.False(DialogCloseQuitPolicy.IsMessageBox(confirm));
            Assert.False(DialogCloseQuitPolicy.QuitsApp(message, false, WindowCloseReason.WindowClosing, false));
            Assert.True(DialogCloseQuitPolicy.QuitsApp(confirm, false, WindowCloseReason.WindowClosing, false));
        }, TimeSpan.FromSeconds(30), "Message box marking did not finish.");
    }

    [Theory]
    [InlineData("A replay save is already in progress.", true)]
    [InlineData("A replay save is already in progress", true)] // The native recorder's wording.
    [InlineData(" A replay save is already in progress. ", true)]
    [InlineData("Not enough free space on the library drive.", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void OnlyTheBusyRefusalSkipsTheMessageBox(string? error, bool busy)
        => Assert.Equal(busy, CaptureWorkerSaveErrors.IsBusy(error));
}
