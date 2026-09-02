using ClypDat.App.Views;
using Xunit;

namespace ClypDat.App.Tests;

public sealed class ShareDialogTests
{
    [Fact]
    public void OriginalShareSize_MatchesEditorBinaryMegabytes()
    {
        Assert.Equal("182.7 MB · 1080p90", ShareDialog.FormatOriginalResultSize(191_574_835, "1080p90"));
    }
}
