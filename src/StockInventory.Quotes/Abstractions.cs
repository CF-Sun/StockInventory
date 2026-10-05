namespace StockInventory.Quotes;

/// <summary>呼叫 MIS 的最低階介面,方便測試時替換。</summary>
public interface IMisClient
{
    /// <summary>送出一批查詢,回傳原始回應文字;逾時、連線錯誤、非 2xx 一律丟出例外。</summary>
    Task<string> GetAsync(string exCh, CancellationToken ct);
}

/// <summary>目前「有連線」的 SignalR 連線數(不含已 Pause 的)。</summary>
public interface IActiveConnectionCounter
{
    int ActiveCount { get; }
}

/// <summary>抓取服務對外的事件:每輪喚醒、報價套用完成。</summary>
public interface IQuoteSubscriber
{
    /// <summary>每輪喚醒都會呼叫;statusChanged 表示 state 或 stale 旗標與上次推送不同。</summary>
    Task OnTickAsync(MarketStatusSnapshot status, bool statusChanged, CancellationToken ct);

    /// <summary>至少一批報價成功套用後呼叫。</summary>
    Task OnQuotesAppliedAsync(CancellationToken ct);
}
