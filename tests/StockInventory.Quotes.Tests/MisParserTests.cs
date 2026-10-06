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

public class MisParserRealSampleTests // 真實樣本:2026-10-06 盤中由使用者實測取得
{
    static string Sample(string name) => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Samples", name));

    [Fact] // z 為 "-" 時,改用 trade.z(最近成交價),仍視為成交價
    public void Real_Etf_ZDash_UsesTradeZ_AsTradePrice()
    {
        var q = MisParser.Parse(Sample("real_tse_etf_z_dash.json")).Single();
        Assert.Equal("0050", q.Symbol);
        Assert.Equal((116.0000m, StockInventory.Core.PriceSource.Trade, 115.95m), (q.LastPrice, q.Source, q.PrevClose));
        Assert.Equal(DateTimeOffset.FromUnixTimeMilliseconds(1791250014000).UtcDateTime, q.QuoteTimeUtc);
    }

    [Fact] // 查無代號時,實測回應是帶一個 c 為空的元素(不是空陣列);必須略過
    public void Real_UnknownSymbol_ReturnsPlaceholderElement_Skipped()
    {
        Assert.Empty(MisParser.Parse(Sample("real_unknown_symbol.json")));
    }

    [Fact] // z 與 trade.z 都沒有時才退到買賣價中間價
    public void NoZ_NoTrade_FallsBackToMid_ThenPrevClose()
    {
        var body = """{"msgArray":[{"c":"X","z":"-","a":"10.10_10.20_","b":"10.00_9.90_","y":"9.5","tlong":"1791250014000"}]}""";
        var q = MisParser.Parse(body).Single();
        Assert.Equal((10.05m, StockInventory.Core.PriceSource.MidQuote), (q.LastPrice, q.Source));
    }
}

public class MisParserAfterCloseTests // 真實樣本:2026-10-06 13:46 收盤後實測
{
    [Fact] // 收盤後 z 與 trade.z 都是收盤價(13:30:00),仍為成交價;tlong 停在 13:30:00
    public void RealAfterClose_ZIsClosingPrice_Trade()
    {
        var body = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Samples", "real_tse_etf_after_close.json"));
        var q = MisParser.Parse(body).Single();
        Assert.Equal((116.5m, StockInventory.Core.PriceSource.Trade, 115.95m), (q.LastPrice, q.Source, q.PrevClose));
        Assert.Equal(DateTimeOffset.FromUnixTimeMilliseconds(1791264600000).UtcDateTime, q.QuoteTimeUtc); // 13:30:00 台北時間
    }
}
