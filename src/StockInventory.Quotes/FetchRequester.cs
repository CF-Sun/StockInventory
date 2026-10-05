namespace StockInventory.Quotes;

/// <summary>新增持股後要求下一輪優先抓該代號(§7.3)。P4 由抓取服務實作。</summary>
public interface IFetchRequester
{
    void RequestImmediateFetch(string symbol);
}

public sealed class NullFetchRequester : IFetchRequester
{
    public void RequestImmediateFetch(string symbol) { }
}
