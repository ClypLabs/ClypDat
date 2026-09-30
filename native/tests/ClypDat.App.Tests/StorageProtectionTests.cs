using ClypDat.App.Services;
using ClypDat.Capture.Abstractions;
using Xunit;

namespace ClypDat.App.Tests;

public sealed class StorageProtectionTests
{
    private const long GiB = 1024L * 1024 * 1024;
    private static readonly DateTime Now = new(2026, 9, 30, 0, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void LatencyCooldownRetainsReasonUntilThirtyHealthySeconds()
    {
        var policy = new StoragePressurePolicy("library");
        policy.RecordWrite(TimeSpan.FromSeconds(1.6), Now);
        policy.RecordWrite(TimeSpan.FromSeconds(1.6), Now);
        Assert.Equal(ReplayStorageState.Critical, policy.ObserveFreeSpace(1090 * GiB, Now).State);
        var cooldown = policy.ObserveFreeSpace(1090 * GiB, Now.AddSeconds(11));
        Assert.Equal(ReplayStorageState.Critical, cooldown.State);
        Assert.False(string.IsNullOrWhiteSpace(cooldown.Reason));
        Assert.Equal(ReplayStorageState.Critical, policy.ObserveFreeSpace(1090 * GiB, Now.AddSeconds(40)).State);
        Assert.Equal(ReplayStorageState.Healthy, policy.ObserveFreeSpace(1090 * GiB, Now.AddSeconds(41)).State);
    }

    [Fact]
    public void CapacityHysteresisRetainsExplanation()
    {
        var policy = new StoragePressurePolicy("working storage");
        Assert.Equal(ReplayStorageState.Warning, policy.ObserveFreeSpace(9 * GiB, Now).State);
        var retained = policy.ObserveFreeSpace(11 * GiB, Now.AddSeconds(5));
        Assert.Equal(ReplayStorageState.Warning, retained.State);
        Assert.False(string.IsNullOrWhiteSpace(retained.Reason));
        Assert.Equal(ReplayStorageState.Healthy, policy.ObserveFreeSpace(12 * GiB, Now.AddSeconds(10)).State);
    }

    [Theory]
    [InlineData(1600)]
    [InlineData(8000)]
    public void EntireSuccessfulSavesCannotCreateDiskWritePressure(int saveMilliseconds)
    {
        using var storage = new StorageProtectionService(_ => 1090 * GiB, _ => { });
        storage.Start([( @"D:\clips", "library")]);
        for (var save = 0; save < 4; save++) {
            storage.RecordSaveDuration(TimeSpan.FromMilliseconds(saveMilliseconds));
            Assert.True(storage.CanSave([@"D:\clips", @"C:\working"], 25, TimeSpan.FromSeconds(60), out var reason), reason);
        }
        Assert.False(storage.SavesBlocked);
    }

    [Fact]
    public void ActualDestinationReportsRequiredAndAvailableSpace()
    {
        using var storage = new StorageProtectionService(path => path == @"F:\clips" ? GiB : 1090 * GiB, _ => { });
        Assert.False(storage.CanSave([@"F:\clips", @"C:\working"], 25, TimeSpan.FromSeconds(60), out var reason));
        Assert.Contains(@"F:\clips", reason);
        Assert.Contains(storage.EstimateSave(25, TimeSpan.FromSeconds(60)).RequiredFreeBytes.ToString(), reason);
        Assert.Contains(GiB.ToString(), reason);
    }

    [Fact]
    public void InaccessibleDestinationIsRejectedWithLocation()
    {
        using var storage = new StorageProtectionService(_ => 1090 * GiB, path => {
            if (path == @"F:\offline") throw new IOException("Volume offline");
        });
        Assert.False(storage.CanSave([@"F:\offline"], 25, TimeSpan.FromSeconds(60), out var reason));
        Assert.Contains(@"F:\offline", reason); Assert.Contains("Volume offline", reason);
    }

    [Fact]
    public void ConfigurationRemovesOldRootsAndUnrelatedVolumesCannotBlockReplay()
    {
        using var storage = new StorageProtectionService(path => path.StartsWith("F:", StringComparison.Ordinal) ? throw new IOException("Offline full session") : 1090 * GiB, _ => { });
        storage.Start([(@"D:\clips", "library"), (@"F:\sessions", "full-session")]);
        Assert.True(storage.CanSave([@"D:\clips", @"C:\working"], 25, TimeSpan.FromSeconds(60), out var reason), reason);
        storage.Start([(@"D:\new-clips", "library"), (@"C:\working", "working storage")]);
        Assert.DoesNotContain(@"F:\", storage.MonitoredRoots);
        Assert.Contains(@"C:\", storage.MonitoredRoots);
    }

    [Fact]
    public void RequestedWindowControlsCapacityBudget()
    {
        using var storage = new StorageProtectionService(_ => 3 * GiB, _ => { });
        Assert.True(storage.CanSave([@"D:\clips"], 100, TimeSpan.FromSeconds(10), out _));
        Assert.False(storage.CanSave([@"D:\clips"], 100, TimeSpan.FromSeconds(60), out _));
    }

    [Fact]
    public void RepeatedLatencyPressureRestartsRecoveryClock()
    {
        var policy = new StoragePressurePolicy();
        policy.RecordWrite(TimeSpan.FromSeconds(1), Now); policy.RecordWrite(TimeSpan.FromSeconds(1), Now);
        policy.ObserveFreeSpace(1090 * GiB, Now);
        policy.ObserveFreeSpace(1090 * GiB, Now.AddSeconds(11));
        policy.RecordWrite(TimeSpan.FromSeconds(1), Now.AddSeconds(20)); policy.RecordWrite(TimeSpan.FromSeconds(1), Now.AddSeconds(20));
        Assert.Equal(ReplayStorageState.Critical, policy.ObserveFreeSpace(1090 * GiB, Now.AddSeconds(20)).State);
        policy.ObserveFreeSpace(1090 * GiB, Now.AddSeconds(31));
        Assert.Equal(ReplayStorageState.Critical, policy.ObserveFreeSpace(1090 * GiB, Now.AddSeconds(60)).State);
        Assert.Equal(ReplayStorageState.Healthy, policy.ObserveFreeSpace(1090 * GiB, Now.AddSeconds(61)).State);
    }
}
