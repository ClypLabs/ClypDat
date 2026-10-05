using ClypDat.App.Services;
using Xunit;

namespace ClypDat.App.Tests;

public sealed class TestAppDataIsolationTests
{
    [Fact]
    public void TestRunLogsOutsideTheUsersAppData()
    {
        var real = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ClypDat");
        var expected = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            AvaloniaTestThread.TestAppDataFolder, "logs");
        Assert.Equal(expected, AppLog.LogFolder);
        Assert.False(AppLog.LogFolder.StartsWith(real + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase));
    }
}
