using ClypDat.App.ViewModels;
using Xunit;

namespace ClypDat.App.Tests;

public sealed class EditorBitrateLabelTests
{
    [Fact]
    public void BitrateSuffix_ShowsAverageMbps()
    {
        // 117 MB over 60 s, like a typical 1080p60 replay clip.
        Assert.Equal(" · 15.6 Mbps", MainWindowViewModel.BitrateSuffix(117_000_000, TimeSpan.FromSeconds(60)));
    }

    [Fact]
    public void BitrateSuffix_UsesKbpsBelowOneMbps()
    {
        Assert.Equal(" · 800 kbps", MainWindowViewModel.BitrateSuffix(1_000_000, TimeSpan.FromSeconds(10)));
    }

    [Theory]
    [InlineData(0, 60)]
    [InlineData(117_000_000, 0)]
    public void BitrateSuffix_OmittedWhenUnknown(long bytes, double seconds)
    {
        Assert.Equal(string.Empty, MainWindowViewModel.BitrateSuffix(bytes, TimeSpan.FromSeconds(seconds)));
    }
}
