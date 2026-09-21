using System.Text;
using ClypDat.App.Services;
using Xunit;

namespace ClypDat.App.Tests;

public sealed class AppLogAppendTests
{
    // Two writers on the same log, as two ClypDat processes are, must never
    // overwrite each other: every write lands at the current end of file.
    [Fact]
    public void ConcurrentHandlesAppendInsteadOfOverwriting()
    {
        var path = Path.Combine(Path.GetTempPath(), $"ClypDat-AppLogAppendTests-{Guid.NewGuid():N}.log");
        try
        {
            using (var first = AppLog.OpenAppendOnly(path))
            using (var second = AppLog.OpenAppendOnly(path))
            {
                for (var i = 0; i < 50; i++)
                {
                    var a = Encoding.UTF8.GetBytes($"first {i}\n");
                    first.Write(a);
                    first.Flush();
                    var b = Encoding.UTF8.GetBytes($"second {i}\n");
                    second.Write(b);
                    second.Flush();
                }
            }

            var lines = File.ReadAllLines(path);
            Assert.Equal(100, lines.Length);
            for (var i = 0; i < 50; i++)
            {
                Assert.Contains($"first {i}", lines);
                Assert.Contains($"second {i}", lines);
            }
        }
        finally
        {
            File.Delete(path);
        }
    }
}
