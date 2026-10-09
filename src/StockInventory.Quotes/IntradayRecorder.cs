using System.Diagnostics;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using StockInventory.Core;
using StockInventory.Data;

namespace StockInventory.Quotes;

/// <summary>
/// 檢查並修正後的 <c>Intraday:*</c> 設定(啟動時只記錄一次設定錯誤,不讓網站啟動失敗)。
/// 紀錄器、清理器與 API-15 共用同一份。
/// </summary>
public sealed class IntradayConfig
{
    public IntradayConfig(IOptions<IntradayOptions> options, ILogger<IntradayConfig> log)
    {
        Value = options.Value.Normalize(out var errors);
        foreach (var e in errors) log.LogError("設定錯誤:{Message}", e);
    }

    public IntradayOptions Value { get; }
}

/// <summary>抓價服務套用報價後呼叫(§7.3、§18)。實作不得丟出例外到抓價流程。</summary>
public interface IIntradayRecorder
{
    void Record(string symbol, decimal price, PriceSource source, MarketState state, DateTime? quoteTimeUtc, DateTime fetchedAtUtc);
}

/// <summary>待寫入的一個分鐘桶。</summary>
public sealed record IntradaySample(string Symbol, DateTime BucketUtc, DateOnly TradeDate, decimal Price);

/// <summary>
/// FR-26 走勢紀錄器(SA 文件 §5.3):
/// 記憶體每代號只留「當前分鐘最後一筆」,進入下一分鐘時移入待寫佇列;每 <c>Intraday:FlushSeconds</c>
/// (對齊分鐘邊界後數秒)批次 upsert 到 <c>QuoteIntraday</c>(單一交易);收盤與網站正常關閉時額外 flush 當前分鐘。
/// flush 失敗只記一則警告(同一原因 10 分鐘內不重複),佇列保留到下次重試,上限 symbols × 15 分鐘,超過丟最舊者。
/// 日誌只寫筆數與耗時,不記代號與使用者持股關係。
/// </summary>
public sealed class IntradayRecorder : BackgroundService, IIntradayRecorder
{
    public const int QueueMinutesPerSymbol = 15;
    private const int FlushOffsetSeconds = 5;
    private static readonly TimeSpan WarnThrottle = TimeSpan.FromMinutes(10);

    private readonly IServiceScopeFactory _scopes;
    private readonly IntradayOptions _o;
    private readonly TimeProvider _clock;
    private readonly ILogger<IntradayRecorder> _log;
    private readonly TimeZoneInfo _zone;

    private readonly object _lock = new();
    private readonly Dictionary<string, IntradaySample> _current = new(StringComparer.Ordinal);
    private List<IntradaySample> _pending = [];
    private readonly Dictionary<string, DateTime> _lastWarnUtc = new();

    public IntradayRecorder(IServiceScopeFactory scopes, IntradayConfig config, IOptions<MarketOptions> market,
        TimeProvider clock, ILogger<IntradayRecorder> log)
    {
        (_scopes, _o, _clock, _log) = (scopes, config.Value, clock, log);
        _zone = TaipeiTime.Zone(market.Value.TimeZoneId);
    }

    /// <summary>目前待寫佇列筆數(測試與診斷用)。</summary>
    public int PendingCount { get { lock (_lock) return _pending.Count; } }

    /// <summary>記憶體中「當前分鐘」的緩衝(測試與診斷用)。</summary>
    public IReadOnlyList<IntradaySample> BufferedSnapshot() { lock (_lock) return _current.Values.ToList(); }

    /// <summary>待寫佇列快照(測試與診斷用)。</summary>
    public IReadOnlyList<IntradaySample> PendingSnapshot() { lock (_lock) return _pending.ToList(); }

    // ---- 記錄 ----

