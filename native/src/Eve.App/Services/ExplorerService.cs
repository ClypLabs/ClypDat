using System.Runtime.InteropServices;

namespace Eve.App.Services;

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
            Name = "EVE Explorer"
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
        var parseResult = SHParseDisplayName(path, IntPtr.Zero, out var itemIdList, 0, out _);
        if (parseResult >= 0)
        {
            try
            {
                var selectResult = SHOpenFolderAndSelectItems(itemIdList, 0, IntPtr.Zero, 0);
                if (selectResult >= 0) return;

                AppLog.Error($"Failed to select '{path}' in Explorer (HRESULT 0x{selectResult:X8}); opening its folder instead.");
            }
            finally
            {
                Marshal.FreeCoTaskMem(itemIdList);
            }
        }
        else
        {
            AppLog.Error($"Failed to resolve '{path}' for Explorer selection (HRESULT 0x{parseResult:X8}); opening its folder instead.");
        }

        var folder = Path.GetDirectoryName(path);
        if (!string.IsNullOrWhiteSpace(folder)) OpenFolder(folder);
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
        IntPtr childItemIdLists,
        uint flags);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr ShellExecute(
        IntPtr windowHandle,
        string operation,
        string file,
        string? parameters,
        string? directory,
        int showCommand);
}
