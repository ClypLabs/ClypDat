namespace ClypDat.App.Services;

internal enum NewClipsPresentation
{
    Deferred,
    LibraryOverlay,
    EditorWindow
}

// Keeps notification placement independent from the UI plumbing that renders
// it. A hidden or minimized owner must never create a desktop-level popup.
internal static class NewClipsPresentationPolicy
{
    public static NewClipsPresentation Resolve(bool isWindowVisible, bool isWindowMinimized, bool isEditorVisible) =>
        !isWindowVisible || isWindowMinimized
            ? NewClipsPresentation.Deferred
            : isEditorVisible
                ? NewClipsPresentation.EditorWindow
                : NewClipsPresentation.LibraryOverlay;
}
