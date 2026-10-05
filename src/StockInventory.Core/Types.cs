namespace StockInventory.Core;

public enum Market { Twse = 1, Tpex = 2 }
public enum InstrumentKind { Stock = 1, Etf = 2 }
public enum PriceSource { Trade = 1, MidQuote = 2, PrevClose = 3 }
public enum QuoteStatus { Live, Delayed, Reference, PrevClose, Missing }
public enum MarketState { Open, Closed, Holiday }

public sealed record HoldingInput(
    int HoldingId, int PortfolioId, string Symbol, string Name,
    Market Market, InstrumentKind Kind, long Shares, long TotalCost);

public sealed record QuoteInput(
    decimal? LastPrice, decimal? PrevClose, PriceSource PriceSource, DateTime FetchedAtUtc);

public sealed record CalcOptions(
    bool Merge, bool DeductFees, decimal FeeRate, decimal FeeDiscount, decimal MinFee,
    decimal StockTaxRate, decimal EtfTaxRate, int StaleSeconds,
    MarketState MarketState, DateTime NowUtc);

public sealed record SourceRow(int PortfolioId, int HoldingId, long Shares, long TotalCost);

/// <summary>已捨入的顯示值(§6.3)。</summary>
public sealed record ViewRow(
    string Symbol, string Name, Market Market, InstrumentKind Kind,
    long Shares, long TotalCost, decimal AvgCost,
    decimal? LastPrice, decimal? PrevClose, decimal? Change, decimal? ChangePct,
    PriceSource? PriceSource, QuoteStatus QuoteStatus,
    long? MarketValue, long? UnrealizedPnl, decimal? ReturnRatePct, long? TodayPnl,
    long? EstFee, long? EstTax,
    IReadOnlyList<SourceRow> Sources,
    int? PortfolioId = null, int? HoldingId = null);

public sealed record ViewSummary(
    long MarketValue, long TotalCost, long UnrealizedPnl,
    decimal? ReturnRatePct, long TodayPnl, int MissingQuoteCount);

public sealed record ViewResult(ViewSummary Summary, IReadOnlyList<ViewRow> Rows);
