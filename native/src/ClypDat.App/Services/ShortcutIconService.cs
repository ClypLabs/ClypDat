using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Text;
using Avalonia.Platform;
using ClypDat.Core.Settings;

namespace ClypDat.App.Services;

/// <summary>
/// Points ClypDat's Windows shortcuts at the chosen logo.
///
/// A pinned taskbar button does not draw the running window's icon: the app's
/// windows group under the pin, and the pin keeps the icon of the shortcut
/// behind it - by default the icon compiled into ClypDat.exe. So switching the
/// logo in the app changes the window and the tray, but not the button the user
/// actually looks at, until the shortcut itself says otherwise.
///
/// Runs on every launch, not just on the toggle, because every update re-runs
/// the installer silently and the installer recreates the Start menu and desktop
/// shortcuts with the exe's own icon. The icon file lives in the data folder,
/// which updates never touch, rather than beside the exe, which they replace.
/// </summary>
internal static class ShortcutIconService
{
    private const string ClassicIconName = "clypdat-classic.ico";

    /// <summary>Best effort and quiet: a shortcut that cannot be read or written
    /// is simply left as it was.</summary>
    public static void Apply(bool classic)
    {
        if (!OperatingSystem.IsWindows()) return;
        var exe = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(exe)) return;
        try
        {
            var icon = classic ? EnsureClassicIconFile() : null;
            var changed = new List<string>();
            foreach (var shortcut in CandidateShortcuts())
                if (TryPoint(shortcut, exe, icon)) changed.Add(shortcut);
            if (changed.Count == 0) return;
            // Explorer caches taskbar and shortcut icons; tell it these changed.
            foreach (var path in changed) SHChangeNotify(ShcneUpdateItem, ShcnfPathW, path, IntPtr.Zero);
            SHChangeNotify(ShcneAssocChanged, ShcnfIdList, IntPtr.Zero, IntPtr.Zero);
            AppLog.Info($"Shortcut icons now use the {(classic ? "classic" : "current")} logo ({changed.Count} updated).");
        }
        catch (Exception error)
        {
            AppLog.Error("Shortcut icons could not be updated", error);
        }
    }

    private static IEnumerable<string> CandidateShortcuts()
    {
        var roaming = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var folders = new[]
        {
            Path.Combine(roaming, "Microsoft", "Internet Explorer", "Quick Launch", "User Pinned", "TaskBar"),
            Environment.GetFolderPath(Environment.SpecialFolder.Programs),
            Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
        };
        foreach (var folder in folders)
        {
            if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder)) continue;
            IEnumerable<string> files;
            // Named for ClypDat: the installer's Start menu and desktop shortcuts
            // and the pin made from them all are. Opening every shortcut in the
            // Start menu through COM on each launch would be wasted work.
            try { files = Directory.EnumerateFiles(folder, "*ClypDat*.lnk", SearchOption.AllDirectories).ToArray(); }
            catch (Exception) { continue; }
            foreach (var file in files) yield return file;
        }
    }

    /// <summary>Points one shortcut at the icon if - and only if - it launches this
    /// exe. Returns true when it was actually rewritten.</summary>
    private static bool TryPoint(string shortcut, string exe, string? icon)
    {
        try
        {
            var link = (IShellLinkW)new ShellLink();
            var file = (IPersistFile)link;
            file.Load(shortcut, 0);
            var target = new StringBuilder(1024);
            link.GetPath(target, target.Capacity, IntPtr.Zero, 0);
            // Other installs - a test build under Temp, the dev channel - keep
            // their own icons; only shortcuts to the running exe are ours to change.
            if (!string.Equals(Path.GetFullPath(target.ToString()), Path.GetFullPath(exe), StringComparison.OrdinalIgnoreCase)) return false;

            var current = new StringBuilder(1024);
            link.GetIconLocation(current, current.Capacity, out var index);
            // The current logo is the exe's own icon, which is what an empty
            // location already means, so "current" writes an empty location.
            var wanted = icon ?? string.Empty;
            var isDefault = current.Length == 0 || string.Equals(current.ToString(), exe, StringComparison.OrdinalIgnoreCase);
            if (icon is null ? isDefault : string.Equals(current.ToString(), icon, StringComparison.OrdinalIgnoreCase) && index == 0) return false;

            link.SetIconLocation(wanted, 0);
            file.Save(shortcut, true);
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>Copies the classic tile icon into the data folder, refreshing it
    /// if a newer build ships a different one.</summary>
    private static string EnsureClassicIconFile()
    {
        var path = Path.Combine(AppDataPaths.Root, ClassicIconName);
        using var asset = AssetLoader.Open(new Uri($"avares://ClypDat/Assets/{ClassicIconName}"));
        using var buffer = new MemoryStream();
        asset.CopyTo(buffer);
        var bytes = buffer.ToArray();
        if (!File.Exists(path) || !File.ReadAllBytes(path).AsSpan().SequenceEqual(bytes))
        {
            Directory.CreateDirectory(AppDataPaths.Root);
            File.WriteAllBytes(path, bytes);
        }
        return path;
    }

    private const uint ShcneUpdateItem = 0x00002000, ShcneAssocChanged = 0x08000000;
    private const uint ShcnfIdList = 0x0000, ShcnfPathW = 0x0005;

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern void SHChangeNotify(uint eventId, uint flags, string item1, IntPtr item2);

    [DllImport("shell32.dll")]
    private static extern void SHChangeNotify(uint eventId, uint flags, IntPtr item1, IntPtr item2);

    [ComImport, Guid("00021401-0000-0000-C000-000000000046")]
    private class ShellLink;

    [ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown), Guid("000214F9-0000-0000-C000-000000000046")]
    private interface IShellLinkW
    {
        void GetPath([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder file, int maxPath, IntPtr findData, uint flags);
        void GetIDList(out IntPtr idList);
        void SetIDList(IntPtr idList);
        void GetDescription([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder name, int maxName);
        void SetDescription([MarshalAs(UnmanagedType.LPWStr)] string name);
        void GetWorkingDirectory([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder dir, int maxPath);
        void SetWorkingDirectory([MarshalAs(UnmanagedType.LPWStr)] string dir);
        void GetArguments([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder args, int maxPath);
        void SetArguments([MarshalAs(UnmanagedType.LPWStr)] string args);
        void GetHotkey(out short hotkey);
        void SetHotkey(short hotkey);
        void GetShowCmd(out int showCmd);
        void SetShowCmd(int showCmd);
        void GetIconLocation([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder iconPath, int maxPath, out int iconIndex);
        void SetIconLocation([MarshalAs(UnmanagedType.LPWStr)] string iconPath, int iconIndex);
        void SetRelativePath([MarshalAs(UnmanagedType.LPWStr)] string relativePath, uint reserved);
        void Resolve(IntPtr hwnd, uint flags);
        void SetPath([MarshalAs(UnmanagedType.LPWStr)] string file);
    }
}
