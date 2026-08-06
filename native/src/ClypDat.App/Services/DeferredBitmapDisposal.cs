using Avalonia.Threading;

namespace ClypDat.App.Services;

// Disposing a Bitmap the instant it is unbound from an Image is a native
// use-after-free waiting to happen. Clearing the binding only updates the
// visual tree; the compositor renders on its own thread from the LAST
// committed frame, which still holds that bitmap's SkiaSharp surface. Freeing
// the pixels out from under it crashes the process with no managed exception
// and nothing in the log - which is exactly the shape of the "clicked a clip
// while the library was still loading and it just died" reports.
//
// The window where that matters is worst on a cold start: opening a clip
// collapses LibraryScrollViewer, every realized card raises
// EffectiveViewportChanged with an empty viewport in one dispatcher turn, and
// each one used to free its thumbnail right there - a whole screenful of
// bitmaps released while the compositor was still drawing the frame that used
// them.
//
// Two Background-priority hops put the free after layout and render have been
// serviced for the frame that no longer references the bitmap. Post is
// thread-safe, so callers off the UI thread (the hover-preview session
// teardown) can use this too. BitmapCache solves the same problem by never
// disposing at all and letting the finalizer do it; that is fine for its small
// thumbnails but not for the 1920x1080 hover-preview buffers, which are worth
// releasing promptly.
internal static class DeferredBitmapDisposal
{
    public static void Release(IDisposable? bitmap)
    {
        if (bitmap is null) return;

        Dispatcher.UIThread.Post(
            () => Dispatcher.UIThread.Post(
                () =>
                {
                    try { bitmap.Dispose(); }
                    catch { /* Freeing a preview image must never take the app down. */ }
                },
                DispatcherPriority.Background),
            DispatcherPriority.Background);
    }
}
