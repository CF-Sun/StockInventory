using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using StockInventory.Core;
using StockInventory.Data;
using StockInventory.Quotes;
using Xunit;

namespace StockInventory.Quotes.Tests;

/// <summary>QT-01、QT-02:走勢紀錄器的緩衝、flush、失敗重試;以及保留期清理(5000 筆分批)。</summary>
public class IntradayRecorderTests
{
    // 2026-10-05 週一;Taipei 10:00 = 02:00 UTC
    static DateTime Utc(int h, int m, int s = 0) => new(2026, 10, 5, h, m, s, DateTimeKind.Utc);
    static readonly DateTime Open10 = Utc(2, 0, 0);

    sealed class FlakyScopeFactory(IServiceScopeFactory inner) : IServiceScopeFactory
    {
        public bool Fail;
        public IServiceScope CreateScope() => Fail ? throw new InvalidOperationException("db down") : inner.CreateScope();
    }

    sealed class CaptureLogger : ILogger<IntradayRecorder>
    {
        public List<(LogLevel Level, string Message)> Entries = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            Entries.Add((logLevel, formatter(state, exception)));
    }

    sealed class Env
    {
        public FakeTimeProvider Clock = new(new DateTimeOffset(Open10));
        public ServiceProvider Sp;
        public FlakyScopeFactory Scopes;
        public IntradayRecorder Recorder;
        public CaptureLogger Log = new();

        public Env(bool enabled = true)
        {
            var db = Guid.NewGuid().ToString();
            Sp = new ServiceCollection().AddDbContext<AppDbContext>(o => o.UseInMemoryDatabase(db)).BuildServiceProvider();
            Scopes = new FlakyScopeFactory(Sp.GetRequiredService<IServiceScopeFactory>());
            var cfg = new IntradayConfig(Options.Create(new IntradayOptions { Enabled = enabled }), NullLogger<IntradayConfig>.Instance);
            Recorder = new IntradayRecorder(Scopes, cfg, Options.Create(new MarketOptions()), Clock, Log);
        }

        /// <summary>觀測時間 = fetched,報價時間預設為同一時間(今日)。</summary>
        public void Rec(string sym, decimal price, DateTime fetched, PriceSource src = PriceSource.Trade,
            MarketState st = MarketState.Open, DateTime? quote = null, bool quoteNull = false) =>
            Recorder.Record(sym, price, src, st, quoteNull ? null : quote ?? fetched, fetched);

        public async Task<List<QuoteIntradayRow>> Rows()
        {
            using var s = Sp.CreateScope();
            return await s.ServiceProvider.GetRequiredService<AppDbContext>().QuoteIntraday.AsNoTracking()
                .OrderBy(r => r.Symbol).ThenBy(r => r.BucketUtc).ToListAsync();
        }
    }

    // ---------- QT-01 ----------
    [Fact]
    public void SameMinute_KeepsLastObservation_NothingPending()
    {
        var e = new Env();
        e.Rec("2330", 600m, Utc(2, 0, 5));
        e.Rec("2330", 601m, Utc(2, 0, 25));
        e.Rec("2330", 602.5m, Utc(2, 0, 55));
        var cur = Assert.Single(e.Recorder.BufferedSnapshot());
        Assert.Equal((602.5m, Utc(2, 0, 0)), (cur.Price, cur.BucketUtc));
        Assert.Equal(0, e.Recorder.PendingCount);
    }

    [Fact] // 以「觀測時間」分桶,不是 tlong:報價時間停在 09:10 的冷門股,線尾仍隨觀測時間前進
    public void BucketsByObservationTime_NotQuoteTime()
    {
        var e = new Env();
        e.Rec("6488", 50m, Utc(2, 5, 30), quote: Utc(1, 10, 0));   // tlong 09:10,觀測 10:05:30
        var s = Assert.Single(e.Recorder.BufferedSnapshot());
        Assert.Equal(Utc(2, 5, 0), s.BucketUtc);
        Assert.Equal(new DateOnly(2026, 10, 5), s.TradeDate);      // BucketUtc 換算 Taipei 的日期
    }

