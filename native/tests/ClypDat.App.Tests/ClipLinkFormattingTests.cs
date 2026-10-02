using ClypDat.App.Services;
using ClypDat.App.ViewModels;
using Xunit;

namespace ClypDat.App.Tests;

public sealed class ClipLinkFormattingTests
{
    // Same short forms the website's Intl.NumberFormat("en") shows.
    [Theory]
    [InlineData(399, "aud", "A$3.99")]
    [InlineData(249, "usd", "$2.49")]
    [InlineData(4490, "nzd", "NZ$44.90")]
    [InlineData(299, "EUR", "€2.99")]
    [InlineData(1999, "pln", "PLN 19.99")]
    public void PricesReadLikeTheWebsite(long amount, string currency, string expected) =>
        Assert.Equal(expected, ClipHostingService.FormatMoney(new ClipHostingService.MoneyResponse { Amount = amount, Currency = currency }));

    [Fact]
    public void MissingPriceIsNull() => Assert.Null(ClipHostingService.FormatMoney(null));

    [Theory]
    [InlineData(50_000_000_000, "50 GB")]
    [InlineData(2_500_000_000, "2.5 GB")]
    [InlineData(312_400_000, "312 MB")]
    [InlineData(0, "0 MB")]
    public void StorageIsDecimalGigabytes(long bytes, string expected) =>
        Assert.Equal(expected, MainWindowViewModel.FormatStorage(bytes));
}
