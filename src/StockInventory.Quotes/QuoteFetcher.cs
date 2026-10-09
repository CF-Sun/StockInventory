using System.Collections.Concurrent;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using StockInventory.Core;
using StockInventory.Data;

namespace StockInventory.Quotes;

/// <summary>§7.3 抓取背景服務;同時是市場狀態來源(§7.4 全域旗標)與立即抓取請求的接收者。</summary>
public sealed class QuoteFetcher : BackgroundService, IMarketStatusSource, IFetchRequester
{
    private static readonly TimeSpan[] Backoff = [TimeSpan.FromSeconds(15), TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(60)];

    private readonly IServiceScopeFactory _scopes;
    private readonly IMisClient _client;
    private readonly QuoteCacheStore _cache;
    private readonly IActiveConnectionCounter _conns;
    private readonly IEnumerable<IQuoteSubscriber> _subs;
    private readonly QuoteOptions _q;
    private readonly MarketOptions _m;
    private readonly TimeProvider _clock;
    private readonly ILogger<QuoteFetcher> _log;
    private readonly SlidingWindowLimiter _limiter;
    private readonly TimeZoneInfo _zone;
    private readonly ConcurrentDictionary<string, byte> _priority = new();
    private readonly IIntradayRecorder? _recorder;
    private readonly int _unwatchedSeconds;

    /// <summary>抓取間隔比較時允許的誤差:喚醒時間有毫秒級抖動,不容許誤差會讓 30 秒間隔偶爾退化成 35 秒。</summary>
    private const double IntervalToleranceSeconds = 0.5;

    private readonly object _lock = new();
    private MarketState _state = MarketState.Closed;
    private DateTime? _lastSuccessUtc;
    private DateTime? _lastRoundUtc;
    private DateTime? _lastMissingRoundUtc;
    private int _failures;
    private DateTime _retryAtUtc = DateTime.MinValue;
    private HashSet<DateOnly> _holidays = [];
    private DateTime _holidaysLoadedUtc = DateTime.MinValue;
    private bool _hasPushedStatus;
    private (MarketState, bool) _lastPushed;

    // 盤中抓價統計(每 StatsLogMinutes 分鐘記一則,供確認「沒人看也約每 30 秒抓價」,不含任何持股資訊)
    private const int StatsLogMinutes = 10;
    private DateTime _statsSinceUtc;
    private int _statsActiveRounds, _statsUnwatchedRounds, _statsFailedRounds;

    public QuoteFetcher(IServiceScopeFactory scopes, IMisClient client, QuoteCacheStore cache, IActiveConnectionCounter conns,
        IEnumerable<IQuoteSubscriber> subs, IOptions<QuoteOptions> q, IOptions<MarketOptions> m, TimeProvider clock,
        ILogger<QuoteFetcher> log, Func<TimeSpan, CancellationToken, Task>? delay = null, IIntradayRecorder? recorder = null)
    {
        (_scopes, _client, _cache, _conns, _subs, _q, _m, _clock, _log) = (scopes, client, cache, conns, subs, q.Value, m.Value, clock, log);
        _recorder = recorder;
        _unwatchedSeconds = ResolveUnwatchedSeconds(_q, log);
        _limiter = new SlidingWindowLimiter(_q.MaxRequestsPer5s, TimeSpan.FromSeconds(5), clock, delay);
        _zone = TaipeiTime.Zone(_m.TimeZoneId);
    }

    /// <summary>Quote:UnwatchedIntervalSeconds 須在 ActiveIntervalSeconds 至 300 之間;超出範圍回報設定錯誤並使用預設(30,並夾在合法範圍內)。</summary>
    private static int ResolveUnwatchedSeconds(QuoteOptions q, ILogger log)
    {
        var min = q.ActiveIntervalSeconds;
        var max = QuoteOptions.MaxUnwatchedIntervalSeconds;
        if (q.UnwatchedIntervalSeconds >= min && q.UnwatchedIntervalSeconds <= max) return q.UnwatchedIntervalSeconds;
        var fallback = Math.Clamp(QuoteOptions.DefaultUnwatchedIntervalSeconds, min, max);
        log.LogError("設定錯誤:Quote:UnwatchedIntervalSeconds = {Value} 超出範圍 {Min} 至 {Max},改用預設 {Fallback}",
            q.UnwatchedIntervalSeconds, min, max, fallback);
        return fallback;
    }

    // ---- IMarketStatusSource ----
    public MarketStatusSnapshot Get()
    {
        lock (_lock) return Snapshot(_clock.GetUtcNow().UtcDateTime);
    }

