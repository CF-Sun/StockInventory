namespace StockInventory.Core;

public static class ViewCalculator
{
    private sealed record Line(
        HoldingInput H, QuoteInput? Q, bool HasPrice,
        decimal MarketValue, decimal Fee, decimal Tax, decimal Unrealized, decimal? TodayPnl);

    private sealed record Row(
        string Symbol, string Name, Market Market, InstrumentKind Kind, long Shares, long TotalCost,
        bool HasPrice, QuoteInput? Q, decimal MarketValue, decimal Fee, decimal Tax,
        decimal Unrealized, decimal? TodayPnl, IReadOnlyList<SourceRow> Sources,
        int? PortfolioId, int? HoldingId);

    public static ViewResult Build(
        IReadOnlyList<HoldingInput> holdings,
        IReadOnlyDictionary<string, QuoteInput> quotes,
        CalcOptions o)
    {
        var lines = holdings.Select(h => MakeLine(h, quotes.GetValueOrDefault(h.Symbol), o)).ToList();

        var rows = o.Merge
            ? lines.GroupBy(l => l.H.Symbol, StringComparer.Ordinal).Select(g => Merge(g.ToList())).ToList()
            : lines.Select(l => new Row(l.H.Symbol, l.H.Name, l.H.Market, l.H.Kind, l.H.Shares, l.H.TotalCost,
                l.HasPrice, l.Q, l.MarketValue, l.Fee, l.Tax, l.Unrealized, l.TodayPnl,
                Array.Empty<SourceRow>(), l.H.PortfolioId, l.H.HoldingId)).ToList();

        var priced = rows.Where(r => r.HasPrice).ToList();
        var sumMv = priced.Sum(r => r.MarketValue);
        var sumCost = priced.Sum(r => (decimal)r.TotalCost);
        var sumUnreal = priced.Sum(r => r.Unrealized);
        var sumToday = priced.Where(r => r.TodayPnl != null).Sum(r => r.TodayPnl!.Value);

        var summary = new ViewSummary(
            RoundL(sumMv), (long)sumCost, RoundL(sumUnreal),
            sumCost == 0 ? null : RoundD(sumUnreal / sumCost * 100, 2),
            RoundL(sumToday), rows.Count - priced.Count);

        var ordered = rows
            .OrderBy(r => r.HasPrice ? 0 : 1)
            .ThenByDescending(r => r.HasPrice ? r.MarketValue : 0m)
            .ThenBy(r => r.Symbol, StringComparer.Ordinal)
            .Select(r => ToView(r, o))
            .ToList();

        return new ViewResult(summary, ordered);
    }

    private static Line MakeLine(HoldingInput h, QuoteInput? q, CalcOptions o)
    {
        if (q?.LastPrice is not { } p)
            return new Line(h, q, false, 0, 0, 0, 0, null);

        var gross = p * h.Shares;
        decimal fee = 0, tax = 0;
        if (o.DeductFees)
        {
            fee = Math.Max(o.MinFee, Math.Floor(gross * o.FeeRate * o.FeeDiscount));
            tax = Math.Floor(gross * (h.Kind == InstrumentKind.Etf ? o.EtfTaxRate : o.StockTaxRate));
        }
        var unreal = gross - h.TotalCost - fee - tax;
        decimal? today = q.PrevClose is { } y ? (p - y) * h.Shares : null;
        return new Line(h, q, true, gross, fee, tax, unreal, today);
    }

    private static Row Merge(List<Line> g)
    {
        var first = g[0].H;
        var priced = g.Where(l => l.HasPrice).ToList();
        var todays = priced.Where(l => l.TodayPnl != null).ToList();
        IReadOnlyList<SourceRow> sources = g.Count >= 2
            ? g.Select(l => new SourceRow(l.H.PortfolioId, l.H.HoldingId, l.H.Shares, l.H.TotalCost)).ToList()
            : Array.Empty<SourceRow>();
        return new Row(first.Symbol, first.Name, first.Market, first.Kind,
            g.Sum(l => l.H.Shares), g.Sum(l => l.H.TotalCost),
            priced.Count > 0, g[0].Q,
            priced.Sum(l => l.MarketValue), priced.Sum(l => l.Fee), priced.Sum(l => l.Tax),
            priced.Sum(l => l.Unrealized),
            todays.Count > 0 ? todays.Sum(l => l.TodayPnl!.Value) : null,
            sources,
            g.Count == 1 ? g[0].H.PortfolioId : null, g.Count == 1 ? g[0].H.HoldingId : null);
    }

    private static ViewRow ToView(Row r, CalcOptions o)
    {
        var avg = RoundD((decimal)r.TotalCost / r.Shares, 2);
        var status = r.HasPrice
            ? QuoteStatusCalculator.Get(r.Q, o.MarketState, o.NowUtc, o.StaleSeconds)
            : QuoteStatus.Missing;
        if (!r.HasPrice)
            return new ViewRow(r.Symbol, r.Name, r.Market, r.Kind, r.Shares, r.TotalCost, avg,
                null, null, null, null, null, status, null, null, null, null, null, null,
                r.Sources, r.PortfolioId, r.HoldingId);

        var p = r.Q!.LastPrice!.Value;
        var y = r.Q.PrevClose;
        return new ViewRow(r.Symbol, r.Name, r.Market, r.Kind, r.Shares, r.TotalCost, avg,
            p, y,
            y is null ? null : p - y.Value,
            y is null or 0 ? null : RoundD((p - y.Value) / y.Value * 100, 2),
            r.Q.PriceSource, status,
            RoundL(r.MarketValue), RoundL(r.Unrealized),
            r.TotalCost == 0 ? null : RoundD(r.Unrealized / r.TotalCost * 100, 2),
            r.TodayPnl is null ? null : RoundL(r.TodayPnl.Value),
            o.DeductFees ? RoundL(r.Fee) : null, o.DeductFees ? RoundL(r.Tax) : null,
            r.Sources, r.PortfolioId, r.HoldingId);
    }

    public static decimal RoundD(decimal x, int digits) => Math.Round(x, digits, MidpointRounding.AwayFromZero);
    public static long RoundL(decimal x) => (long)Math.Round(x, 0, MidpointRounding.AwayFromZero);
}
