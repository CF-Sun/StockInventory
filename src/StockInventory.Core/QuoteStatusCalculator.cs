namespace StockInventory.Core;

public static class QuoteStatusCalculator
{
    public static QuoteStatus Get(QuoteInput? quote, MarketState state, DateTime nowUtc, int staleSeconds)
    {
        if (quote is null || quote.LastPrice is null) return QuoteStatus.Missing;
        var baseStatus = quote.PriceSource switch
        {
            PriceSource.Trade => QuoteStatus.Live,
            PriceSource.MidQuote => QuoteStatus.Reference,
            _ => QuoteStatus.PrevClose,
        };
        if (state == MarketState.Open
            && (nowUtc - quote.FetchedAtUtc).TotalSeconds > staleSeconds
            && baseStatus is QuoteStatus.Live or QuoteStatus.Reference)
            return QuoteStatus.Delayed;
        return baseStatus;
    }
}