    /// <summary>§5.3 記錄規則:全部符合才進緩衝;以「觀測時間」(fetchedAtUtc)分桶,不用 tlong。</summary>
    public void Record(string symbol, decimal price, PriceSource source, MarketState state, DateTime? quoteTimeUtc, DateTime fetchedAtUtc)
    {
        if (!_o.Enabled) return;
        try
        {
            var fetchedUtc = DateTime.SpecifyKind(fetchedAtUtc, DateTimeKind.Utc);
            var fetchedLocal = TimeZoneInfo.ConvertTimeFromUtc(fetchedUtc, _zone);
            DateTime? quoteLocal = quoteTimeUtc is { } q ? TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(q, DateTimeKind.Utc), _zone) : null;
            if (!IntradayRules.ShouldRecord(source, state, fetchedLocal, quoteLocal, _o.RecordStartTime, _o.RecordEndTime)) return;
            if (price <= 0 || string.IsNullOrWhiteSpace(symbol)) return; // CK_QuoteIntraday_Price:Price > 0

            var bucket = IntradayRules.FloorToMinute(fetchedUtc);
            var sample = new IntradaySample(symbol, bucket, DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(bucket, _zone)), price);
            lock (_lock)
            {
                if (_current.TryGetValue(symbol, out var cur))
                {
                    if (sample.BucketUtc < cur.BucketUtc) return;                 // 時鐘倒退:忽略,不覆蓋較新的分鐘
                    if (sample.BucketUtc > cur.BucketUtc) { _pending.Add(cur); EnforceCap(); } // 跨分鐘:上一分鐘移入待寫佇列
                }
                _current[symbol] = sample;                                         // 同分鐘:後到者覆蓋
            }
        }
        catch (Exception ex)
        {
            Warn(ex.GetType().Name, "走勢紀錄失敗");
        }
    }

    // ---- flush ----

    /// <summary>
    /// 把已結束的分鐘(以及 <paramref name="includeCurrent"/> 時連同當前分鐘)批次寫入資料庫。
    /// 失敗時佇列保留、回傳 0,不丟出例外(取消除外)。回傳寫入筆數。
    /// </summary>
    public async Task<int> FlushAsync(DateTime nowUtc, bool includeCurrent, CancellationToken ct)
    {
        if (!_o.Enabled) return 0;
        List<IntradaySample> batch;
        lock (_lock)
        {
            var thisMinute = IntradayRules.FloorToMinute(DateTime.SpecifyKind(nowUtc, DateTimeKind.Utc));
            foreach (var s in _current.Values.Where(v => includeCurrent || v.BucketUtc < thisMinute).ToList())
            {
                _pending.Add(s);
                _current.Remove(s.Symbol);
            }
            EnforceCap();
            batch = _pending;
            _pending = [];
        }
        if (batch.Count == 0) return 0;

        var sw = Stopwatch.StartNew();
        try
        {
            await WriteAsync(batch, ct);
            _log.LogDebug("走勢 flush 完成:{Count} 筆,{Ms} 毫秒", batch.Count, sw.ElapsedMilliseconds);
            return batch.Count;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            Requeue(batch);
            throw;
        }
        catch (Exception ex)
        {
            Requeue(batch);
            Warn(ex.GetType().Name, $"走勢 flush 失敗,保留 {batch.Count} 筆待下次重試");
            return 0;
        }
    }

    /// <summary>
    /// 邏輯 upsert:先查既有 PK,存在則更新、不存在則新增(不用 MERGE 原生 SQL,InMemory 可測);
    /// 併發造成的 PK 衝突(DbUpdateException)重試一次,第二次會查到既有列而改為更新。
    /// </summary>
    private async Task WriteAsync(IReadOnlyList<IntradaySample> batch, CancellationToken ct)
    {
        // 同一 (Symbol, BucketUtc) 重複時,後者勝
        var latest = new Dictionary<(string, DateTime), IntradaySample>();
        foreach (var s in batch) latest[(s.Symbol, s.BucketUtc)] = s;
        var symbols = latest.Values.Select(s => s.Symbol).Distinct().ToList();
        var min = latest.Values.Min(s => s.BucketUtc);
        var max = latest.Values.Max(s => s.BucketUtc);

        for (var attempt = 0; ; attempt++)
        {
            using var scope = _scopes.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var existing = await db.QuoteIntraday
                .Where(x => symbols.Contains(x.Symbol) && x.BucketUtc >= min && x.BucketUtc <= max)
                .ToDictionaryAsync(x => (x.Symbol, x.BucketUtc), ct);
            foreach (var s in latest.Values)
            {
                if (existing.TryGetValue((s.Symbol, s.BucketUtc), out var row))
                {
                    row.Price = s.Price;
                    row.TradeDate = s.TradeDate;
                }
                else
                {
                    db.QuoteIntraday.Add(new QuoteIntradayRow { Symbol = s.Symbol, BucketUtc = s.BucketUtc, TradeDate = s.TradeDate, Price = s.Price });
                }
            }
            try
            {
                await db.SaveChangesAsync(ct); // 單一交易
                return;
            }
            catch (DbUpdateException) when (attempt == 0)
            {
                // 併發寫入同一主鍵:重新查詢後再試一次
            }
        }
    }

    private void Requeue(List<IntradaySample> failed)
    {
        lock (_lock)
        {
            failed.AddRange(_pending); // 失敗的在前(較舊),flush 期間新進的在後
            _pending = failed;
            EnforceCap();
        }
    }

    /// <summary>佇列上限 = 代號數 × 15 分鐘,超過丟最舊者。呼叫端須已持有 _lock。</summary>
    private void EnforceCap()
    {
        var limit = Math.Max(1, _current.Count) * QueueMinutesPerSymbol;
        var excess = _pending.Count - limit;
        if (excess <= 0) return;
        _pending = _pending.OrderBy(s => s.BucketUtc).Skip(excess).ToList();
        Warn("QueueOverflow", $"走勢待寫佇列超過上限 {limit},已丟棄最舊 {excess} 筆");
    }

    /// <summary>同一原因 10 分鐘內只記一則警告。日誌不含代號與使用者資訊。</summary>
    private void Warn(string reason, string message)
    {
        var now = _clock.GetUtcNow().UtcDateTime;
        lock (_lock)
        {
            if (_lastWarnUtc.TryGetValue(reason, out var last) && now - last < WarnThrottle) return;
            _lastWarnUtc[reason] = now;
        }
        _log.LogWarning("{Message} ({Reason})", message, reason);
    }

    // ---- 背景排程 ----

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        if (!_o.Enabled)
        {
            _log.LogInformation("走勢功能已停用(Intraday:Enabled=false),不記錄");
            return;
        }
        _log.LogInformation("走勢紀錄器啟動(記錄 {Start}~{End},每 {Flush} 秒 flush,保留 {Days} 天)",
            _o.RecordStartTime, _o.RecordEndTime, _o.FlushSeconds, _o.RetentionDays);
        await ProbeTableAsync(ct);

        while (!ct.IsCancellationRequested)
        {
            try { await Task.Delay(UntilNextFlush(), _clock, ct); }
            catch (OperationCanceledException) { break; }

            try
            {
                var nowUtc = _clock.GetUtcNow().UtcDateTime;
                var local = TimeZoneInfo.ConvertTimeFromUtc(nowUtc, _zone);
                // 收盤(超過記錄終點分鐘)後,連當前分鐘一併寫入
                var closed = local.TimeOfDay >= _o.RecordEndTime + TimeSpan.FromMinutes(1);
                await FlushAsync(nowUtc, includeCurrent: closed, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
            catch (Exception ex) { Warn(ex.GetType().Name, "走勢紀錄器排程發生未預期錯誤"); }
        }
    }

    /// <summary>網站正常關閉:額外 flush 當前分鐘。</summary>
    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        await base.StopAsync(cancellationToken);
        if (!_o.Enabled) return;
        try { await FlushAsync(_clock.GetUtcNow().UtcDateTime, includeCurrent: true, cancellationToken); }
        catch (Exception ex) { _log.LogWarning("關閉時走勢 flush 失敗 ({Type})", ex.GetType().Name); }
    }

    /// <summary>啟動時確認資料表可讀(例如尚未執行 Migration AddQuoteIntraday),只記警告,不影響網站。</summary>
    private async Task ProbeTableAsync(CancellationToken ct)
    {
        try
        {
            using var scope = _scopes.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            await db.QuoteIntraday.AsNoTracking().AnyAsync(ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _log.LogWarning("走勢資料表無法讀取,請確認已人工執行 Migration AddQuoteIntraday ({Type})", ex.GetType().Name);
        }
    }

    /// <summary>下一次 flush 的等待時間:對齊 FlushSeconds 的邊界(60 秒即分鐘邊界)之後 5 秒。</summary>
    private TimeSpan UntilNextFlush()
    {
        var period = _o.FlushSeconds;
        var secs = _clock.GetUtcNow().ToUnixTimeMilliseconds() / 1000.0;
        var next = Math.Floor(secs / period) * period + FlushOffsetSeconds;
        if (next <= secs + 0.001) next += period;
        return TimeSpan.FromSeconds(next - secs);
    }
}

