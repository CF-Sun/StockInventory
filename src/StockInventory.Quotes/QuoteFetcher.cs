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

    public QuoteFetcher(IServiceScopeFactory scopes, IMisClient client, QuoteCacheStore cache, IActiveConnectionCounter conns,
        IEnumerable<IQuoteSubscriber> subs, IOptions<QuoteOptions> q, IOptions<MarketOptions> m, TimeProvider clock,
        ILogger<QuoteFetcher> log, Func<TimeSpan, CancellationToken, Task>? delay = null)
    {
        (_scopes, _client, _cache, _conns, _subs, _q, _m, _clock, _log) = (scopes, client, cache, conns, subs, q.Value, m.Value, clock, log);
        _limiter = new SlidingWindowLimiter(_q.MaxRequestsPer5s, TimeSpan.FromSeconds(5), clock, delay);
        _zone = TaipeiTime.Zone(_m.TimeZoneId);
    }

    // ---- IMarketStatusSource ----
    public MarketStatusSnapshot Get()
    {
        lock (_lock) return Snapshot(_clock.GetUtcNow().UtcDateTime);
    }

    private MarketStatusSnapshot Snapshot(DateTime nowUtc)
    {
        // market.stale = (state == Open) && (now - lastSuccessAtUtc) > 門檻;從未成功抓取過時為 false。
        // 門檻:有連線時為 StaleSeconds;無連線時抓取間隔拉長為 IdleIntervalSeconds,所以門檻為 IdleIntervalSeconds + StaleSeconds,
        // 避免正常的閒置間隔被誤判為報價中斷(SPEC §7.3/§7.4 矛盾的修正,方案 a)。
        var threshold = _conns.ActiveCount > 0 ? _q.StaleSeconds : _q.IdleIntervalSeconds + _q.StaleSeconds;
        var stale = _state == MarketState.Open && _lastSuccessUtc is { } t && (nowUtc - t).TotalSeconds > threshold;
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
        try { await LoadCacheAsync(ct); }
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
        else if (_conns.ActiveCount == 0 && !hasPriority
            && _lastRoundUtc is { } last && (nowUtc - last).TotalSeconds < _q.IdleIntervalSeconds)
            return;
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

        var anySuccess = false;
        foreach (var batch in batches)
        {
            try
            {
                await _limiter.WaitAsync(ct);
                var exCh = string.Join('|', batch.Select(s => (s.Market == Market.Twse ? "tse_" : "otc_") + s.Symbol + ".tw"));
                var parsed = MisParser.Parse(await _client.GetAsync(exCh, ct));
                await ApplyAsync(parsed, ct);
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
    }

    /// <summary>§7.2:同一個動作內更新記憶體快取並 upsert Quotes;沒有可用價格者不覆蓋。</summary>
    private async Task ApplyAsync(IReadOnlyList<ParsedQuote> parsed, CancellationToken ct)
    {
        var usable = parsed.Where(p => p.LastPrice is not null).ToList();
        if (usable.Count == 0) return;

        var now = _clock.GetUtcNow().UtcDateTime;
        using var scope = _scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var symbols = usable.Select(p => p.Symbol).ToList();
        var existing = await db.Quotes.Where(q => symbols.Contains(q.Symbol)).ToDictionaryAsync(q => q.Symbol, ct);
        var known = (await db.Instruments.AsNoTracking().Where(i => symbols.Contains(i.Symbol)).Select(i => i.Symbol).ToListAsync(ct)).ToHashSet();

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
        }
        await db.SaveChangesAsync(ct);
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
