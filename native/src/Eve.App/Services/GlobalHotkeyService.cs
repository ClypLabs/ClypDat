using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Eve.App.Services;

public sealed class GlobalHotkeyService : IDisposable
{
    private const int WhKeyboardLl = 13;
    private const int WmKeyDown = 0x0100;
    private const int WmSysKeyDown = 0x0104;
    private const int WmKeyUp = 0x0101;
    private const int WmSysKeyUp = 0x0105;

    private readonly LowLevelKeyboardProc _proc;
    private readonly HashSet<string> _pressed = new(StringComparer.OrdinalIgnoreCase);
    private HashSet<string> _combo = HotkeyCombo.Parse("Ctrl+Shift+F9");
    private IntPtr _hook;
    private bool _fired;

    public GlobalHotkeyService()
    {
        _proc = HookCallback;
    }

    public event EventHandler? Pressed;

    public void SetHotkey(string hotkey)
    {
        _combo = HotkeyCombo.Parse(hotkey);
        _fired = false;
    }

    public void Start()
    {
        if (_hook != IntPtr.Zero || !OperatingSystem.IsWindows()) return;
        using var process = Process.GetCurrentProcess();
        using var module = process.MainModule;
        _hook = SetWindowsHookEx(WhKeyboardLl, _proc, GetModuleHandle(module?.ModuleName), 0);
        if (_hook == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error());
    }

    public void Dispose()
    {
        if (_hook != IntPtr.Zero)
        {
            UnhookWindowsHookEx(_hook);
            _hook = IntPtr.Zero;
        }
    }

    private IntPtr HookCallback(int code, IntPtr wParam, IntPtr lParam)
    {
        if (code >= 0)
        {
            var vkCode = Marshal.ReadInt32(lParam);
            var key = NormalizeVirtualKey(vkCode);
            if (!string.IsNullOrWhiteSpace(key))
            {
                var message = wParam.ToInt32();
                if (message is WmKeyDown or WmSysKeyDown)
                {
                    _pressed.Add(key);
                    if (!_fired && _combo.Count > 0 && _combo.SetEquals(_pressed))
                    {
                        _fired = true;
                        Pressed?.Invoke(this, EventArgs.Empty);
                    }
                }
                else if (message is WmKeyUp or WmSysKeyUp)
                {
                    _pressed.Remove(key);
                    _fired = false;
                }
            }
        }

        return CallNextHookEx(_hook, code, wParam, lParam);
    }

    private static string NormalizeVirtualKey(int vkCode)
    {
        return vkCode switch
        {
            0xA0 or 0xA1 or 0x10 or 0xA2 or 0xA3 or 0x11 => vkCode is 0xA0 or 0xA1 or 0x10 ? "Shift" : "Ctrl",
            0xA4 or 0xA5 or 0x12 => "Alt",
            0x5B or 0x5C => "Win",
            >= 0x30 and <= 0x39 => ((char)vkCode).ToString(),
            >= 0x41 and <= 0x5A => ((char)vkCode).ToString(),
            >= 0x70 and <= 0x87 => $"F{vkCode - 0x6F}",
            0x20 => "Space",
            0x25 => "Left",
            0x26 => "Up",
            0x27 => "Right",
            0x28 => "Down",
            _ => string.Empty
        };
    }

    private delegate IntPtr LowLevelKeyboardProc(int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetWindowsHookEx(int idHook, LowLevelKeyboardProc lpfn, IntPtr hMod, uint dwThreadId);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnhookWindowsHookEx(IntPtr hhk);

    [DllImport("user32.dll")]
    private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    private static extern IntPtr GetModuleHandle(string? lpModuleName);
}
