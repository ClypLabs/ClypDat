using System.Runtime.InteropServices;
using ComTypes = System.Runtime.InteropServices.ComTypes;

namespace ClypDat.App.Services;

// Drags a file out of the app the way Explorer does, thumbnail and all.
//
// Avalonia's own DragDrop.DoDragDrop takes a managed DataObject and hands it
// to the OS wrapped in Avalonia's own IDataObject implementation. That works
// - the file lands in Discord - but the shell has nothing it recognises to
// draw a drag image from, so the user gets a bare cursor with no indication
// of WHAT is being dragged.
//
// Asking the shell for the file's own data object instead (the same one
// Explorer uses for its items) means the shell already knows how to render
// it, and IDragSourceHelper then produces the familiar translucent thumbnail
// that follows the cursor. Everything here is best-effort: any failure
// returns false and the caller falls back to Avalonia's plain drag.
internal static class ShellFileDrag
{
    public static bool TryDragFile(string path)
    {
        if (!OperatingSystem.IsWindows() || string.IsNullOrWhiteSpace(path)) return false;

        var pidl = IntPtr.Zero;
        var parentPtr = IntPtr.Zero;
        var dataObjectPtr = IntPtr.Zero;
        try
        {
            if (SHParseDisplayName(path, IntPtr.Zero, out pidl, 0, out _) != 0 || pidl == IntPtr.Zero) return false;

            var shellFolderIid = IidShellFolder;
            if (SHBindToParent(pidl, ref shellFolderIid, out parentPtr, out var childPidl) != 0 ||
                parentPtr == IntPtr.Zero || childPidl == IntPtr.Zero)
            {
                return false;
            }

            if (Marshal.GetObjectForIUnknown(parentPtr) is not IShellFolder parent) return false;

            var dataObjectIid = IidDataObject;
            // childPidl belongs to pidl's memory block - borrowed, never freed
            // separately.
            if (parent.GetUIObjectOf(IntPtr.Zero, 1, new[] { childPidl }, ref dataObjectIid, IntPtr.Zero, out dataObjectPtr) != 0 ||
                dataObjectPtr == IntPtr.Zero)
            {
                return false;
            }

            if (Marshal.GetObjectForIUnknown(dataObjectPtr) is not ComTypes.IDataObject data) return false;

            TryAttachDragImage(data);

            // Blocks for the whole gesture, exactly like Avalonia's version,
            // so the caller's drag-active UI still brackets it correctly.
            DoDragDrop(data, new DropSource(), DropEffectCopy, out _);
            return true;
        }
        catch (Exception error)
        {
            AppLog.Debug($"Shell drag unavailable, falling back to the plain drag: {error.Message}");
            return false;
        }
        finally
        {
            if (dataObjectPtr != IntPtr.Zero) Marshal.Release(dataObjectPtr);
            if (parentPtr != IntPtr.Zero) Marshal.Release(parentPtr);
            if (pidl != IntPtr.Zero) CoTaskMemFree(pidl);
        }
    }

    // Null hwnd/point tells the helper to build the image from the data
    // object itself, which for a shell item means its real thumbnail. Purely
    // cosmetic, so a failure here still leaves a working drag.
    private static void TryAttachDragImage(ComTypes.IDataObject data)
    {
        try
        {
            var helperType = Type.GetTypeFromCLSID(ClsidDragDropHelper);
            if (helperType is null) return;
            if (Activator.CreateInstance(helperType) is IDragSourceHelper helper)
            {
                helper.InitializeFromWindow(IntPtr.Zero, IntPtr.Zero, data);
            }
        }
        catch (Exception error)
        {
            AppLog.Debug($"Drag image unavailable (drag itself is unaffected): {error.Message}");
        }
    }

    private const uint DropEffectCopy = 1;
    private const uint MkLeftButton = 0x0001;
    private const int DragDropSDrop = 0x00040100;
    private const int DragDropSCancel = 0x00040101;
    private const int DragDropSUseDefaultCursors = 0x00040102;

    private static Guid IidShellFolder = new("000214E6-0000-0000-C000-000000000046");
    private static Guid IidDataObject = new("0000010E-0000-0000-C000-000000000046");
    private static readonly Guid ClsidDragDropHelper = new("4657278A-411B-11D2-839A-00C04FD918D0");

    [ClassInterface(ClassInterfaceType.None)]
    private sealed class DropSource : IDropSource
    {
        public int QueryContinueDrag(int escapePressed, uint keyState)
        {
            if (escapePressed != 0) return DragDropSCancel;
            // Left button released - that is the drop.
            if ((keyState & MkLeftButton) == 0) return DragDropSDrop;
            return 0; // S_OK: keep dragging.
        }

        // Lets the shell draw the drag image and standard cursors rather than
        // taking that over ourselves - the whole reason for going through the
        // shell in the first place.
        public int GiveFeedback(uint effect) => DragDropSUseDefaultCursors;
    }

    [ComImport]
    [Guid("00000121-0000-0000-C000-000000000046")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IDropSource
    {
        [PreserveSig] int QueryContinueDrag(int escapePressed, uint keyState);
        [PreserveSig] int GiveFeedback(uint effect);
    }

    [ComImport]
    [Guid("DE5BF786-477A-11D2-839D-00C04FD918D0")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IDragSourceHelper
    {
        void InitializeFromBitmap(IntPtr dragImage, ComTypes.IDataObject dataObject);
        void InitializeFromWindow(IntPtr hwnd, IntPtr point, ComTypes.IDataObject dataObject);
    }

    // Declared down to GetUIObjectOf only - every method before it still has
    // to be present, and in order, for the vtable slots to line up. Nothing
    // past it is ever called.
    [ComImport]
    [Guid("000214E6-0000-0000-C000-000000000046")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellFolder
    {
        [PreserveSig] int ParseDisplayName(IntPtr hwnd, IntPtr bindContext, [MarshalAs(UnmanagedType.LPWStr)] string displayName, out uint eaten, out IntPtr pidl, ref uint attributes);
        [PreserveSig] int EnumObjects(IntPtr hwnd, int flags, out IntPtr enumIdList);
        [PreserveSig] int BindToObject(IntPtr pidl, IntPtr bindContext, ref Guid riid, out IntPtr ppv);
        [PreserveSig] int BindToStorage(IntPtr pidl, IntPtr bindContext, ref Guid riid, out IntPtr ppv);
        [PreserveSig] int CompareIDs(IntPtr lParam, IntPtr pidl1, IntPtr pidl2);
        [PreserveSig] int CreateViewObject(IntPtr hwndOwner, ref Guid riid, out IntPtr ppv);
        [PreserveSig] int GetAttributesOf(uint count, [In, MarshalAs(UnmanagedType.LPArray)] IntPtr[] pidls, ref uint attributes);
        [PreserveSig] int GetUIObjectOf(IntPtr hwndOwner, uint count, [In, MarshalAs(UnmanagedType.LPArray)] IntPtr[] pidls, ref Guid riid, IntPtr reserved, out IntPtr ppv);
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHParseDisplayName(string name, IntPtr bindContext, out IntPtr pidl, uint attributesIn, out uint attributesOut);

    [DllImport("shell32.dll")]
    private static extern int SHBindToParent(IntPtr pidl, ref Guid riid, out IntPtr ppv, out IntPtr pidlLast);

    [DllImport("ole32.dll")]
    private static extern int DoDragDrop(ComTypes.IDataObject dataObject, IDropSource dropSource, uint allowedEffects, out uint effect);

    [DllImport("ole32.dll")]
    private static extern void CoTaskMemFree(IntPtr pointer);
}