    [Fact]
    public void CrossingMinute_MovesPreviousMinuteToPending()
    {
        var e = new Env();
        e.Rec("2330", 600m, Utc(2, 0, 10));
        e.Rec("2330", 601m, Utc(2, 0, 40));
        e.Rec("2330", 603m, Utc(2, 1, 5));
        var pending = Assert.Single(e.Recorder.PendingSnapshot());
        Assert.Equal((601m, Utc(2, 0, 0)), (pending.Price, pending.BucketUtc));        // 上一分鐘最後一筆
        Assert.Equal((603m, Utc(2, 1, 0)), (e.Recorder.BufferedSnapshot().Single().Price, e.Recorder.BufferedSnapshot().Single().BucketUtc));
    }

    [Fact]
    public async Task Flush_WritesCompletedMinutes_ThenClearsQueue_KeepsCurrentMinute()
    {
        var e = new Env();
        e.Rec("2330", 600m, Utc(2, 0, 10));
        e.Rec("2330", 603m, Utc(2, 1, 5));
        e.Rec("0050", 150m, Utc(2, 1, 6));
        var n = await e.Recorder.FlushAsync(Utc(2, 1, 8), includeCurrent: false, default);
        Assert.Equal(1, n);                                                              // 只有已結束的 10:00 分鐘
        var rows = await e.Rows();
        var r = Assert.Single(rows);
        Assert.Equal(("2330", 600m, Utc(2, 0, 0)), (r.Symbol, r.Price, r.BucketUtc));
        Assert.Equal(new DateOnly(2026, 10, 5), r.TradeDate);
        Assert.Equal(0, e.Recorder.PendingCount);
        Assert.Equal(2, e.Recorder.BufferedSnapshot().Count);                            // 當前分鐘仍在緩衝
    }

    [Fact] // 下一個 flush 時,已過去的分鐘(即使該代號沒有新報價)也會被寫入
    public async Task Flush_PromotesBufferedMinutesThatHavePassed()
    {
        var e = new Env();
        e.Rec("2330", 600m, Utc(2, 0, 10));
        Assert.Equal(0, await e.Recorder.FlushAsync(Utc(2, 0, 50), false, default));     // 同一分鐘:不寫
        Assert.Equal(1, await e.Recorder.FlushAsync(Utc(2, 1, 5), false, default));      // 已過一分鐘:寫入
        Assert.Single(await e.Rows());
        Assert.Empty(e.Recorder.BufferedSnapshot());
    }

    [Fact] // 收盤與關機:額外 flush 當前分鐘
    public async Task CloseAndShutdown_FlushCurrentMinute()
    {
        var e = new Env();
        e.Rec("2330", 650m, Utc(5, 30, 20));                                             // 13:30:20 Taipei,收盤最後一分鐘
        Assert.Equal(0, await e.Recorder.FlushAsync(Utc(5, 30, 40), false, default));
        Assert.Equal(1, await e.Recorder.FlushAsync(Utc(5, 30, 40), includeCurrent: true, default)); // 關機
        Assert.Equal(Utc(5, 30, 0), (await e.Rows()).Single().BucketUtc);

        e.Rec("0050", 150m, Utc(5, 30, 59));                                             // 13:30:59 仍記錄
        Assert.Equal(1, await e.Recorder.FlushAsync(Utc(5, 31, 3), false, default));     // 13:31 的排程 flush 寫入 13:30 分鐘
        Assert.Equal(2, (await e.Rows()).Count);
    }

    [Fact] // flush 同一個 (Symbol, BucketUtc) 兩次 = 覆蓋(後者勝),不重複列
    public async Task Flush_SameBucketTwice_Upserts()
    {
        var e = new Env();
        e.Rec("2330", 600m, Utc(2, 0, 10));
        await e.Recorder.FlushAsync(Utc(2, 0, 30), includeCurrent: true, default);
        e.Rec("2330", 605m, Utc(2, 0, 50));                                              // 重啟或收盤補寫:同一分鐘再寫一次
        await e.Recorder.FlushAsync(Utc(2, 1, 5), includeCurrent: false, default);
        var r = Assert.Single(await e.Rows());
        Assert.Equal(605m, r.Price);
    }

