using StockInventory.Core;
using StockInventory.Quotes;
using Xunit;

namespace StockInventory.Quotes.Tests;

public class MisParserTests // §15.2:不連網路
{
    static string Sample(string name) => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Samples", name));

    [Fact]
    public void Normal_StockAndEtf_UseTradePrice()
    {
        var r = MisParser.Parse(Sample("normal_tse.json"));
        Assert.Equal(2, r.Count);
        var tsmc = r.Single(x => x.Symbol == "2330");
        Assert.Equal((600.00m, 610.00m, PriceSource.Trade), (tsmc.LastPrice, tsmc.PrevClose, tsmc.Source));
        var etf = r.Single(x => x.Symbol == "0050");
        Assert.Equal((150.5m, 149m, PriceSource.Trade), (etf.LastPrice, etf.PrevClose, etf.Source));
        Assert.Equal(DateTimeOffset.FromUnixTimeMilliseconds(1759629000000).UtcDateTime, tsmc.QuoteTimeUtc);
    }

    [Fact]
    public void NoTrade_WithBidAsk_UsesMidPrice()
    {
        var q = MisParser.Parse(Sample("no_trade_with_quotes.json")).Single();
        Assert.Equal((600.5m, PriceSource.MidQuote, 610m), (q.LastPrice, q.Source, q.PrevClose)); // (601 + 600) / 2,第 1 檔
    }

    [Fact]
    public void OnlyPrevClose_UsesPrevClose()
    {
        var q = MisParser.Parse(Sample("only_prevclose.json")).Single();
        Assert.Equal((610m, PriceSource.PrevClose, 610m), (q.LastPrice, q.Source, q.PrevClose));
    }

    [Fact]
    public void NoUsablePrice_LastPriceNull_SoCallerDoesNotOverwrite()
    {
        var q = MisParser.Parse(Sample("no_price.json")).Single();
        Assert.Null(q.LastPrice);
        Assert.Null(q.Source);
        Assert.Null(q.QuoteTimeUtc);
    }

    [Fact]
    public void EmptyMsgArray_NoEntries_NoError() => Assert.Empty(MisParser.Parse(Sample("empty.json")));

    [Fact]
    public void Otc_Parsed()
    {
        var q = MisParser.Parse(Sample("otc.json")).Single();
        Assert.Equal(("6488", 350.5m), (q.Symbol, q.LastPrice));
    }

    [Theory]
    [InlineData("html_error.html")]
    public void HtmlErrorPage_Throws(string file) => Assert.Throws<QuoteParseException>(() => MisParser.Parse(Sample(file)));

    [Theory]
    [InlineData("")] [InlineData("not json")] [InlineData("{}")] [InlineData("[]")] [InlineData("{\"msgArray\":1}")]
    public void MalformedBodies_Throw(string body) => Assert.Throws<QuoteParseException>(() => MisParser.Parse(body));

    [Theory] // 數字解析:"-"、空、非數字、≤ 0 都視為沒有
    [InlineData("-")] [InlineData("")] [InlineData("abc")] [InlineData("0")] [InlineData("0.0000")] [InlineData("-5")]
    public void InvalidNumbers_TreatedAsMissing(string z)
    {
        var body = $"{{\"msgArray\":[{{\"c\":\"X\",\"z\":\"{z}\",\"y\":\"-\",\"a\":\"-\",\"b\":\"-\"}}]}}";
        Assert.Null(MisParser.Parse(body).Single().LastPrice);
    }

    [Fact]
    public void BidOnlyOrAskOnly_DoesNotMakeMid()
    {
        var body = "{\"msgArray\":[{\"c\":\"X\",\"z\":\"-\",\"y\":\"10\",\"a\":\"-\",\"b\":\"9.9_\"}]}";
        var q = MisParser.Parse(body).Single();
        Assert.Equal((10m, PriceSource.PrevClose), (q.LastPrice, q.Source)); // 退到昨收
    }
}
