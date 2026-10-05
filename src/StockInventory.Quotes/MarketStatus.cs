using StockInventory.Core;

namespace StockInventory.Quotes;

public sealed record MarketStatusSnapshot(MarketState State, bool Stale, DateTime? LastFetchedAtUtc);

/// <summary>報價服務目前的整體狀態(§7.4 全域旗標)。P4 由抓取服務實作。</summary>
public interface IMarketStatusSource
{
    MarketStatusSnapshot Get();
}

/// <summary>P4 完成前的預設實作:尚未有抓取服務,視為休市且未延遲。</summary>
public sealed class NullMarketStatusSource : IMarketStatusSource
{
    public MarketStatusSnapshot Get() => new(MarketState.Closed, false, null);
}