    [Fact]
    public async Task Flush_BatchesManySymbolsInOneCall()
    {
        var e = new Env();
        foreach (var sym in new[] { "2330", "0050", "6488" }) { e.Rec(sym, 10m, Utc(2, 0, 5)); e.Rec(sym, 11m, Utc(2, 1, 5)); }
        Assert.Equal(3, await e.Recorder.FlushAsync(Utc(2, 1, 10), false, default));
        Assert.Equal(3, (await e.Rows()).Count);
    }

    [Theory] // 記錄條件(UT-12 在紀錄器的整合):不符合者一律不進緩衝
    [InlineData("pz")]       // PriceSource = 2
    [InlineData("y")]        // PriceSource = 3
    [InlineData("closed")]   // MarketState.Closed
    [InlineData("holiday")]
    [InlineData("before")]   // 08:59:59 Taipei
    [InlineData("after")]    // 13:31:00 Taipei
    [InlineData("yesterday")]// 報價時間不是今日
    [InlineData("noquote")]  // 沒有報價時間
    [InlineData("zero")]     // Price <= 0
    public void NotRecorded_WhenConditionsNotMet(string kind)
    {
        var e = new Env();
        var t = Utc(2, 0, 10);
        switch (kind)
        {
            case "pz": e.Rec("2330", 10m, t, PriceSource.MidQuote); break;
            case "y": e.Rec("2330", 10m, t, PriceSource.PrevClose); break;
            case "closed": e.Rec("2330", 10m, t, st: MarketState.Closed); break;
            case "holiday": e.Rec("2330", 10m, t, st: MarketState.Holiday); break;
            case "before": e.Rec("2330", 10m, Utc(0, 59, 59)); break;
            case "after": e.Rec("2330", 10m, Utc(5, 31, 0)); break;
            case "yesterday": e.Rec("2330", 10m, Utc(1, 0, 5), quote: Utc(1, 0, 5).AddDays(-1)); break;
            case "noquote": e.Rec("2330", 10m, t, quoteNull: true); break;
            case "zero": e.Rec("2330", 0m, t); break;
        }
        Assert.Empty(e.Recorder.BufferedSnapshot());
        Assert.Equal(0, e.Recorder.PendingCount);
    }

    [Fact]
    public async Task Boundaries_0900_Recorded_And_133059_Recorded()
    {
        var e = new Env();
        e.Rec("A", 1m, Utc(1, 0, 0));        // 09:00:00
        e.Rec("B", 1m, Utc(5, 30, 59));      // 13:30:59
        Assert.Equal(2, e.Recorder.BufferedSnapshot().Count);
        Assert.Equal(2, await e.Recorder.FlushAsync(Utc(6, 0, 0), true, default));
    }

    [Fact]
    public async Task Disabled_RecordsNothing_FlushDoesNothing()
    {
        var e = new Env(enabled: false);
        e.Rec("2330", 600m, Utc(2, 0, 10));
        Assert.Empty(e.Recorder.BufferedSnapshot());
        Assert.Equal(0, await e.Recorder.FlushAsync(Utc(3, 0, 0), true, default));
        Assert.Empty(await e.Rows());
    }

    [Fact] // 時鐘倒退(較舊的分鐘)不覆蓋較新的緩衝
    public void OlderBucket_DoesNotOverwriteNewer()
    {
        var e = new Env();
        e.Rec("2330", 600m, Utc(2, 5, 10));
        e.Rec("2330", 500m, Utc(2, 4, 10));
        Assert.Equal(600m, e.Recorder.BufferedSnapshot().Single().Price);
        Assert.Equal(0, e.Recorder.PendingCount);
    }

