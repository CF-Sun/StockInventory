using StockInventory.Core;
using Xunit;

namespace StockInventory.Core.Tests;

public class ViewCalculatorTests
{
    static readonly DateTime Now = new(2026, 10, 5, 5, 0, 0, DateTimeKind.Utc);

    static CalcOptions Opt(bool merge = true, bool fees = false) =>
        new(merge, fees, 0.001425m, 1.000m, 20m, 0.003m, 0.001m, 180, MarketState.Open, Now);

    static HoldingInput H(int id, int pf, string sym, InstrumentKind k, long shares, long cost) =>
        new(id, pf, sym, sym, Market.Twse, k, shares, cost);

    static QuoteInput Q(decimal? last, decimal? prev) => new(last, prev, PriceSource.Trade, Now);

    static IReadOnlyDictionary<string, QuoteInput> Qs(params (string, QuoteInput)[] q) =>
        q.ToDictionary(x => x.Item1, x => x.Item2);

    static ViewResult Tv01() => ViewCalculator.Build(
        [H(1, 1, "0050", InstrumentKind.Etf, 2000, 280000), H(2, 1, "2330", InstrumentKind.Stock, 1000, 520000)],
        Qs(("0050", Q(150.50m, 149m)), ("2330", Q(600m, 610m))), Opt());

    static ViewResult One(long shares, long cost, decimal? last, decimal? prev, bool fees = false,
        InstrumentKind kind = InstrumentKind.Stock) =>
        ViewCalculator.Build([H(1, 1, "X", kind, shares, cost)], Qs(("X", Q(last, prev))), Opt(true, fees));

    [Fact] // UT-01
    public void AvgCost()
    {
        Assert.Equal(33333.33m, One(3, 100000, 1, 1).Rows[0].AvgCost);
        Assert.Equal(7m, One(1, 7, 1, 1).Rows[0].AvgCost);
    }

    [Fact] // UT-02
    public void PerRow_TV01()
    {
        var r = Tv01().Rows.ToDictionary(x => x.Symbol);
        var a = r["0050"];
        Assert.Equal((140.00m, 301000L, 21000L, 7.50m, 1.50m, 1.01m, 3000L),
            (a.AvgCost, a.MarketValue, a.UnrealizedPnl, a.ReturnRatePct, a.Change, a.ChangePct, a.TodayPnl));
        var b = r["2330"];
        Assert.Equal((520.00m, 600000L, 80000L, 15.38m, -10.00m, -1.64m, -10000L),
            (b.AvgCost, b.MarketValue, b.UnrealizedPnl, b.ReturnRatePct, b.Change, b.ChangePct, b.TodayPnl));
        Assert.Equal("2330", Tv01().Rows[0].Symbol); // 市值大到小
    }

    [Fact] // UT-03
    public void Summary_TV01()
    {
        var s = Tv01().Summary;
        Assert.Equal(901000, s.MarketValue);
        Assert.Equal(800000, s.TotalCost);
        Assert.Equal(101000, s.UnrealizedPnl);
        Assert.Equal(12.63m, s.ReturnRatePct);
        Assert.Equal(-7000, s.TodayPnl);
        Assert.Equal(0, s.MissingQuoteCount);
    }

    static readonly HoldingInput[] Two =
    [
        new(11, 1, "0050", "0050", Market.Twse, InstrumentKind.Etf, 1000, 130000),
        new(25, 2, "0050", "0050", Market.Twse, InstrumentKind.Etf, 1000, 150000),
    ];
    static readonly IReadOnlyDictionary<string, QuoteInput> Q50 = Qs(("0050", Q(150.50m, 149m)));

    [Fact] // UT-04
    public void Merge_NoFees()
    {
        var res = ViewCalculator.Build(Two, Q50, Opt(true, false));
        var r = Assert.Single(res.Rows);
        Assert.Equal((2000L, 280000L, 140.00m, 301000L, 21000L, 7.50m), (r.Shares, r.TotalCost, r.AvgCost, r.MarketValue, r.UnrealizedPnl, r.ReturnRatePct));
        Assert.Null(r.EstFee);
        Assert.Null(r.EstTax);
        Assert.Equal(2, r.Sources.Count);
    }

    [Fact] // UT-05
    public void Fees()
    {
        var m = Assert.Single(ViewCalculator.Build(Two, Q50, Opt(true, true)).Rows);
        Assert.Equal((20272L, 7.24m, 428L, 300L), (m.UnrealizedPnl, m.ReturnRatePct, m.EstFee, m.EstTax));

        var rows = ViewCalculator.Build(Two, Q50, Opt(false, true)).Rows.OrderBy(x => x.HoldingId).ToList();
        Assert.Equal((20136L, 15.49m, 214L, 150L), (rows[0].UnrealizedPnl, rows[0].ReturnRatePct, rows[0].EstFee, rows[0].EstTax));
        Assert.Equal((136L, 0.09m, 214L, 150L), (rows[1].UnrealizedPnl, rows[1].ReturnRatePct, rows[1].EstFee, rows[1].EstTax));
        Assert.Empty(rows[0].Sources);

        var min = One(100, 1000, 10m, 10m, fees: true).Rows[0]; // TV-09
        Assert.Equal((20L, 3L, -23L, -2.30m), (min.EstFee, min.EstTax, min.UnrealizedPnl, min.ReturnRatePct));
    }

    [Fact] // UT-06
    public void MissingAndNullPrev()
    {
        var res = ViewCalculator.Build([H(1, 1, "2330", InstrumentKind.Stock, 1000, 520000)], Qs(), Opt());
        var r = res.Rows[0];
        Assert.Equal(QuoteStatus.Missing, r.QuoteStatus);
        Assert.Null(r.MarketValue);
        Assert.Null(r.UnrealizedPnl);
        Assert.Equal(new ViewSummary(0, 0, 0, null, 0, 1), res.Summary);

        var n = One(100, 9000, 100m, null);
        Assert.Null(n.Rows[0].Change);
        Assert.Null(n.Rows[0].ChangePct);
        Assert.Null(n.Rows[0].TodayPnl);
        Assert.Equal(0, n.Summary.TodayPnl);
        Assert.Equal(10000, n.Rows[0].MarketValue);

        var z = One(100, 0, 10m, 10m).Rows[0]; // TV-04
        Assert.Equal((1000L, 1000L, 0L), (z.MarketValue, z.UnrealizedPnl, z.TodayPnl));
        Assert.Null(z.ReturnRatePct);
    }

    [Fact] // UT-07
    public void Rounding()
    {
        var odd = One(1, 30, 33.33m, 33.33m).Rows[0];
        Assert.Equal((33L, 3L), (odd.MarketValue, odd.UnrealizedPnl));
        var neg = One(1, 100, 99.5m, 99.5m).Rows[0];
        Assert.Equal((100L, -1L, -0.50m), (neg.MarketValue, neg.UnrealizedPnl, neg.ReturnRatePct));
    }
}
