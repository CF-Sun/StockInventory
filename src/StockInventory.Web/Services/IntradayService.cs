using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using StockInventory.Core;
using StockInventory.Data;
using StockInventory.Quotes;

namespace StockInventory.Web.Services;

public sealed record IntradayAxisDto(DateTime OpenUtc, DateTime CloseUtc);
public sealed record IntradayPointDto(DateTime T, decimal P, bool Gap);
public sealed record IntradaySeriesDto(
    string Symbol, string Name, string Status, string? TradeDate, bool IsToday, bool Incremental,
    IntradayAxisDto? Axis, decimal? PrevClose, decimal? High, decimal? Low, decimal? YMin, decimal? YMax,
    string? Trend, IReadOnlyList<IntradayPointDto> Points);
public sealed record IntradayDto(DateTime AsOf, IReadOnlyList<IntradaySeriesDto> Series);

/// <summary>API-15 的資料來源;抽成介面讓測試可模擬資料來源例外(IT-17)。</summary>
public interface IIntradayService
{
    /// <returns>null 表示任一代號不在使用者持股內(呼叫端回 404)。</returns>
    Task<IntradayDto?> GetAsync(Guid userId, IReadOnlyList<string> symbols, DateOnly? tradeDate, DateTime? sinceUtc, CancellationToken ct = default);
}

