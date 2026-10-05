using StockInventory.Core;

namespace StockInventory.Quotes;

/// <summary>目前的報價快取(key = Symbol)。P4 由抓取服務維護的記憶體快取實作。</summary>
public interface IQuoteCache
{
    IReadOnlyDictionary<string, QuoteInput> Snapshot();
}

/// <summary>P4 完成前的假報價(§14):價格取 §6.4 TV-01。</summary>
public sealed class FakeQuoteProvider(TimeProvider clock) : IQuoteCache
{
    public IReadOnlyDictionary<string, QuoteInput> Snapshot()
    {
        var now = clock.GetUtcNow().UtcDateTime;
        return new Dictionary<string, QuoteInput>
        {
            ["0050"] = new(150.50m, 149.00m, PriceSource.Trade, now),
            ["2330"] = new(600.00m, 610.00m, PriceSource.Trade, now),
        };
    }
}