    private MarketStatusSnapshot Snapshot(DateTime nowUtc)
    {
        // market.stale = (state == Open) && (now - lastSuccessAtUtc) > 門檻;從未成功抓取過時為 false。
        // 門檻(v1.5,SPEC §7.4):有連線時為 StaleSeconds;無連線時盤中抓取間隔為 UnwatchedIntervalSeconds,
        // 所以門檻為 UnwatchedIntervalSeconds + StaleSeconds,避免正常的沒人看間隔被誤判為報價中斷。
        // IdleIntervalSeconds 只用於非開盤補抓的節流,不參與門檻。
        var threshold = StaleRule.ThresholdSeconds(_conns.ActiveCount > 0, _unwatchedSeconds, _q.StaleSeconds);
        var stale = StaleRule.IsStale(_state, _lastSuccessUtc, nowUtc, threshold);
        return new MarketStatusSnapshot(_state, stale, _lastSuccessUtc);
    }

    // ---- IFetchRequester ----
    public void RequestImmediateFetch(string symbol) => _priority[symbol] = 0;

    // ---- 啟動:從 Quotes 載入快取,讓第一個畫面立即有價 ----
    public async Task LoadCacheAsync(CancellationToken ct)
    {
        using var scope = _scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var rows = await db.Quotes.AsNoTracking().ToListAsync(ct);
        _cache.Load(rows.Select(r => KeyValuePair.Create(r.Symbol,
            new QuoteInput(r.LastPrice, r.PrevClose, r.PriceSource, DateTime.SpecifyKind(r.FetchedAtUtc, DateTimeKind.Utc)))));
    }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        _log.LogInformation("報價抓取服務啟動(每 {Active} 秒喚醒;批次 {Batch} 檔;開盤 {Open}~{Close})",
            _q.ActiveIntervalSeconds, _q.BatchSize, _m.OpenTime, _m.CloseTime);
        try
        {
            await LoadCacheAsync(ct);
            _log.LogInformation("已從資料庫載入 {Count} 檔報價到快取", _cache.Snapshot().Count);
        }
        catch (Exception ex) { _log.LogWarning("啟動載入報價快取失敗 ({Type})", ex.GetType().Name); }

        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(_q.ActiveIntervalSeconds), _clock);
        while (!ct.IsCancellationRequested)
        {
            try { await TickAsync(ct); }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
            catch (Exception ex) { _log.LogError("報價抓取輪次發生未預期錯誤 ({Type})", ex.GetType().Name); }

            try { if (!await timer.WaitForNextTickAsync(ct)) break; }
            catch (OperationCanceledException) { break; }
        }
    }

    /// <summary>一次喚醒要做的事(§7.3)。公開以便測試。</summary>
    public async Task TickAsync(CancellationToken ct)
    {
        var nowUtc = _clock.GetUtcNow().UtcDateTime;
        await RefreshHolidaysAsync(nowUtc, ct);
        var local = TimeZoneInfo.ConvertTimeFromUtc(nowUtc, _zone);
        var state = MarketCalendar.GetState(local, _holidays, _m.OpenTime, _m.CloseTime);

        MarketStatusSnapshot snap;
        bool changed;
        lock (_lock)
        {
            if (_state != state && _hasPushedStatus) _log.LogInformation("市場狀態改變:{From} → {To}", _state, state);
            if (!_hasPushedStatus) _log.LogInformation("目前市場狀態:{State}(台北時間 {Local:yyyy-MM-dd HH:mm:ss})", state, local);
            _state = state;
            snap = Snapshot(nowUtc);
            changed = !_hasPushedStatus || _lastPushed != (snap.State, snap.Stale);
        }

        // 開盤時段:正常抓取。非開盤:只補抓快取裡還沒有價格的標的(例如剛新增的持股),
        // 否則新增的持股要等到下一個開盤日才有價格(MIS 收盤後仍會回傳收盤價,實測)。
        await FetchRoundAsync(nowUtc, ct, onlyMissing: state != MarketState.Open);

        // 抓完後重算旗標,狀態或旗標改變時推送
        lock (_lock)
        {
            snap = Snapshot(_clock.GetUtcNow().UtcDateTime);
            changed = !_hasPushedStatus || _lastPushed != (snap.State, snap.Stale);
            _hasPushedStatus = true;
            _lastPushed = (snap.State, snap.Stale);
        }
        foreach (var s in _subs)
            await Safe(() => s.OnTickAsync(snap, changed, ct));
    }

    private async Task FetchRoundAsync(DateTime nowUtc, CancellationToken ct, bool onlyMissing = false)
    {
        var hasPriority = !_priority.IsEmpty;
        if (onlyMissing)
        {
            // 非開盤補抓:除非有新增持股的優先請求,否則最多每 IdleIntervalSeconds 試一次,避免抓不到價的標的整晚重試
            if (!hasPriority && _lastMissingRoundUtc is { } lm && (nowUtc - lm).TotalSeconds < _q.IdleIntervalSeconds) return;
        }
        else if (!hasPriority && _lastRoundUtc is { } last)
        {
            // v1.5(§7.3):盤中沒人看也持續抓價。有連線 ActiveIntervalSeconds(5 秒),沒有連線 UnwatchedIntervalSeconds(30 秒)
            var interval = _conns.ActiveCount > 0 ? _q.ActiveIntervalSeconds : _unwatchedSeconds;
            if ((nowUtc - last).TotalSeconds < interval - IntervalToleranceSeconds) return;
        }
        if (nowUtc < _retryAtUtc) return; // 失敗退避期

        if (onlyMissing) _lastMissingRoundUtc = nowUtc; else _lastRoundUtc = nowUtc;
        var priority = _priority.Keys.ToList();
        foreach (var k in priority) _priority.TryRemove(k, out _);

        List<(string Symbol, Market Market)> symbols;
        using (var scope = _scopes.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            symbols = (await db.Holdings.AsNoTracking().Select(h => new { h.Symbol, h.Instrument!.Market }).Distinct().ToListAsync(ct))
                .Select(x => (x.Symbol, x.Market)).ToList();
        }
        if (onlyMissing)
        {
            var cached = _cache.Snapshot();
            symbols = symbols.Where(x => !cached.ContainsKey(x.Symbol)).ToList();
        }
        if (symbols.Count == 0) return;

        // 新增持股的代號優先
        var ordered = symbols.OrderBy(s => priority.Contains(s.Symbol) ? 0 : 1).ThenBy(s => s.Symbol, StringComparer.Ordinal).ToList();
        var batches = ordered.Chunk(_q.BatchSize).ToList();

        var watched = _conns.ActiveCount > 0;
        var anySuccess = false;
        foreach (var batch in batches)
        {
            try
            {
                await _limiter.WaitAsync(ct);
                var exCh = string.Join('|', batch.Select(s => (s.Market == Market.Twse ? "tse_" : "otc_") + s.Symbol + ".tw"));
                var parsed = MisParser.Parse(await _client.GetAsync(exCh, ct));
                await ApplyAsync(parsed, batch.Select(b => b.Symbol).ToHashSet(StringComparer.Ordinal), ct);
                anySuccess = true;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex)
            {
                _log.LogWarning("報價批次抓取失敗 ({Type})", ex.GetType().Name); // 不記錄回應內容與持股資料
            }
        }

        if (anySuccess)
        {
            if (_failures > 0 || _lastSuccessUtc is null || onlyMissing)
                _log.LogInformation("報價抓取成功({Mode},{Count} 檔,快取共 {Cached} 檔)", onlyMissing ? "補抓缺價" : "盤中", symbols.Count, _cache.Snapshot().Count);
            lock (_lock) { _lastSuccessUtc = _clock.GetUtcNow().UtcDateTime; _failures = 0; _retryAtUtc = DateTime.MinValue; }
            foreach (var s in _subs) await Safe(() => s.OnQuotesAppliedAsync(ct));
        }
        else
        {
            lock (_lock)
            {
                _failures++;
                if (_failures >= 3)
                    _retryAtUtc = _clock.GetUtcNow().UtcDateTime + Backoff[Math.Min(_failures - 3, Backoff.Length - 1)];
            }
            _log.LogError("本輪所有報價批次皆失敗,連續失敗 {Failures} 次", _failures);
        }
        if (!onlyMissing) LogRoundStats(nowUtc, watched, anySuccess);
    }

    /// <summary>盤中每輪累計;每 10 分鐘記一則統計(有人看與沒人看各幾輪、失敗幾輪)。只記次數。</summary>
    private void LogRoundStats(DateTime nowUtc, bool watched, bool success)
    {
        int active, unwatched, failed;
        lock (_lock)
        {
            if (_statsSinceUtc == default) _statsSinceUtc = nowUtc;
            if (watched) _statsActiveRounds++; else _statsUnwatchedRounds++;
            if (!success) _statsFailedRounds++;
            if ((nowUtc - _statsSinceUtc).TotalMinutes < StatsLogMinutes) return;
            (active, unwatched, failed) = (_statsActiveRounds, _statsUnwatchedRounds, _statsFailedRounds);
            _statsSinceUtc = nowUtc;
            _statsActiveRounds = _statsUnwatchedRounds = _statsFailedRounds = 0;
        }
        _log.LogInformation("盤中抓價統計:近 {Minutes} 分鐘共 {Rounds} 輪(有人看 {Active}、沒人看 {Unwatched}),失敗 {Failed} 輪",
            StatsLogMinutes, active + unwatched, active, unwatched, failed);
    }

    /// <summary>§7.2:同一個動作內更新記憶體快取並 upsert Quotes;沒有可用價格者不覆蓋。</summary>
    private async Task ApplyAsync(IReadOnlyList<ParsedQuote> parsed, IReadOnlySet<string> requested, CancellationToken ct)
    {
        var usable = parsed.Where(p => p.LastPrice is not null).ToList();
        if (usable.Count == 0) return;

        var now = _clock.GetUtcNow().UtcDateTime;
        using var scope = _scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var symbols = usable.Select(p => p.Symbol).ToList();
        var existing = await db.Quotes.Where(q => symbols.Contains(q.Symbol)).ToDictionaryAsync(q => q.Symbol, ct);
        var known = (await db.Instruments.AsNoTracking().Where(i => symbols.Contains(i.Symbol)).Select(i => i.Symbol).ToListAsync(ct)).ToHashSet();

        var applied = new List<ParsedQuote>(usable.Count);
        foreach (var p in usable)
        {
            if (!known.Contains(p.Symbol)) continue; // 回應帶了我們沒有的代號:略過
            var tradeDate = DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(p.QuoteTimeUtc ?? now, _zone));
            if (!existing.TryGetValue(p.Symbol, out var row))
            {
                row = new QuoteRow { Symbol = p.Symbol };
                db.Quotes.Add(row);
            }
            row.LastPrice = p.LastPrice;
            row.PrevClose = p.PrevClose;
            row.PriceSource = p.Source!.Value;
            row.TradeDate = tradeDate;
            row.QuoteTimeUtc = p.QuoteTimeUtc;
            row.FetchedAtUtc = now;
            _cache.Set(p.Symbol, new QuoteInput(p.LastPrice, p.PrevClose, p.Source.Value, now));
            applied.Add(p);
        }
        await db.SaveChangesAsync(ct);
        RecordIntraday(applied, requested, now);
    }

    /// <summary>
    /// §7.3、FR-26:每筆報價套用後呼叫走勢紀錄器,不論有沒有人看。只記本輪請求的持股代號(requested);
    /// 是否記錄的條件由紀錄器判斷(§18)。任何失敗都只記日誌,絕不影響報價更新、推播或其他 API(Q14)。
    /// </summary>
    private void RecordIntraday(IReadOnlyList<ParsedQuote> applied, IReadOnlySet<string> requested, DateTime fetchedAtUtc)
    {
        if (_recorder is null) return;
        MarketState state;
        lock (_lock) state = _state;
        foreach (var p in applied)
        {
            if (!requested.Contains(p.Symbol) || p.LastPrice is not { } price || p.Source is not { } source) continue;
            try { _recorder.Record(p.Symbol, price, source, state, p.QuoteTimeUtc, fetchedAtUtc); }
            catch (Exception ex) { _log.LogWarning("走勢紀錄失敗 ({Type})", ex.GetType().Name); }
        }
    }

    private async Task RefreshHolidaysAsync(DateTime nowUtc, CancellationToken ct)
    {
        if ((nowUtc - _holidaysLoadedUtc).TotalMinutes < 60) return;
        try
        {
            using var scope = _scopes.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            _holidays = (await db.MarketHolidays.AsNoTracking().Select(h => h.HolidayDate).ToListAsync(ct)).ToHashSet();
            _holidaysLoadedUtc = nowUtc;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _log.LogWarning("載入休市日失敗 ({Type})", ex.GetType().Name);
            _holidaysLoadedUtc = nowUtc.AddMinutes(-55); // 5 分鐘後重試
        }
    }

    private async Task Safe(Func<Task> f)
    {
        try { await f(); }
        catch (Exception ex) when (ex is not OperationCanceledException) { _log.LogWarning("訂閱者處理失敗 ({Type})", ex.GetType().Name); }
    }
}