/// <summary>
/// API-15 的資料組裝(SA 文件 §5.4、§6.1)。計算全部委派給 <see cref="IntradayRules"/>(純函式);
/// 這裡只負責驗證持股、讀取資料並轉成 DTO。不記錄代號、使用者與持股關係。
/// </summary>
public sealed class IntradayService(AppDbContext db, IMarketStatusSource market, IntradayConfig config,
    IOptions<MarketOptions> marketOptions, TimeProvider clock) : IIntradayService
{
    /// <summary>增量回應的點數上限(整日最多 271 分鐘,實務上用不到)。</summary>
    public const int MaxIncrementalPoints = 300;

    private readonly TimeZoneInfo _zone = TaipeiTime.Zone(marketOptions.Value.TimeZoneId);

    /// <param name="symbols">已正規化(大寫、去重、格式正確)的代號,順序即回應順序。</param>
    /// <returns>null 表示任一代號不在使用者持股內(呼叫端回 404,不透露是不存在或屬於他人)。</returns>
    public async Task<IntradayDto?> GetAsync(Guid userId, IReadOnlyList<string> symbols, DateOnly? tradeDate, DateTime? sinceUtc,
        CancellationToken ct = default)
    {
        var o = config.Value;

        // SEC-08、Q13:請求代號必須是目前使用者持股的子集合
        var owned = await db.Holdings.AsNoTracking()
            .Where(h => h.Portfolio!.UserId == userId && symbols.Contains(h.Symbol))
            .Select(h => h.Symbol).Distinct().ToListAsync(ct);
        if (owned.Count != symbols.Count) return null;

        var nowUtc = clock.GetUtcNow().UtcDateTime;
        var today = DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(nowUtc, _zone));
        var state = market.Get().State;

        var names = await db.Instruments.AsNoTracking().Where(i => symbols.Contains(i.Symbol))
            .ToDictionaryAsync(i => i.Symbol, i => i.Name, ct);

        // 休市日顯示「該代號最近一筆紀錄的日期」,其他情況為今日
        var latest = new Dictionary<string, DateOnly>(StringComparer.Ordinal);
        if (state == MarketState.Holiday)
        {
            var rows = await db.QuoteIntraday.AsNoTracking().Where(x => symbols.Contains(x.Symbol))
                .GroupBy(x => x.Symbol).Select(g => new { Symbol = g.Key, Latest = g.Max(x => x.TradeDate) }).ToListAsync(ct);
            foreach (var r in rows) latest[r.Symbol] = r.Latest;
        }
        var days = symbols.ToDictionary(s => s,
            s => IntradayRules.SelectDisplayDay(state, today, latest.TryGetValue(s, out var d) ? d : null, o.RetentionDays),
            StringComparer.Ordinal);

        var dates = days.Values.Where(d => d.TradeDate is not null).Select(d => d.TradeDate!.Value).Distinct().ToList();
        var points = dates.Count == 0
            ? []
            : await db.QuoteIntraday.AsNoTracking()
                .Where(x => symbols.Contains(x.Symbol) && dates.Contains(x.TradeDate))
                .OrderBy(x => x.BucketUtc)
                .Select(x => new { x.Symbol, x.BucketUtc, x.TradeDate, x.Price }).ToListAsync(ct);
        var quotes = await db.Quotes.AsNoTracking().Where(q => symbols.Contains(q.Symbol))
            .Select(q => new { q.Symbol, q.TradeDate, q.PrevClose }).ToDictionaryAsync(q => q.Symbol, ct);

        var series = new List<IntradaySeriesDto>(symbols.Count);
        foreach (var s in symbols)
        {
            var name = names.GetValueOrDefault(s, s);
            var day = days[s];
            if (day.TradeDate is not { } date)
            {
                series.Add(new IntradaySeriesDto(s, name, "noData", null, false, false, null, null, null, null, null, null, null, []));
                continue;
            }

            var axis = new IntradayAxisDto(ToUtc(date, o.RecordStartTime), ToUtc(date, o.RecordEndTime));
            var raw = points.Where(p => p.Symbol == s && p.TradeDate == date)
                .Select(p => (TimeUtc: DateTime.SpecifyKind(p.BucketUtc, DateTimeKind.Utc), p.Price)).ToList();
            if (raw.Count == 0)
            {
                series.Add(new IntradaySeriesDto(s, name, "noData", date.ToString("yyyy-MM-dd"), day.IsToday, false, axis,
                    null, null, null, null, null, null, []));
                continue;
            }

            decimal? prevClose = quotes.TryGetValue(s, out var q) ? IntradayRules.PrevCloseFor(date, q.TradeDate, q.PrevClose) : null;
            var prices = raw.Select(r => r.Price).ToList();
            var yAxis = IntradayRules.ComputeAxis(prices, prevClose)!;
            var trend = IntradayRules.GetTrend(prices, prevClose);

            // 增量:僅當前端的 tradeDate 等於伺服器此刻判定的顯示日;否則忽略 sinceUtc 回完整資料
            var incremental = tradeDate == date && sinceUtc is not null;
            IReadOnlyList<IntradayPoint> outPoints;
            if (incremental)
            {
                var since = DateTime.SpecifyKind(sinceUtc!.Value, DateTimeKind.Utc);
                // 第一個新點與 sinceUtc(前端已有的最後一點)比較缺口;不降採樣
                outPoints = IntradayRules.MarkGaps(raw.Where(r => r.TimeUtc > since).Take(MaxIncrementalPoints).ToList(),
                    o.GapBreakSeconds, since);
            }
            else
            {
                outPoints = IntradayRules.Downsample(IntradayRules.MarkGaps(raw, o.GapBreakSeconds), o.MaxPoints);
            }

            series.Add(new IntradaySeriesDto(s, name, "ok", date.ToString("yyyy-MM-dd"), day.IsToday, incremental, axis,
                prevClose, yAxis.High, yAxis.Low, yAxis.YMin, yAxis.YMax,
                trend switch { IntradayTrend.Up => "up", IntradayTrend.Down => "down", _ => "flat" },
                outPoints.Select(p => new IntradayPointDto(p.TimeUtc, p.Price, p.Gap)).ToList()));
        }
        return new IntradayDto(nowUtc, series);
    }

    /// <summary>把顯示日的 Taipei 本地時刻換成 UTC(X 軸固定範圍 09:00 至 13:30)。</summary>
    private DateTime ToUtc(DateOnly date, TimeSpan timeOfDay) =>
        TimeZoneInfo.ConvertTimeToUtc(date.ToDateTime(TimeOnly.MinValue, DateTimeKind.Unspecified) + timeOfDay, _zone);
}

/// <summary>
/// API-15 的每使用者請求速率限制(固定視窗,每分鐘 <c>Intraday:MaxRequestsPerMinute</c> 次)。
/// 分區鍵為登入身分的 UserId;只存計數,不記錄任何請求內容。
/// </summary>
public sealed class IntradayRateLimiter(IntradayConfig config, TimeProvider clock)
{
    private readonly object _gate = new();
    private readonly Dictionary<Guid, (long Window, int Count)> _map = new();

    public bool TryAcquire(Guid userId, out int retryAfterSeconds)
    {
        var nowSeconds = clock.GetUtcNow().ToUnixTimeSeconds();
        var window = nowSeconds / 60;
        retryAfterSeconds = (int)Math.Max(1, (window + 1) * 60 - nowSeconds);
        lock (_gate)
        {
            if (_map.Count > 256)
                foreach (var k in _map.Where(kv => kv.Value.Window < window).Select(kv => kv.Key).ToList()) _map.Remove(k);

            var (w, count) = _map.GetValueOrDefault(userId);
            if (w != window) count = 0;
            if (count >= config.Value.MaxRequestsPerMinute)
            {
                _map[userId] = (window, count);
                return false;
            }
            _map[userId] = (window, count + 1);
            return true;
        }
    }
}
