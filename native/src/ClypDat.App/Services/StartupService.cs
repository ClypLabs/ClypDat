using Microsoft.Win32;

namespace ClypDat.App.Services;

public sealed record StartupRegistrationResult(
    bool Success,
    string Error,
    bool RunEntryPresent,
    bool TaskManagerDisabled)
{
    public static StartupRegistrationResult Ok(bool runEntryPresent = true) => new(true, string.Empty, runEntryPresent, false);
}

public static class StartupService
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string StartupApprovedRunKey = @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run";
    private const string ValueName = "ClypDat";

    public static StartupRegistrationResult LastResult { get; private set; } = StartupRegistrationResult.Ok(false);

    public static StartupRegistrationResult SetLaunchOnStartup(bool enabled, bool minimized)
    {
        if (!OperatingSystem.IsWindows()) return Remember(StartupRegistrationResult.Ok(false));
        return SetLaunchOnStartup(enabled, minimized, new WindowsStartupRegistryAdapter(), Environment.ProcessPath);
    }

    internal static StartupRegistrationResult SetLaunchOnStartup(
        bool enabled,
        bool minimized,
        IStartupRegistryAdapter registry,
        string? executablePath)
    {
        try
        {
            if (!enabled)
            {
                using var existingKey = registry.OpenRunKey(writable: true);
                if (existingKey is null) return Remember(StartupRegistrationResult.Ok(false));
                existingKey.DeleteValue(ValueName);
                return Remember(StartupRegistrationResult.Ok(false));
            }

            if (string.IsNullOrWhiteSpace(executablePath))
                return Remember(Failure("Windows startup registration failed: executable path is unavailable."));

            using var key = registry.OpenRunKey(writable: true) ?? registry.CreateRunKey();
            if (key is null)
                return Remember(Failure("Windows startup registration failed: unable to create the per-user Run key."));

            var command = QuoteExecutablePath(executablePath) + (minimized ? " --minimized" : string.Empty);
            key.SetString(ValueName, command);
            var readBack = key.GetString(ValueName);
            if (!string.Equals(readBack, command, StringComparison.Ordinal))
                return Remember(Failure("Windows startup registration failed: Run value read-back did not match the requested command."));

            var approval = registry.ReadStartupApproved(ValueName);
            if (IsTaskManagerDisabled(approval))
                return Remember(new StartupRegistrationResult(false,
                    "Windows Startup Apps has disabled ClypDat. Re-enable it in Task Manager to start ClypDat with Windows.",
                    true, true));

            return Remember(StartupRegistrationResult.Ok(true));
        }
        catch (Exception error)
        {
            var result = Failure($"Windows startup registration failed: {error.Message}");
            AppLog.Error(result.Error, error);
            return Remember(result);
        }
    }

    internal static string QuoteExecutablePath(string executablePath) =>
        $"\"{executablePath.Replace("\"", "\\\"", StringComparison.Ordinal)}\"";

    internal static bool IsTaskManagerDisabled(byte[]? approval) =>
        approval is { Length: >= 4 } && (approval[0] == 0x03 || approval[0] == 0x07);

    private static StartupRegistrationResult Failure(string error) => new(false, error, false, false);

    private static StartupRegistrationResult Remember(StartupRegistrationResult result)
    {
        LastResult = result;
        if (!result.Success) AppLog.Info(result.Error);
        return result;
    }

    internal interface IStartupRegistryAdapter
    {
        IStartupRegistryKey? OpenRunKey(bool writable);
        IStartupRegistryKey? CreateRunKey();
        byte[]? ReadStartupApproved(string valueName);
    }

    internal interface IStartupRegistryKey : IDisposable
    {
        string? GetString(string valueName);
        void SetString(string valueName, string value);
        void DeleteValue(string valueName);
    }

    private sealed class WindowsStartupRegistryAdapter : IStartupRegistryAdapter
    {
        public IStartupRegistryKey? OpenRunKey(bool writable) => Wrap(Registry.CurrentUser.OpenSubKey(RunKey, writable));
        public IStartupRegistryKey? CreateRunKey() => Wrap(Registry.CurrentUser.CreateSubKey(RunKey, writable: true));

        public byte[]? ReadStartupApproved(string valueName)
        {
            using var key = Registry.CurrentUser.OpenSubKey(StartupApprovedRunKey, writable: false);
            return key?.GetValue(valueName) as byte[];
        }

        private static IStartupRegistryKey? Wrap(RegistryKey? key) => key is null ? null : new WindowsStartupRegistryKey(key);
    }

    private sealed class WindowsStartupRegistryKey(RegistryKey key) : IStartupRegistryKey
    {
        public string? GetString(string valueName) => key.GetValue(valueName) as string;
        public void SetString(string valueName, string value) => key.SetValue(valueName, value, RegistryValueKind.String);
        public void DeleteValue(string valueName) => key.DeleteValue(valueName, throwOnMissingValue: false);
        public void Dispose() => key.Dispose();
    }
}
