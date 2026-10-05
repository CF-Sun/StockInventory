using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using StockInventory.Core;
using StockInventory.Data;
using StockInventory.Quotes;
using StockInventory.Web.Options;
using StockInventory.Quotes;

namespace StockInventory.Web.Services;

public sealed record MarketDto(string State, bool Stale, DateTime? LastFetchedAtUtc);
public sealed record SummaryDto(long MarketValue, long TotalCost, long UnrealizedPnl, decimal? ReturnRatePct, long TodayPnl, int MissingQuoteCount);
public sealed record SourceDto(int PortfolioId, int HoldingId, long Shares, long TotalCost);
public sealed record RowDto(
    string Symbol, string Name, string Market, string Kind, long Shares, long TotalCost, decimal AvgCost,
    decimal? LastPrice, decimal? PrevClose, decimal? Change, decimal? ChangePct, string? PriceSource, string QuoteStatus,
    long? MarketValue, long? UnrealizedPnl, decimal? ReturnRatePct, long? TodayPnl, long? EstFee, long? EstTax,
    IReadOnlyList<SourceDto> Sources);
public sealed record ViewDto(DateTime AsOf, MarketDto Market, SummaryDto Summary, IReadOnlyList<RowDto> Rows);

/// <summary>組出 §8.2 的 View;API-06 與之後的 SignalR 推播共用。</summary>
public sealed class ViewService(AppDbContext db, IQuoteCache quotes, IMarketStatusSource market,
    IOptions<FeesOptions> fees, IOptions<QuoteOptions> quote, TimeProvider clock)
{
    public static int[]? ParseIds(string? raw) =>
        string.IsNullOrWhiteSpace(raw) ? null
        : raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(x => int.TryParse(x, out var n) ? n : (int?)null).Where(n => n.HasValue).Select(n => n!.Value).ToArray();

    /// <param name="portfolioIds">null = 全部;不屬於自己的 Id 忽略,全部無效視為全部。</param>
    public async Task<ViewDto> BuildAsync(Guid userId, IReadOnlyCollection<int>? portfolioIds, bool merge, CancellationToken ct = default) =>
        ToDto(await BuildContextAsync(userId, portfolioIds, merge, ct));

    /// <summary>未轉成 JSON 的計算結果與脈絡(CSV 匯出需要庫存名稱與是否扣費)。</summary>
    public sealed record ViewContext(ViewResult Result, MarketStatusSnapshot Market, bool DeductFees, DateTime AsOf,
        IReadOnlyDictionary<int, string> PortfolioNames);

    public async Task<ViewContext> BuildContextAsync(Guid userId, IReadOnlyCollection<int>? portfolioIds, bool merge, CancellationToken ct = default)
    {
        var ownedRows = await db.Portfolios.AsNoTracking().Where(p => p.UserId == userId)
            .Select(p => new { p.PortfolioId, p.Name }).ToListAsync(ct);
        var owned = ownedRows.Select(p => p.PortfolioId).ToList();
        var selected = portfolioIds is null ? owned : owned.Where(portfolioIds.Contains).ToList();
        if (selected.Count == 0) selected = owned;

        var holdings = await db.Holdings.AsNoTracking()
            .Where(h => selected.Contains(h.PortfolioId))
            .Select(h => new HoldingInput(h.HoldingId, h.PortfolioId, h.Symbol, h.Instrument!.Name,
                h.Instrument.Market, h.Instrument.Kind, h.Shares, h.TotalCost))
            .ToListAsync(ct);

        var s = await db.UserSettings.AsNoTracking().FirstOrDefaultAsync(x => x.UserId == userId, ct);
        var m = market.Get();
        var now = clock.GetUtcNow().UtcDateTime;
        var o = new CalcOptions(merge, s?.DeductFees ?? false, s?.FeeRate ?? 0.001425m, s?.FeeDiscount ?? 1.000m,
            fees.Value.MinFee, fees.Value.StockTaxRate, fees.Value.EtfTaxRate, quote.Value.StaleSeconds, m.State, now);

        var r = ViewCalculator.Build(holdings, quotes.Snapshot(), o);
        return new ViewContext(r, m, o.DeductFees, now, ownedRows.ToDictionary(p => p.PortfolioId, p => p.Name));
    }

    private static ViewDto ToDto(ViewContext c)
    {
        var (r, m, now) = (c.Result, c.Market, c.AsOf);
        return new ViewDto(now,
            new MarketDto(m.State.ToString().ToLowerInvariant(), m.Stale,
                m.LastFetchedAtUtc is { } t ? DateTime.SpecifyKind(t, DateTimeKind.Utc) : null),
            new SummaryDto(r.Summary.MarketValue, r.Summary.TotalCost, r.Summary.UnrealizedPnl, r.Summary.ReturnRatePct,
                r.Summary.TodayPnl, r.Summary.MissingQuoteCount),
            r.Rows.Select(x => new RowDto(x.Symbol, x.Name, x.Market == Market.Twse ? "TWSE" : "TPEx",
                x.Kind == InstrumentKind.Etf ? "etf" : "stock", x.Shares, x.TotalCost, x.AvgCost,
                x.LastPrice, x.PrevClose, x.Change, x.ChangePct,
                x.PriceSource switch { PriceSource.Trade => "trade", PriceSource.MidQuote => "midquote", PriceSource.PrevClose => "prevclose", _ => null },
                x.QuoteStatus.ToString().ToLowerInvariant(),
                x.MarketValue, x.UnrealizedPnl, x.ReturnRatePct, x.TodayPnl, x.EstFee, x.EstTax,
                x.Sources.Select(y => new SourceDto(y.PortfolioId, y.HoldingId, y.Shares, y.TotalCost)).ToList())).ToList());
    }
}