/// <summary>
/// §5.4 走勢清理:刪除 TradeDate 早於「今日(Taipei)− RetentionDays」的列,每批 5000 筆迴圈,失敗只記日誌。
/// 以 EF Core 查詢加刪除實作(InMemory 可測);每日約 5 萬列,批次次數有限。
/// </summary>
public sealed class IntradayRetention(IServiceScopeFactory scopes, IntradayConfig config, IOptions<MarketOptions> market,
    TimeProvider clock, ILogger<IntradayRetention> log)
{
    public const int BatchSize = 5000;
    private readonly TimeZoneInfo _zone = TaipeiTime.Zone(market.Value.TimeZoneId);

    /// <summary>回傳刪除的總筆數;失敗或功能停用時回傳已刪除的筆數(可能為 0),不丟出例外(取消除外)。</summary>
    public async Task<int> PurgeAsync(CancellationToken ct)
    {
        var o = config.Value;
        if (!o.Enabled) return 0;
        var deleted = 0;
        var sw = Stopwatch.StartNew();
        try
        {
            var today = DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(clock.GetUtcNow().UtcDateTime, _zone));
            var cutoff = today.AddDays(-o.RetentionDays);
            while (true)
            {
                using var scope = scopes.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
                var batch = await db.QuoteIntraday.Where(x => x.TradeDate < cutoff)
                    .OrderBy(x => x.TradeDate).Take(BatchSize).ToListAsync(ct);
                if (batch.Count == 0) break;
                db.QuoteIntraday.RemoveRange(batch);
                await db.SaveChangesAsync(ct);
                deleted += batch.Count;
                if (batch.Count < BatchSize) break;
            }
            if (deleted > 0) log.LogInformation("走勢清理完成:刪除 {Count} 筆,{Ms} 毫秒", deleted, sw.ElapsedMilliseconds);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            log.LogWarning("走勢清理失敗,已刪除 {Count} 筆,下個週期重試 ({Type})", deleted, ex.GetType().Name);
        }
        return deleted;
    }
}
