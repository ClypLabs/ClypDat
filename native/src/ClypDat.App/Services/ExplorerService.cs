using System.Runtime.InteropServices;

namespace ClypDat.App.Services;

public static class ExplorerService
{
    private const uint CoInitApartmentThreaded = 0x2;
    private const int ShellExecuteSuccessThreshold = 32;
    private const int ShowNormal = 1;

    public static void Open(string path, bool selectFile)
    {
        if (string.IsNullOrWhiteSpace(path)) return;

        var shellThread = new Thread(() => OpenOnShellThread(path, selectFile))
        {
            IsBackground = true,
            Name = "ClypDat Explorer"
        };
        shellThread.SetApartmentState(ApartmentState.STA);
        shellThread.Start();
    }

    private static void OpenOnShellThread(string path, bool selectFile)
    {
        var initializationResult = CoInitializeEx(IntPtr.Zero, CoInitApartmentThreaded);
        if (initializationResult < 0)
        {
            AppLog.Error($"Failed to initialize the Windows shell for '{path}' (HRESULT 0x{initializationResult:X8}).");
            return;
        }

        try
        {
            if (selectFile)
            {
                SelectFile(path);
            }
            else
            {
                OpenFolder(path);
            }
        }
        catch (Exception error)
        {
            AppLog.Error($"Failed to open Explorer for '{path}'", error);
        }
        finally
        {
            CoUninitialize();
        }
    }

    private static void SelectFile(string path)
    {
        var folder = Path.GetDirectoryName(path);
        if (string.IsNullOrWhiteSpace(folder))
        {
            AppLog.Error($"Could not determine the parent folder for '{path}'.");
            return;
        }

        var folderParseResult = SHParseDisplayName(folder, IntPtr.Zero, out var folderItemIdList, 0, out _);
        var itemParseResult = SHParseDisplayName(path, IntPtr.Zero, out var itemIdList, 0, out _);
        if (folderParseResult >= 0 && itemParseResult >= 0)
        {
            try
            {
                var childItemIdList = ILFindLastID(itemIdList);
                var selectResult = SHOpenFolderAndSelectItems(folderItemIdList, 1, new[] { childItemIdList }, 0);
                if (selectResult >= 0) return;

                AppLog.Error($"Failed to select '{path}' in Explorer (HRESULT 0x{selectResult:X8}); opening its folder instead.");
            }
            finally
            {
                Marshal.FreeCoTaskMem(itemIdList);
                Marshal.FreeCoTaskMem(folderItemIdList);
            }
        }

        if (itemParseResult >= 0) Marshal.FreeCoTaskMem(itemIdList);
        if (folderParseResult >= 0) Marshal.FreeCoTaskMem(folderItemIdList);
        AppLog.Error($"Failed to resolve '{path}' for Explorer selection (folder HRESULT 0x{folderParseResult:X8}, item HRESULT 0x{itemParseResult:X8}); opening its folder instead.");
        OpenFolder(folder);
    }

    private static void OpenFolder(string path)
    {
        var result = ShellExecute(IntPtr.Zero, "open", path, null, null, ShowNormal).ToInt64();
        if (result <= ShellExecuteSuccessThreshold)
        {
            AppLog.Error($"Failed to open Explorer for '{path}' (ShellExecute error {result}).");
        }
    }

    [DllImport("ole32.dll")]
    private static extern int CoInitializeEx(IntPtr reserved, uint coInit);

    [DllImport("ole32.dll")]
    private static extern void CoUninitialize();

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHParseDisplayName(
        string name,
        IntPtr bindingContext,
        out IntPtr itemIdList,
        uint attributes,
        out uint attributesOut);

    [DllImport("shell32.dll")]
    private static extern int SHOpenFolderAndSelectItems(
        IntPtr folderItemIdList,
        uint childItemCount,
        [MarshalAs(UnmanagedType.LPArray, SizeParamIndex = 1)] IntPtr[] childItemIdLists,
        uint flags);

    [DllImport("shell32.dll")]
    private static extern IntPtr ILFindLastID(IntPtr itemIdList);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr ShellExecute(
        IntPtr windowHandle,
        string operation,
        string file,
        string? parameters,
        string? directory,
        int showCommand);
}