    // ---------- QT-02 ----------
    [Fact]
    public async Task FlushFailure_KeepsQueue_RetriesAndBackfillsOnNextFlush()
    {
        var e = new Env();
        e.Scopes.Fail = true;
        e.Rec("2330", 600m, Utc(2, 0, 10));
        e.Rec("2330", 601m, Utc(2, 1, 10));
        e.Rec("2330", 602m, Utc(2, 2, 10));                                              // pending: 02:00、02:01;current: 02:02

        var n = await e.Recorder.FlushAsync(Utc(2, 3, 5), false, default);               // 失敗:不丟例外
        Assert.Equal(0, n);
        Assert.Equal(3, e.Recorder.PendingCount);                                        // 佇列保留(含剛移入的 02:02)

        e.Scopes.Fail = false;
        e.Rec("2330", 603m, Utc(2, 3, 20));
        Assert.Equal(4, await e.Recorder.FlushAsync(Utc(2, 4, 5), false, default));      // 下次重試成功,補寫先前失敗的分鐘
        var rows = await e.Rows();
        Assert.Equal([Utc(2, 0, 0), Utc(2, 1, 0), Utc(2, 2, 0), Utc(2, 3, 0)], rows.Select(r => r.BucketUtc).ToArray());
        Assert.Equal(0, e.Recorder.PendingCount);
    }

    [Fact]
    public async Task FlushFailure_LogsOneWarningPer10Minutes_WithoutSymbolsOrPrices()
    {
        var e = new Env();
        e.Scopes.Fail = true;
        e.Rec("2330", 600.123m, Utc(2, 0, 10));
        e.Rec("2330", 601m, Utc(2, 1, 10));
        for (var i = 0; i < 5; i++)
        {
            await e.Recorder.FlushAsync(Utc(2, 2, 0), false, default);
            e.Clock.Advance(TimeSpan.FromMinutes(1));
        }
        var warns = e.Log.Entries.Where(x => x.Level == LogLevel.Warning).ToList();
        Assert.Single(warns);                                                            // 同一原因 10 分鐘內不重複
        Assert.DoesNotContain("2330", warns[0].Message);
        Assert.DoesNotContain("600", warns[0].Message);
        e.Clock.Advance(TimeSpan.FromMinutes(10));
        await e.Recorder.FlushAsync(Utc(2, 2, 0), false, default);
        Assert.Equal(2, e.Log.Entries.Count(x => x.Level == LogLevel.Warning));          // 過了 10 分鐘再記一則
    }

    [Fact] // 佇列上限 = 代號數 × 15 分鐘,超過丟最舊
    public void QueueOverCap_DropsOldest()
    {
        var e = new Env();
        for (var m = 0; m <= 20; m++) e.Rec("2330", 100m + m, Utc(2, 0, 10).AddMinutes(m));   // 21 個分鐘桶 → 20 筆移入佇列
        var pending = e.Recorder.PendingSnapshot();
        Assert.Equal(15, pending.Count);                                                 // 1 檔 × 15
        Assert.Equal(Utc(2, 5, 0), pending.Min(p => p.BucketUtc));                       // 最舊的 5 筆(02:00~02:04)被丟棄
        Assert.Equal(Utc(2, 19, 0), pending.Max(p => p.BucketUtc));
    }

    [Fact] // 失敗時佇列達上限後仍只保留最新的 symbols × 15 筆,恢復後補寫保留下來的
    public async Task OverCapWhileDown_OnlyNewestKeptAndBackfilled()
    {
        var e = new Env();
        e.Scopes.Fail = true;
        for (var m = 0; m <= 20; m++) e.Rec("2330", 100m + m, Utc(2, 0, 10).AddMinutes(m));
        await e.Recorder.FlushAsync(Utc(2, 21, 0), false, default);                      // 失敗
        Assert.Equal(15, e.Recorder.PendingCount);                                       // 剛移入的當前分鐘後再次套用上限,仍是 1 檔 × 15
        e.Scopes.Fail = false;
        var written = await e.Recorder.FlushAsync(Utc(2, 22, 0), false, default);
        Assert.Equal(15, written);
        Assert.Equal(15, (await e.Rows()).Count);
        Assert.Equal(Utc(2, 20, 0), (await e.Rows()).Max(r => r.BucketUtc));
    }

