namespace StockInventory.Core;

public enum IntradayTrend { Up, Down, Flat }

/// <summary>走勢上的一點(1 分鐘桶)。Gap = 與前一點之間有超過缺口門檻的間隔(第一點為 false)。</summary>
public sealed record IntradayPoint(DateTime TimeUtc, decimal Price, bool Gap);

/// <summary>整日(降採樣前)最高、最低與 Y 軸範圍(含邊距)。</summary>
public sealed record IntradayAxis(decimal High, decimal Low, decimal YMin, decimal YMax);

/// <summary>顯示日。TradeDate 為 null 表示休市日且保留期內沒有任何紀錄。</summary>
public sealed record DisplayDay(DateOnly? TradeDate, bool IsToday);

/// <summary>
/// FR-26 當日走勢的純函式(SPEC §5.4、§18、SA 文件 §5.3 至 §6.1)。
/// 不讀時鐘、不做 I/O;時間由參數傳入,Taipei 本地時間由呼叫端換算(同 <see cref="MarketCalendar"/>)。
/// </summary>
public static class IntradayRules
{
    /// <summary>邊距比例:最高最低(與昨收)的聯集範圍上下各加 5%。</summary>
    public const decimal AxisMarginRatio = 0.05m;
    /// <summary>全部同價(範圍為 0)時,上下各加 1% 價格。</summary>
    public const decimal FlatMarginRatio = 0.01m;

    /// <summary>
    /// UT-12:是否要把這筆報價記入走勢。全部符合才記:成交價、盤中、觀測時間落在 [start, end + 59 秒]、
    /// 報價時間的 Taipei 日期等於觀測當日(null 視為不符合,防止開盤初期 MIS 殘留前一日價格)。
    /// </summary>
    /// <param name="fetchedTaipei">本系統抓到該價的時間(Taipei 本地時間)。</param>
    /// <param name="quoteTaipei">報價資料時間 tlong(Taipei 本地時間),沒有時為 null。</param>
    /// <param name="start">記錄起點,含(預設 09:00)。</param>
    /// <param name="end">記錄終點的分鐘,含整個分鐘到 :59(預設 13:30)。</param>
    public static bool ShouldRecord(PriceSource source, MarketState state, DateTime fetchedTaipei, DateTime? quoteTaipei,
        TimeSpan start, TimeSpan end)
    {
        if (source != PriceSource.Trade || state != MarketState.Open) return false;
        var t = fetchedTaipei.TimeOfDay;
        if (t < start || t >= end + TimeSpan.FromMinutes(1)) return false;
        return quoteTaipei is { } q && q.Date == fetchedTaipei.Date;
    }

    /// <summary>截到分鐘(秒與以下歸零),保留 Kind。</summary>
    public static DateTime FloorToMinute(DateTime t) => new(t.Ticks - t.Ticks % TimeSpan.TicksPerMinute, t.Kind);

    /// <summary>
    /// 依原始序列(須依時間升冪)標記缺口:與前一點的間隔「大於」<paramref name="gapBreakSeconds"/>(剛好等於不算)為 true。
    /// 第一點為 false,除非提供 <paramref name="previousUtc"/>(增量查詢:前端已有的最後一點時間)。
    /// </summary>
    public static IReadOnlyList<IntradayPoint> MarkGaps(IReadOnlyList<(DateTime TimeUtc, decimal Price)> raw,
        int gapBreakSeconds, DateTime? previousUtc = null)
    {
        var list = new List<IntradayPoint>(raw.Count);
        var prev = previousUtc;
        foreach (var (time, price) in raw)
        {
            var gap = prev is { } p && (time - p).TotalSeconds > gapBreakSeconds;
            list.Add(new IntradayPoint(time, price, gap));
            prev = time;
        }
        return list;
    }

