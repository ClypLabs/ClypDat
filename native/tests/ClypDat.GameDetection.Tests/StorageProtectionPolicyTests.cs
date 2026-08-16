using ClypDat.App.Services;
using ClypDat.Capture.Abstractions;
using Xunit;

namespace ClypDat.GameDetection.Tests;

public sealed class StorageProtectionPolicyTests
{
    private static readonly DateTime Epoch = DateTime.UnixEpoch;
    private static long Gb(long value) => value * 1024 * 1024 * 1024;

    [Fact]
    public void FreeSpaceBelowTenGbWarns()
    {
        var policy = new StoragePressurePolicy("library");
        Assert.Equal(ReplayStorageState.Warning, policy.ObserveFreeSpace(Gb(9), Epoch).State);
    }

    [Fact]
    public void CriticalFreeSpaceClearsAboveThreeGb()
    {
        var policy = new StoragePressurePolicy("library");
        policy.ObserveFreeSpace(Gb(1), Epoch);
        Assert.Equal(ReplayStorageState.Warning, policy.ObserveFreeSpace(Gb(4), Epoch.AddSeconds(1)).State);
        Assert.Equal(ReplayStorageState.Healthy, policy.ObserveFreeSpace(Gb(13), Epoch.AddSeconds(2)).State);
    }

    [Fact]
    public void ThreeSlowWritesWarnWithinTenSeconds()
    {
        var policy = new StoragePressurePolicy("scratch");
        policy.RecordWrite(TimeSpan.FromMilliseconds(100), Epoch);
        policy.RecordWrite(TimeSpan.FromMilliseconds(100), Epoch.AddSeconds(1));
        policy.RecordWrite(TimeSpan.FromMilliseconds(100), Epoch.AddSeconds(2));
        Assert.Equal(ReplayStorageState.Warning, policy.ObserveFreeSpace(Gb(20), Epoch.AddSeconds(2)).State);
    }

    [Fact]
    public void SaveEstimateIncludesThreeTimesTemporarySpaceAndTwoGbHeadroom()
    {
        using var storage = new StorageProtectionService();
        var estimate = storage.EstimateSave(15, TimeSpan.FromSeconds(60));
        Assert.Equal(estimate.EstimatedFinalBytes * 3, estimate.TemporaryBytes);
        Assert.Equal(estimate.TemporaryBytes + Gb(2), estimate.RequiredFreeBytes);
    }
}