    // ---------- 保留期清理(IT-16 的 Quotes 層:5000 筆分批) ----------
    [Fact]
    public async Task Retention_DeletesOlderThan7Days_InBatches_KeepsOthers()
    {
        var e = new Env();
        var today = new DateOnly(2026, 10, 5);
        using (var s = e.Sp.CreateScope())
        {
            var db = s.ServiceProvider.GetRequiredService<AppDbContext>();
            // 12 個交易日早於保留期(< 2026-09-28),共 5400 列 → 需要 2 批
            for (var i = 0; i < 5400; i++)
                db.QuoteIntraday.Add(new QuoteIntradayRow { Symbol = $"S{i % 20:00}", BucketUtc = Utc(1, 0).AddDays(-30).AddMinutes(i), TradeDate = today.AddDays(-30), Price = 1m });
            db.QuoteIntraday.Add(new QuoteIntradayRow { Symbol = "KEEP", BucketUtc = Utc(2, 0).AddDays(-7), TradeDate = today.AddDays(-7), Price = 1m });   // 剛好 7 天:保留
            db.QuoteIntraday.Add(new QuoteIntradayRow { Symbol = "DEL", BucketUtc = Utc(2, 0).AddDays(-8), TradeDate = today.AddDays(-8), Price = 1m });     // 8 天:刪除
            db.QuoteIntraday.Add(new QuoteIntradayRow { Symbol = "NEW", BucketUtc = Utc(2, 0), TradeDate = today, Price = 1m });
            await db.SaveChangesAsync();
        }
        var cfg = new IntradayConfig(Options.Create(new IntradayOptions()), NullLogger<IntradayConfig>.Instance);
        var job = new IntradayRetention(e.Sp.GetRequiredService<IServiceScopeFactory>(), cfg, Options.Create(new MarketOptions()), e.Clock,
            NullLogger<IntradayRetention>.Instance);
        var deleted = await job.PurgeAsync(default);
        Assert.Equal(5401, deleted);
        Assert.Equal(["KEEP", "NEW"], (await e.Rows()).Select(r => r.Symbol).ToArray());
        Assert.Equal(0, await job.PurgeAsync(default));                                   // 再跑一次沒有東西可刪
    }

    [Fact]
    public async Task Retention_FailureOnlyLogs_DoesNotThrow()
    {
        var e = new Env();
        e.Scopes.Fail = true;
        var cfg = new IntradayConfig(Options.Create(new IntradayOptions()), NullLogger<IntradayConfig>.Instance);
        var job = new IntradayRetention(e.Scopes, cfg, Options.Create(new MarketOptions()), e.Clock, NullLogger<IntradayRetention>.Instance);
        Assert.Equal(0, await job.PurgeAsync(default));
    }

    [Fact]
    public void Options_Normalize_ReplacesInvalidValuesWithDefaults()
    {
        var o = new IntradayOptions { RetentionDays = 0, FlushSeconds = -1, MaxPoints = 1, GapBreakSeconds = 0, MaxRequestsPerMinute = 0,
            RecordStartTime = new TimeSpan(14, 0, 0), RecordEndTime = new TimeSpan(9, 0, 0) };
        var r = o.Normalize(out var errors);
        Assert.Equal(6, errors.Count);
        Assert.Equal((7, 60, 90, 180, 30), (r.RetentionDays, r.FlushSeconds, r.MaxPoints, r.GapBreakSeconds, r.MaxRequestsPerMinute));
        Assert.Equal((new TimeSpan(9, 0, 0), new TimeSpan(13, 30, 0)), (r.RecordStartTime, r.RecordEndTime));
        new IntradayOptions().Normalize(out var none);
        Assert.Empty(none);                                                               // 預設值全部合法
    }
}
