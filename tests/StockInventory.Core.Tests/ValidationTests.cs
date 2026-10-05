using StockInventory.Core;
using Xunit;

namespace StockInventory.Core.Tests;

public class ValidationTests // UT-10
{
    [Theory]
    [InlineData("", false)] [InlineData("   ", false)] [InlineData("a", true)]
    [InlineData("123456789012345678901234567890", true)] [InlineData("1234567890123456789012345678901", false)]
    public void PortfolioName(string raw, bool ok) =>
        Assert.Equal(ok, Validation.PortfolioName(Validation.NormalizePortfolioName(raw)) is null);

    [Fact] public void PortfolioName_TrimsBeforeLengthCheck() =>
        Assert.Equal("abc", Validation.NormalizePortfolioName("  abc "));

    [Theory]
    [InlineData(0, true)] [InlineData(999_999_999_999, true)] [InlineData(1000000000000, false)]
    [InlineData(-1, false)] [InlineData(1.5, false)]
    public void TotalCost(double v, bool ok) => Assert.Equal(ok, Validation.TotalCost((decimal)v) is null);

    [Fact] public void TotalCost_NullAndMessage()
    {
        Assert.NotNull(Validation.TotalCost(null));
        Assert.Equal("總成本必須是 0 到 999,999,999,999 的整數", Validation.TotalCost(-1));
    }

    [Theory]
    [InlineData(1, true)] [InlineData(1_000_000_000, true)] [InlineData(1_000_000_001, false)]
    [InlineData(0, false)] [InlineData(2.5, false)]
    public void Shares(double v, bool ok) => Assert.Equal(ok, Validation.Shares((decimal)v) is null);

    [Theory]
    [InlineData(0, true)] [InlineData(0.01, true)] [InlineData(0.0101, false)] [InlineData(-0.001, false)]
    public void FeeRate(double v, bool ok) => Assert.Equal(ok, Validation.FeeRate((decimal)v) is null);

    [Theory]
    [InlineData(0.001, true)] [InlineData(1, true)] [InlineData(0.0009, false)] [InlineData(1.001, false)]
    public void FeeDiscount(double v, bool ok) => Assert.Equal(ok, Validation.FeeDiscount((decimal)v) is null);

    [Theory] [InlineData(0, true)] [InlineData(1, true)] [InlineData(2, false)] [InlineData(-1, false)]
    public void ColorScheme(int v, bool ok) => Assert.Equal(ok, Validation.ColorScheme(v) is null);

    [Fact] public void Symbol_UppercasedAndTrimmed() => Assert.Equal("00878", Validation.NormalizeSymbol(" 00878 "));
}
