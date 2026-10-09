using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using StockInventory.Core;
using StockInventory.Data;

namespace StockInventory.Quotes;

/// <summary>
/// §7.7 定期工作:標的主檔同步(每日 07:00 Taipei)與休市日同步(啟動時缺當年資料、每年 1 月 1 日)。
/// 補充假設(SPEC 未寫明):啟動時若 Instruments 為空也會同步一次;每日 07:00 一併重新同步當年休市日(可涵蓋臨時公告)。
/// 同步失敗只記日誌,不影響網站運作;下個週期重試。
/// </summary>
public sealed class ReferenceDataSync(IServiceScopeFactory scopes, IReferenceDataSource source, IOptions<MarketOptions> market,
    TimeProvider clock, ILogger<ReferenceDataSync> log, IntradayRetention? retention = null) : BackgroundService
{
    private readonly TimeZoneInfo _zone = TaipeiTime.Zone(market.Value.TimeZoneId);

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        try
        {
            await Safe(() => EnsureStartupDataAsync(ct));
            await PurgeIntradayAsync(ct); // 啟動時補清:網站可能在 07:00 當下沒有執行
            while (!ct.IsCancellationRequested)
            {
                await Task.Delay(UntilNextRun(), clock, ct);
                await Safe(() => SyncInstrumentsAsync(ct));
                await Safe(() => SyncHolidaysAsync(Today().Year, ct));
                await PurgeIntradayAsync(ct); // §7.7 走勢清理:在標的主檔同步之後
            }
        }
        catch (OperationCanceledException) { /* 關閉 */ }
    }

    /// <summary>走勢清理(§5.4):失敗只記日誌(清理器內部處理),不影響其他定期工作。</summary>
    private async Task PurgeIntradayAsync(CancellationToken ct)
    {
        if (retention is null) return;
        await retention.PurgeAsync(ct);
    }

    private async Task Safe(Func<Task> f)
    {
        try { await f(); }
        catch (Exception ex) when (ex is not OperationCanceledException) { log.LogWarning("參考資料同步失敗,下個週期重試 ({Type})", ex.GetType().Name); }
    }

    private DateOnly Today() => DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(clock.GetUtcNow().UtcDateTime, _zone));

    private TimeSpan UntilNextRun()
    {
        var local = TimeZoneInfo.ConvertTimeFromUtc(clock.GetUtcNow().UtcDateTime, _zone);
        var next = local.Date.AddHours(7);
        if (next <= local) next = next.AddDays(1);
        return next - local;
    }

    public async Task EnsureStartupDataAsync(CancellationToken ct)
    {
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var year = Today().Year;
        if (!await db.Instruments.AnyAsync(ct)) await Safe(() => SyncInstrumentsAsync(ct));
        var from = new DateOnly(year, 1, 1); var to = new DateOnly(year, 12, 31);
        if (!await db.MarketHolidays.AnyAsync(h => h.HolidayDate >= from && h.HolidayDate <= to, ct)) await Safe(() => SyncHolidaysAsync(year, ct));
    }

    /// <summary>以 Symbol upsert;不在最新名單者設 IsActive = 0,不刪除。任一來源失敗或結果明顯不合理時,完全不更動。</summary>
    public async Task<bool> SyncInstrumentsAsync(CancellationToken ct)
    {
        var listed = IsinParser.Parse(await source.GetIsinHtmlAsync(2, ct), Market.Twse);
        var otc = IsinParser.Parse(await source.GetIsinHtmlAsync(4, ct), Market.Tpex);

        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var existing = await db.Instruments.ToDictionaryAsync(i => i.Symbol, ct);

        // 防呆:錯誤頁或截斷的回應不能造成大量停用。新名單至少要有該市場現有有效標的的一半。
        foreach (var (mk, list) in new[] { (Market.Twse, listed), (Market.Tpex, otc) })
        {
            var active = existing.Values.Count(i => i.IsActive && i.Market == mk);
            if (list.Count == 0 || list.Count < active / 2)
            {
                log.LogError("標的主檔同步中止:{Market} 取得 {Count} 筆,現有有效 {Active} 筆,疑似來源異常", mk, list.Count, active);
                return false;
            }
        }

        var now = clock.GetUtcNow().UtcDateTime;
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var info in listed.Concat(otc))
        {
            if (!seen.Add(info.Symbol)) continue; // 同一代號出現在兩個清單時,以先出現者為準
            if (existing.TryGetValue(info.Symbol, out var row))
            {
                if (row.Name != info.Name || row.Market != info.Market || row.Kind != info.Kind || !row.IsActive)
                {
                    row.Name = info.Name; row.Market = info.Market; row.Kind = info.Kind; row.IsActive = true; row.UpdatedAtUtc = now;
                }
            }
            else
            {
                db.Instruments.Add(new Instrument { Symbol = info.Symbol, Name = info.Name, Market = info.Market, Kind = info.Kind, IsActive = true, UpdatedAtUtc = now });
            }
        }
        foreach (var row in existing.Values.Where(i => i.IsActive && !seen.Contains(i.Symbol)))
        {
            row.IsActive = false; row.UpdatedAtUtc = now;
        }
        await db.SaveChangesAsync(ct);
        log.LogInformation("標的主檔同步完成:上市 {Listed} 筆、上櫃 {Otc} 筆", listed.Count, otc.Count);
        return true;
    }

    /// <summary>以來源為準,同步指定年度的休市日;解析結果為空時不更動。</summary>
    public async Task<bool> SyncHolidaysAsync(int year, CancellationToken ct)
    {
        var parsed = HolidayParser.Parse(await source.GetHolidayJsonAsync(year, ct)).Where(h => h.Date.Year == year).ToList();
        if (parsed.Count == 0) { log.LogWarning("休市日同步:{Year} 年沒有取得資料,不更動", year); return false; }

        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var from = new DateOnly(year, 1, 1); var to = new DateOnly(year, 12, 31);
        var existing = await db.MarketHolidays.Where(h => h.HolidayDate >= from && h.HolidayDate <= to).ToDictionaryAsync(h => h.HolidayDate, ct);
        var wanted = parsed.GroupBy(h => h.Date).ToDictionary(g => g.Key, g => g.First().Description);

        foreach (var (date, desc) in wanted)
        {
            if (existing.TryGetValue(date, out var row)) row.Description = desc;
            else db.MarketHolidays.Add(new MarketHoliday { HolidayDate = date, Description = desc });
        }
        foreach (var row in existing.Values.Where(r => !wanted.ContainsKey(r.HolidayDate))) db.MarketHolidays.Remove(row);
        await db.SaveChangesAsync(ct);
        log.LogInformation("休市日同步完成:{Year} 年 {Count} 天", year, wanted.Count);
        return true;
    }
}
