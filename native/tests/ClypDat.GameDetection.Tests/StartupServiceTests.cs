using ClypDat.App.Services;
using Xunit;

namespace ClypDat.GameDetection.Tests;

public sealed class StartupServiceTests
{
    [Fact]
    public void Enable_CreatesMissingRunKeyAndWritesQuotedMinimizedCommand()
    {
        var registry = new FakeRegistry { RunKeyExists = false };

        var result = StartupService.SetLaunchOnStartup(true, true, registry, @"C:\Program Files\ClypDat\ClypDat.exe");

        Assert.True(result.Success);
        Assert.True(registry.RunKeyExists);
        Assert.Equal(@"""C:\Program Files\ClypDat\ClypDat.exe"" --minimized", registry.Command);
    }

    [Fact]
    public void Enable_RepairsChangedExecutablePath()
    {
        var registry = new FakeRegistry { RunKeyExists = true, Command = @"""C:\Old\ClypDat.exe""" };

        var result = StartupService.SetLaunchOnStartup(true, false, registry, @"D:\New\ClypDat.exe");

        Assert.True(result.Success);
        Assert.Equal(@"""D:\New\ClypDat.exe""", registry.Command);
    }

    [Fact]
    public void Disable_RemovesExistingEntry()
    {
        var registry = new FakeRegistry { RunKeyExists = true, Command = "old" };

        var result = StartupService.SetLaunchOnStartup(false, false, registry, null);

        Assert.True(result.Success);
        Assert.Null(registry.Command);
    }

    [Fact]
    public void Enable_ReportsReadBackFailure()
    {
        var registry = new FakeRegistry { RunKeyExists = true, IgnoreWrites = true };

        var result = StartupService.SetLaunchOnStartup(true, false, registry, @"C:\ClypDat.exe");

        Assert.False(result.Success);
        Assert.Contains("read-back", result.Error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Enable_ReportsTaskManagerDisabledWithoutOverridingApproval()
    {
        var registry = new FakeRegistry { RunKeyExists = true, StartupApproved = new byte[] { 0x03, 0, 0, 0 } };

        var result = StartupService.SetLaunchOnStartup(true, false, registry, @"C:\ClypDat.exe");

        Assert.False(result.Success);
        Assert.True(result.RunEntryPresent);
        Assert.True(result.TaskManagerDisabled);
        Assert.Null(registry.WrittenApproval);
    }

    private sealed class FakeRegistry : StartupService.IStartupRegistryAdapter
    {
        public bool RunKeyExists { get; set; }
        public string? Command { get; set; }
        public bool IgnoreWrites { get; set; }
        public byte[]? StartupApproved { get; set; }
        public byte[]? WrittenApproval { get; private set; }

        public StartupService.IStartupRegistryKey? OpenRunKey(bool writable) => RunKeyExists ? new FakeKey(this) : null;
        public StartupService.IStartupRegistryKey? CreateRunKey()
        {
            RunKeyExists = true;
            return new FakeKey(this);
        }

        public byte[]? ReadStartupApproved(string valueName) => StartupApproved;

        private sealed class FakeKey(FakeRegistry owner) : StartupService.IStartupRegistryKey
        {
            public string? GetString(string valueName) => owner.Command;
            public void SetString(string valueName, string value)
            {
                if (!owner.IgnoreWrites) owner.Command = value;
            }
            public void DeleteValue(string valueName) => owner.Command = null;
            public void Dispose() { }
        }
    }
}