    /// <summary>
    /// UT-13:降採樣。n ≤ maxPoints 全部回傳;否則 step = ceil(n / maxPoints),由最後一點往前每隔 step 取一點(必含最後一點)。
    /// 被略過的區間若含缺口(依降採樣前的 Gap),下一個保留點的 Gap 為 true;第一個保留點為 false。
    /// </summary>
    public static IReadOnlyList<IntradayPoint> Downsample(IReadOnlyList<IntradayPoint> points, int maxPoints)
    {
        if (maxPoints < 1) throw new ArgumentOutOfRangeException(nameof(maxPoints));
        var n = points.Count;
        if (n <= maxPoints) return points;

        var step = (n + maxPoints - 1) / maxPoints; // ceil(n / maxPoints)
        var kept = new List<int>(n / step + 1);
        for (var i = n - 1; i >= 0; i -= step) kept.Add(i);
        kept.Reverse();

        var result = new List<IntradayPoint>(kept.Count);
        for (var k = 0; k < kept.Count; k++)
        {
            var idx = kept[k];
            var gap = false;
            if (k > 0)
                for (var m = kept[k - 1] + 1; m <= idx && !gap; m++) gap = points[m].Gap;
            result.Add(points[idx] with { Gap = gap });
        }
        return result;
    }

    /// <summary>
    /// UT-14:Y 軸範圍。High/Low 取傳入價格(呼叫端須傳整日、降採樣前的價格);
    /// YMin/YMax = 最高、最低與 prevClose(若有)的聯集,上下各加 5% 範圍;範圍為 0 時各加 ±1% 價格。沒有價格回傳 null。
    /// </summary>
    public static IntradayAxis? ComputeAxis(IReadOnlyList<decimal> prices, decimal? prevClose)
    {
        if (prices.Count == 0) return null;
        var high = prices[0];
        var low = prices[0];
        foreach (var p in prices)
        {
            if (p > high) high = p;
            if (p < low) low = p;
        }
        var hi = prevClose is { } pc && pc > high ? pc : high;
        var lo = prevClose is { } pc2 && pc2 < low ? pc2 : low;
        var spread = hi - lo;
        var margin = spread == 0 ? hi * FlatMarginRatio : spread * AxisMarginRatio;
        return new IntradayAxis(high, low, lo - margin, hi + margin);
    }

    /// <summary>趨勢:最後一點與 prevClose 比較;沒有 prevClose 時與第一點比較;沒有點回傳 null。</summary>
    public static IntradayTrend? GetTrend(IReadOnlyList<decimal> prices, decimal? prevClose)
    {
        if (prices.Count == 0) return null;
        var last = prices[^1];
        var baseline = prevClose ?? prices[0];
        return last > baseline ? IntradayTrend.Up : last < baseline ? IntradayTrend.Down : IntradayTrend.Flat;
    }

    /// <summary>
    /// UT-15:顯示日(SA 文件 §5.4)。平日 Open、Closed → 今日;Holiday → 該代號最近一筆紀錄的日期
    /// (須在保留期內:≥ 今日 − retentionDays),沒有或超過保留期 → TradeDate = null。
    /// </summary>
    public static DisplayDay SelectDisplayDay(MarketState state, DateOnly todayTaipei, DateOnly? latestRecordedDate, int retentionDays)
    {
        if (state != MarketState.Holiday) return new DisplayDay(todayTaipei, true);
        if (latestRecordedDate is { } d && d >= todayTaipei.AddDays(-retentionDays))
            return new DisplayDay(d, d == todayTaipei);
        return new DisplayDay(null, false);
    }

    /// <summary>昨收參考線:僅當 Quotes.TradeDate 等於顯示日才有值,否則 null。</summary>
    public static decimal? PrevCloseFor(DateOnly displayDate, DateOnly quoteTradeDate, decimal? prevClose) =>
        quoteTradeDate == displayDate ? prevClose : null;
}

/// <summary>§7.4 market.stale 的門檻與判斷(純函式)。</summary>
public static class StaleRule
{
    /// <summary>有連線:StaleSeconds;沒有連線:UnwatchedIntervalSeconds + StaleSeconds(v1.5,預設 210 秒)。</summary>
    public static int ThresholdSeconds(bool hasActiveConnections, int unwatchedIntervalSeconds, int staleSeconds) =>
        hasActiveConnections ? staleSeconds : unwatchedIntervalSeconds + staleSeconds;

    /// <summary>stale = (state == Open) 且距上次成功抓取「大於」門檻;從未成功抓取過時為 false。</summary>
    public static bool IsStale(MarketState state, DateTime? lastSuccessUtc, DateTime nowUtc, int thresholdSeconds) =>
        state == MarketState.Open && lastSuccessUtc is { } t && (nowUtc - t).TotalSeconds > thresholdSeconds;
}
