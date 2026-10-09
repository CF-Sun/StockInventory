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

public class FetcherTests
{
    // 2026-10-05 週一 10:00 Taipei = 02:00 UTC(盤中)
    static readonly DateTimeOffset OpenTime = new(2026, 10, 5, 2, 0, 0, TimeSpan.Zero);

    sealed class FakeClient : IMisClient
    {
        public List<string> Calls = [];
        public Func<string, string> Respond = _ => """{"msgArray":[]}""";
        public bool Fail;
        public Task<string> GetAsync(string exCh, CancellationToken ct)
        {
            Calls.Add(exCh);
            if (Fail) throw new HttpRequestException("boom");
            return Task.FromResult(Respond(exCh));
        }
    }
    sealed class Conns(int n) : IActiveConnectionCounter { public int N = n; public int ActiveCount => N; }
    sealed class Sub : IQuoteSubscriber
    {
        public int Applied; public List<(MarketStatusSnapshot S, bool Changed)> Ticks = [];
        public Task OnTickAsync(MarketStatusSnapshot s, bool c, CancellationToken ct) { Ticks.Add((s, c)); return Task.CompletedTask; }
        public Task OnQuotesAppliedAsync(CancellationToken ct) { Applied++; return Task.CompletedTask; }
    }

    sealed class Env
    {
        public FakeTimeProvider Clock = new(OpenTime);
        public FakeClient Client = new();
        public Conns Connections = new(1);
        public Sub Sub = new();
        public QuoteCacheStore Cache = new();
        public ServiceProvider Sp;
        public QuoteFetcher Fetcher;

        public Env(int batchSize = 30, IIntradayRecorder? recorder = null, QuoteOptions? quote = null, ILogger<QuoteFetcher>? log = null)
        {
            var db = Guid.NewGuid().ToString();
            Sp = new ServiceCollection().AddDbContext<AppDbContext>(o => o.UseInMemoryDatabase(db)).BuildServiceProvider();
            quote ??= new QuoteOptions();
            quote.BatchSize = batchSize;
            Fetcher = new QuoteFetcher(Sp.GetRequiredService<IServiceScopeFactory>(), Client, Cache, Connections, [Sub],
                Options.Create(quote), Options.Create(new MarketOptions()), Clock,
                log ?? NullLogger<QuoteFetcher>.Instance, delay: (t, ct) => { Clock.Advance(t); return Task.CompletedTask; }, recorder: recorder);
        }

        public async Task Seed(params (string Sym, Market M)[] items)
        {
            using var s = Sp.CreateScope();
            var d = s.ServiceProvider.GetRequiredService<AppDbContext>();
            var uid = Guid.NewGuid();
            d.Portfolios.Add(new Portfolio { PortfolioId = 1, UserId = uid, Name = "P", SortOrder = 1, CreatedAtUtc = DateTime.UtcNow, UpdatedAtUtc = DateTime.UtcNow });
            foreach (var (sym, m) in items)
            {
                d.Instruments.Add(new Instrument { Symbol = sym, Name = sym, Market = m, Kind = InstrumentKind.Stock, UpdatedAtUtc = DateTime.UtcNow });
                d.Holdings.Add(new Holding { PortfolioId = 1, Symbol = sym, TotalCost = 1, Shares = 1, CreatedAtUtc = DateTime.UtcNow, UpdatedAtUtc = DateTime.UtcNow });
            }
            await d.SaveChangesAsync();
        }

        public async Task Seed2(string sym, Market m)
        {
            using var s = Sp.CreateScope();
            var d = s.ServiceProvider.GetRequiredService<AppDbContext>();
            d.Instruments.Add(new Instrument { Symbol = sym, Name = sym, Market = m, Kind = InstrumentKind.Stock, UpdatedAtUtc = DateTime.UtcNow });
            d.Holdings.Add(new Holding { PortfolioId = 1, Symbol = sym, TotalCost = 1, Shares = 1, CreatedAtUtc = DateTime.UtcNow, UpdatedAtUtc = DateTime.UtcNow });
            await d.SaveChangesAsync();
        }

        public async Task<List<QuoteRow>> Rows()
        {
            using var s = Sp.CreateScope();
            return await s.ServiceProvider.GetRequiredService<AppDbContext>().Quotes.AsNoTracking().ToListAsync();
        }
    }

    static string Body(params (string c, string z, string y)[] q) =>
        "{\"msgArray\":[" + string.Join(',', q.Select(x => $"{{\"c\":\"{x.c}\",\"z\":\"{x.z}\",\"y\":\"{x.y}\",\"a\":\"-\",\"b\":\"-\",\"tlong\":\"{OpenTime.ToUnixTimeMilliseconds()}\"}}")) + "]}";

    [Fact]
    public async Task OpenMarket_FetchesAppliesCacheAndUpsertsQuotes_NotifiesSubscribers()
    {
        var e = new Env();
        await e.Seed(("2330", Market.Twse), ("6488", Market.Tpex));
        e.Client.Respond = _ => Body(("2330", "600.0000", "610.0000"), ("6488", "350.5000", "349.0000"));

        await e.Fetcher.TickAsync(default);

        Assert.Equal("tse_2330.tw|otc_6488.tw", e.Client.Calls.Single()); // 上市 tse_、上櫃 otc_
        var snap = e.Cache.Snapshot();
        Assert.Equal((600m, 610m, PriceSource.Trade), (snap["2330"].LastPrice, snap["2330"].PrevClose, snap["2330"].PriceSource));
        var rows = await e.Rows();
        Assert.Equal(2, rows.Count);
        Assert.Equal(new DateOnly(2026, 10, 5), rows.Single(r => r.Symbol == "2330").TradeDate);
        Assert.Equal(1, e.Sub.Applied);
        var st = e.Fetcher.Get();
        Assert.Equal(MarketState.Open, st.State);
        Assert.False(st.Stale);
        Assert.Equal(OpenTime.UtcDateTime, st.LastFetchedAtUtc);
    }

    [Fact]
    public async Task Closed_StateIsReported_AndCachedSymbolsAreNotRefetched()
    {
        var e = new Env();
        e.Clock.SetUtcNow(new DateTimeOffset(2026, 10, 5, 7, 0, 0, TimeSpan.Zero)); // 15:00 Taipei
        await e.Seed(("2330", Market.Twse));
        e.Cache.Set("2330", new QuoteInput(600m, 610m, PriceSource.Trade, DateTime.UtcNow));
        await e.Fetcher.TickAsync(default);
        Assert.Empty(e.Client.Calls);                                            // 已有價格:收盤後不抓
        Assert.Equal(MarketState.Closed, e.Fetcher.Get().State);
        e.Clock.SetUtcNow(new DateTimeOffset(2026, 10, 10, 2, 0, 0, TimeSpan.Zero)); // 週六
        await e.Fetcher.TickAsync(default);
        Assert.Empty(e.Client.Calls);
        Assert.Equal(MarketState.Holiday, e.Fetcher.Get().State);
    }

    [Fact] // 收盤後新增的持股(快取沒有價格)要能立刻補抓,而不是等到下個開盤日
    public async Task Closed_FetchesOnlyMissingSymbols_WithThrottle_AndPriorityBypass()
    {
        var e = new Env();
        e.Clock.SetUtcNow(new DateTimeOffset(2026, 10, 5, 7, 0, 0, TimeSpan.Zero)); // 15:00 Taipei,已收盤
        await e.Seed(("2330", Market.Twse), ("1605", Market.Twse));
        e.Cache.Set("2330", new QuoteInput(600m, 610m, PriceSource.Trade, DateTime.UtcNow));
        e.Client.Respond = _ => Body(("1605", "30.5000", "30.0000"));

        await e.Fetcher.TickAsync(default);
        Assert.Equal("tse_1605.tw", e.Client.Calls.Single());                    // 只抓缺價的 1605
        Assert.Equal(30.5m, e.Cache.Snapshot()["1605"].LastPrice);
        Assert.Equal(MarketState.Closed, e.Fetcher.Get().State);

        e.Clock.Advance(TimeSpan.FromSeconds(5));
        await e.Fetcher.TickAsync(default);
        Assert.Single(e.Client.Calls);                                           // 已補到價格,不再抓

        // 抓不到價格的標的:最多每 300 秒試一次,不會整晚每 5 秒打 MIS
        await e.Seed2("9999", Market.Twse);
        e.Client.Respond = _ => """{"msgArray":[{"tv":"-","s":"-","c":"","z":"-"}]}""";
        e.Fetcher.RequestImmediateFetch("9999");                                 // API 新增持股後會呼叫這個
        await e.Fetcher.TickAsync(default);
        Assert.Equal(2, e.Client.Calls.Count);
        for (var i = 0; i < 10; i++) { e.Clock.Advance(TimeSpan.FromSeconds(5)); await e.Fetcher.TickAsync(default); }
        Assert.Equal(2, e.Client.Calls.Count);                                   // 節流中
        e.Clock.Advance(TimeSpan.FromSeconds(300));
        await e.Fetcher.TickAsync(default);
        Assert.Equal(3, e.Client.Calls.Count);                                   // 超過 300 秒才再試

        e.Fetcher.RequestImmediateFetch("9999");                                 // 新增持股的優先請求不受節流限制
        await e.Fetcher.TickAsync(default);
        Assert.Equal(4, e.Client.Calls.Count);
    }

    [Fact]
    public async Task NoUsablePrice_DoesNotOverwriteCacheOrTable()
    {
        var e = new Env();
        await e.Seed(("2330", Market.Twse));
        e.Client.Respond = _ => Body(("2330", "600.0000", "610.0000"));
        await e.Fetcher.TickAsync(default);

        e.Clock.Advance(TimeSpan.FromSeconds(6));
        e.Client.Respond = _ => Body(("2330", "-", "-"));
        await e.Fetcher.TickAsync(default);

        Assert.Equal(600m, e.Cache.Snapshot()["2330"].LastPrice);
        Assert.Equal(600m, (await e.Rows()).Single().LastPrice);
    }

    [Fact] // QT-03(Q3):盤中沒人看也持續抓價,間隔 UnwatchedIntervalSeconds(30 秒);新增持股的優先請求不受限
    public async Task Open_NoConnections_FetchesEveryUnwatchedInterval_ButPriorityRequestBypasses()
    {
        var e = new Env { };
        e.Connections.N = 0;
        await e.Seed(("2330", Market.Twse), ("0050", Market.Twse));
        e.Client.Respond = _ => Body(("2330", "1", "1"));

        await e.Fetcher.TickAsync(default);                 // 第一次:從未抓過 → 抓
        Assert.Single(e.Client.Calls);
        e.Clock.Advance(TimeSpan.FromSeconds(29));
        await e.Fetcher.TickAsync(default);                 // 距上次 29 秒 → 不抓
        Assert.Single(e.Client.Calls);

        e.Fetcher.RequestImmediateFetch("0050");            // 新增持股後優先抓(不等 30 秒)
        await e.Fetcher.TickAsync(default);
        Assert.Equal(2, e.Client.Calls.Count);
        Assert.StartsWith("tse_0050.tw", e.Client.Calls[1]); // 優先代號排最前

        e.Clock.Advance(TimeSpan.FromSeconds(29));
        await e.Fetcher.TickAsync(default);                 // 距上次 29 秒 → 不抓
        Assert.Equal(2, e.Client.Calls.Count);
        e.Clock.Advance(TimeSpan.FromSeconds(1));
        await e.Fetcher.TickAsync(default);                 // 剛好 30 秒 → 抓
        Assert.Equal(3, e.Client.Calls.Count);
    }

    [Fact] // QT-03:29 秒不抓、30 秒抓(無連線);有連線 5 秒抓
    public async Task Open_IntervalBoundaries_NoConnections29Vs30_WithConnections5()
    {
        var e = new Env();
        e.Connections.N = 0;
        await e.Seed(("2330", Market.Twse));
        e.Client.Respond = _ => Body(("2330", "1", "1"));
        await e.Fetcher.TickAsync(default);
        Assert.Single(e.Client.Calls);
        e.Clock.Advance(TimeSpan.FromSeconds(29));
        await e.Fetcher.TickAsync(default);
        Assert.Single(e.Client.Calls);
        e.Clock.Advance(TimeSpan.FromSeconds(1));
        await e.Fetcher.TickAsync(default);
        Assert.Equal(2, e.Client.Calls.Count);

        e.Connections.N = 1;                                // 有人看:5 秒間隔
        e.Clock.Advance(TimeSpan.FromSeconds(4));
        await e.Fetcher.TickAsync(default);
        Assert.Equal(2, e.Client.Calls.Count);
        e.Clock.Advance(TimeSpan.FromSeconds(1));
        await e.Fetcher.TickAsync(default);
        Assert.Equal(3, e.Client.Calls.Count);
        e.Clock.Advance(TimeSpan.FromSeconds(5));
        await e.Fetcher.TickAsync(default);
        Assert.Equal(4, e.Client.Calls.Count);
    }

    sealed class SpyRecorder : IIntradayRecorder
    {
        public List<(string Symbol, decimal Price, PriceSource Source, MarketState State, DateTime? QuoteTime, DateTime Fetched)> Calls = [];
        public bool Throw;
        public void Record(string symbol, decimal price, PriceSource source, MarketState state, DateTime? quoteTimeUtc, DateTime fetchedAtUtc)
        {
            Calls.Add((symbol, price, source, state, quoteTimeUtc, fetchedAtUtc));
            if (Throw) throw new InvalidOperationException("recorder boom");
        }
    }

    [Fact] // QT-03:盤中沒人看仍會呼叫紀錄器(帶觀測時間、報價時間、市場狀態)
    public async Task Open_NoConnections_StillCallsRecorder()
    {
        var rec = new SpyRecorder();
        var e = new Env(recorder: rec);
        e.Connections.N = 0;
        await e.Seed(("2330", Market.Twse));
        e.Client.Respond = _ => Body(("2330", "600.5", "610"));
        await e.Fetcher.TickAsync(default);
        var c = Assert.Single(rec.Calls);
        Assert.Equal(("2330", 600.5m, PriceSource.Trade, MarketState.Open), (c.Symbol, c.Price, c.Source, c.State));
        Assert.Equal(OpenTime.UtcDateTime, c.Fetched);                 // 觀測時間 = 本系統收到回應的時間
        Assert.Equal(OpenTime.UtcDateTime, c.QuoteTime);               // tlong
    }

    [Fact] // 紀錄器只收到本輪請求的持股代號;回應夾帶的其他代號不記
    public async Task Recorder_OnlyReceivesRequestedHoldingSymbols()
    {
        var rec = new SpyRecorder();
        var e = new Env(recorder: rec);
        await e.Seed(("2330", Market.Twse));
        using (var s = e.Sp.CreateScope())
        {
            var d = s.ServiceProvider.GetRequiredService<AppDbContext>();
            d.Instruments.Add(new Instrument { Symbol = "0050", Name = "0050", Market = Market.Twse, Kind = InstrumentKind.Etf, UpdatedAtUtc = DateTime.UtcNow });
            await d.SaveChangesAsync();                                 // 0050 在主檔但沒人持有
        }
        e.Client.Respond = _ => Body(("2330", "600", "610"), ("0050", "150", "149"));
        await e.Fetcher.TickAsync(default);
        Assert.Equal(["2330"], rec.Calls.Select(c => c.Symbol).ToArray());
    }

    [Fact] // Q14:紀錄器丟出例外不影響 Quotes upsert、快取與推播
    public async Task RecorderFailure_DoesNotAffectQuotesCacheOrNotification()
    {
        var rec = new SpyRecorder { Throw = true };
        var e = new Env(recorder: rec);
        await e.Seed(("2330", Market.Twse), ("6488", Market.Tpex));
        e.Client.Respond = _ => Body(("2330", "600", "610"), ("6488", "350", "349"));
        await e.Fetcher.TickAsync(default);
        Assert.Equal(2, rec.Calls.Count);                               // 兩檔都嘗試記錄
        Assert.Equal(2, (await e.Rows()).Count);
        Assert.Equal(600m, e.Cache.Snapshot()["2330"].LastPrice);
        Assert.Equal(1, e.Sub.Applied);
        Assert.False(e.Fetcher.Get().Stale);
    }

    [Fact] // QT-03:非開盤只補抓缺價標的,且不會把非開盤報價交給紀錄器記錄(狀態為 Closed)
    public async Task Closed_MissingFetch_PassesClosedStateToRecorder_AndKeepsThrottle()
    {
        var rec = new SpyRecorder();
        var e = new Env(recorder: rec);
        e.Clock.SetUtcNow(new DateTimeOffset(2026, 10, 5, 7, 0, 0, TimeSpan.Zero)); // 15:00 Taipei
        await e.Seed(("2330", Market.Twse));
        e.Client.Respond = _ => Body(("2330", "600", "610"));
        await e.Fetcher.TickAsync(default);
        Assert.Equal(MarketState.Closed, Assert.Single(rec.Calls).State);
        e.Cache.Set("2330", new QuoteInput(600m, 610m, PriceSource.Trade, DateTime.UtcNow));
        e.Clock.Advance(TimeSpan.FromSeconds(60));
        await e.Fetcher.TickAsync(default);
        Assert.Single(e.Client.Calls);                                  // 已有價格:不再抓
    }

    [Theory] // Quote:UnwatchedIntervalSeconds 超出範圍(< Active 或 > 300):回報設定錯誤並使用預設 30 秒
    [InlineData(4)]
    [InlineData(301)]
    [InlineData(0)]
    public async Task UnwatchedInterval_OutOfRange_FallsBackToDefault30(int configured)
    {
        var e = new Env(quote: new QuoteOptions { UnwatchedIntervalSeconds = configured });
        e.Connections.N = 0;
        await e.Seed(("2330", Market.Twse));
        e.Client.Respond = _ => Body(("2330", "1", "1"));
        await e.Fetcher.TickAsync(default);
        e.Clock.Advance(TimeSpan.FromSeconds(29));
        await e.Fetcher.TickAsync(default);
        Assert.Single(e.Client.Calls);
        e.Clock.Advance(TimeSpan.FromSeconds(1));
        await e.Fetcher.TickAsync(default);
        Assert.Equal(2, e.Client.Calls.Count);
    }

    [Theory] // 範圍內(含邊界 5 與 300)的設定被採用
    [InlineData(5, 5)]
    [InlineData(120, 120)]
    [InlineData(300, 300)]
    public async Task UnwatchedInterval_InRange_IsUsed(int configured, int expectedSeconds)
    {
        var e = new Env(quote: new QuoteOptions { UnwatchedIntervalSeconds = configured });
        e.Connections.N = 0;
        await e.Seed(("2330", Market.Twse));
        e.Client.Respond = _ => Body(("2330", "1", "1"));
        await e.Fetcher.TickAsync(default);
        e.Clock.Advance(TimeSpan.FromSeconds(expectedSeconds - 1));
        await e.Fetcher.TickAsync(default);
        Assert.Single(e.Client.Calls);
        e.Clock.Advance(TimeSpan.FromSeconds(1));
        await e.Fetcher.TickAsync(default);
        Assert.Equal(2, e.Client.Calls.Count);
    }

    [Fact] // QT-03:失敗退避期間不抓(沒人看的 30 秒間隔已到,但連續失敗 5 次後的 60 秒退避仍生效)
    public async Task Open_NoConnections_BackoffStillApplies()
    {
        var e = new Env();
        e.Connections.N = 0;
        await e.Seed(("2330", Market.Twse));
        e.Client.Fail = true;
        async Task Tick(double advance) { e.Clock.Advance(TimeSpan.FromSeconds(advance)); await e.Fetcher.TickAsync(default); }

        await Tick(0);   // t=0   失敗 1
        await Tick(30);  // t=30  失敗 2
        await Tick(30);  // t=60  失敗 3 → 退避 15 秒(到 t=75)
        await Tick(30);  // t=90  失敗 4 → 退避 30 秒(到 t=120)
        await Tick(30);  // t=120 失敗 5 → 退避 60 秒(到 t=180)
        Assert.Equal(5, e.Client.Calls.Count);
        await Tick(30);  // t=150 間隔(30 秒)已到,但仍在退避期 → 不抓
        Assert.Equal(5, e.Client.Calls.Count);
        e.Client.Fail = false; e.Client.Respond = _ => Body(("2330", "1", "1"));
        await Tick(30);  // t=180 退避結束 → 抓,成功
        Assert.Equal(6, e.Client.Calls.Count);
        await Tick(29);  // 成功後恢復正常的 30 秒間隔
        Assert.Equal(6, e.Client.Calls.Count);
        await Tick(1);
        Assert.Equal(7, e.Client.Calls.Count);
    }

    [Fact]
    public async Task Failures_BackOff_15_30_60_ThenRecover()
    {
        var e = new Env();
        await e.Seed(("2330", Market.Twse));
        e.Client.Fail = true;

        async Task Tick(double advance) { e.Clock.Advance(TimeSpan.FromSeconds(advance)); await e.Fetcher.TickAsync(default); }

        await Tick(0); await Tick(5); await Tick(5);   // 連續失敗 3 次
        Assert.Equal(3, e.Client.Calls.Count);
        await Tick(5);  Assert.Equal(3, e.Client.Calls.Count);   // 15 秒退避內
        await Tick(10); Assert.Equal(4, e.Client.Calls.Count);   // 第 15 秒 → 重試(第 4 次失敗)
        await Tick(20); Assert.Equal(4, e.Client.Calls.Count);   // 30 秒退避內
        await Tick(10); Assert.Equal(5, e.Client.Calls.Count);   // 第 30 秒 → 重試(第 5 次失敗)
        await Tick(50); Assert.Equal(5, e.Client.Calls.Count);   // 60 秒退避內
        e.Client.Fail = false; e.Client.Respond = _ => Body(("2330", "1", "1"));
        await Tick(10); Assert.Equal(6, e.Client.Calls.Count);   // 第 60 秒 → 成功
        await Tick(5);  Assert.Equal(7, e.Client.Calls.Count);   // 成功後恢復正常間隔
    }

    [Fact]
    public async Task GarbageResponse_CountsAsFailure_KeepsOldPrice()
    {
        var e = new Env();
        await e.Seed(("2330", Market.Twse));
        e.Client.Respond = _ => Body(("2330", "600", "610"));
        await e.Fetcher.TickAsync(default);
        e.Clock.Advance(TimeSpan.FromSeconds(5));
        e.Client.Respond = _ => "<html>503</html>";
        await e.Fetcher.TickAsync(default);
        Assert.Equal(600m, e.Cache.Snapshot()["2330"].LastPrice);
        Assert.Equal(1, e.Sub.Applied);
    }

    [Fact]
    public async Task Stale_BecomesTrue_After180Seconds_OnlyWhileOpen_AndNeverBeforeFirstSuccess()
    {
        var e = new Env();
        await e.Seed(("2330", Market.Twse));
        e.Client.Fail = true;
        await e.Fetcher.TickAsync(default);
        Assert.False(e.Fetcher.Get().Stale);                     // 從未成功:stale = false,lastFetched = null
        Assert.Null(e.Fetcher.Get().LastFetchedAtUtc);

        e.Client.Fail = false; e.Client.Respond = _ => Body(("2330", "1", "1"));
        e.Clock.Advance(TimeSpan.FromSeconds(60));
        await e.Fetcher.TickAsync(default);
        e.Client.Fail = true;
        e.Clock.Advance(TimeSpan.FromSeconds(180));
        await e.Fetcher.TickAsync(default);
        Assert.False(e.Fetcher.Get().Stale);                     // 剛好 180 秒不算
        e.Clock.Advance(TimeSpan.FromSeconds(1));
        await e.Fetcher.TickAsync(default);
        Assert.True(e.Fetcher.Get().Stale);
        Assert.Contains(e.Sub.Ticks, t => t.S.Stale && t.Changed); // 旗標改變有通知
    }

    sealed class ListLogger : ILogger<QuoteFetcher>
    {
        public List<(LogLevel Level, string Message)> Entries = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            Entries.Add((logLevel, formatter(state, exception)));
    }

    [Fact] // 驗收用:盤中每 10 分鐘記一則抓價統計,可看出沒人看時約每 30 秒一輪(10 分鐘約 20 輪)
    public async Task Open_NoConnections_LogsRoundStatsEvery10Minutes()
    {
        var log = new ListLogger();
        var e = new Env(log: log);
        e.Connections.N = 0;
        await e.Seed(("2330", Market.Twse));
        e.Client.Respond = _ => Body(("2330", "1", "1"));
        for (var i = 0; i < 12 * 21; i++) { await e.Fetcher.TickAsync(default); e.Clock.Advance(TimeSpan.FromSeconds(5)); } // 21 分鐘
        var stats = log.Entries.Where(x => x.Message.StartsWith("盤中抓價統計")).ToList();
        Assert.Equal(2, stats.Count);
        Assert.Matches(@"共 2[01] 輪\(有人看 0、沒人看 2[01]\)", stats[0].Message);   // 10 分鐘 / 30 秒 = 20 輪(含頭尾視窗邊界為 21)
        Assert.DoesNotContain("2330", stats[0].Message);
    }

    [Fact] // v1.5(§7.4):無連線時門檻為 Unwatched(30) + Stale(180) = 210 秒;有連線 180 秒
    public async Task Stale_WhenNoConnections_UsesUnwatchedPlusStaleThreshold()
    {
        var e = new Env();
        e.Connections.N = 0;
        await e.Seed(("2330", Market.Twse));
        e.Client.Respond = _ => Body(("2330", "1", "1"));
        await e.Fetcher.TickAsync(default);                       // t0 成功
        e.Client.Fail = true;

        e.Clock.Advance(TimeSpan.FromSeconds(190));
        await e.Fetcher.TickAsync(default);
        Assert.False(e.Fetcher.Get().Stale);                      // 距 t0 190 秒:無連線門檻 210 秒內(有連線的 180 秒規則會誤判)

        e.Clock.Advance(TimeSpan.FromSeconds(20));                // 距 t0 = 210 秒
        Assert.False(e.Fetcher.Get().Stale);                      // 剛好 210 秒不算
        e.Clock.Advance(TimeSpan.FromMilliseconds(1));
        Assert.True(e.Fetcher.Get().Stale);                       // 210.001 秒才算

        e.Connections.N = 1;                                      // 有人連線:門檻 180 秒,所以仍為 stale
        Assert.True(e.Fetcher.Get().Stale);
    }

    [Fact] // 有連線的門檻仍是 180 秒;沒連線時 181 秒不算 stale
    public async Task Stale_ThresholdFollowsConnectionCount_At181Seconds()
    {
        var e = new Env();
        e.Connections.N = 0;
        await e.Seed(("2330", Market.Twse));
        e.Client.Respond = _ => Body(("2330", "1", "1"));
        await e.Fetcher.TickAsync(default);
        e.Client.Fail = true;
        e.Clock.Advance(TimeSpan.FromSeconds(181));
        Assert.False(e.Fetcher.Get().Stale);                      // 無連線:門檻 210
        e.Connections.N = 1;
        Assert.True(e.Fetcher.Get().Stale);                       // 有連線:門檻 180
    }

    [Fact] // 沒人看期間正常抓取(每 30 秒一次)不應觸發任何 stale 通知
    public async Task UnwatchedNormalOperation_NeverFlagsStale()
    {
        var e = new Env();
        e.Connections.N = 0;
        await e.Seed(("2330", Market.Twse));
        e.Client.Respond = _ => Body(("2330", "1", "1"));
        for (var i = 0; i < 12; i++)                               // 模擬 1 小時,每 5 秒喚醒一次
        {
            for (var k = 0; k < 60; k++) { await e.Fetcher.TickAsync(default); e.Clock.Advance(TimeSpan.FromSeconds(5)); }
            Assert.False(e.Fetcher.Get().Stale);
        }
        Assert.All(e.Sub.Ticks, t => Assert.False(t.S.Stale));
        Assert.Equal(120, e.Client.Calls.Count);                   // 一小時 = 120 次(每 30 秒一次),不是 12 次
    }

    [Fact]
    public async Task Batches_BySize_And_RateLimited()
    {
        var e = new Env(batchSize: 2);
        await e.Seed(Enumerable.Range(1, 9).Select(i => ($"T{i}", Market.Twse)).ToArray());
        var t0 = e.Clock.GetUtcNow();
        await e.Fetcher.TickAsync(default);
        Assert.Equal(5, e.Client.Calls.Count);                    // 9 檔 / 2 = 5 批
        Assert.Equal(new[] { 2, 2, 2, 2, 1 }, e.Client.Calls.Select(c => c.Split('|').Length).ToArray());
        Assert.True((e.Clock.GetUtcNow() - t0).TotalSeconds >= 5); // 每 5 秒最多 3 次 → 第 4 批要等
    }

    [Fact]
    public async Task LoadCache_FromQuotesTable()
    {
        var e = new Env();
        await e.Seed(("2330", Market.Twse));
        using (var s = e.Sp.CreateScope())
        {
            var d = s.ServiceProvider.GetRequiredService<AppDbContext>();
            d.Quotes.Add(new QuoteRow { Symbol = "2330", LastPrice = 590m, PrevClose = 600m, PriceSource = PriceSource.PrevClose, TradeDate = new(2026, 10, 2), FetchedAtUtc = DateTime.UtcNow });
            await d.SaveChangesAsync();
        }
        await e.Fetcher.LoadCacheAsync(default);
        Assert.Equal((590m, PriceSource.PrevClose), (e.Cache.Snapshot()["2330"].LastPrice, e.Cache.Snapshot()["2330"].PriceSource));
    }

    [Fact]
    public async Task HolidayInTable_MeansHoliday()
    {
        var e = new Env();
        using (var s = e.Sp.CreateScope())
        {
            var d = s.ServiceProvider.GetRequiredService<AppDbContext>();
            d.MarketHolidays.Add(new MarketHoliday { HolidayDate = new DateOnly(2026, 10, 5), Description = "測試休市" });
            await d.SaveChangesAsync();
        }
        await e.Fetcher.TickAsync(default);
        Assert.Equal(MarketState.Holiday, e.Fetcher.Get().State);
    }
}

public class LimiterAndClientTests
{
    [Fact]
    public async Task Limiter_AllowsMaxPerWindow_ThenWaits()
    {
        var clock = new FakeTimeProvider(DateTimeOffset.UnixEpoch);
        var waits = new List<TimeSpan>();
        var lim = new SlidingWindowLimiter(3, TimeSpan.FromSeconds(5), clock, (t, _) => { waits.Add(t); clock.Advance(t); return Task.CompletedTask; });
        for (var i = 0; i < 3; i++) { await lim.WaitAsync(default); clock.Advance(TimeSpan.FromSeconds(1)); }
        Assert.Empty(waits);
        await lim.WaitAsync(default);                  // 第 4 次:最舊的在 t=0,要等到 t=5(現在 t=3)
        Assert.Equal(TimeSpan.FromSeconds(2), Assert.Single(waits));
    }

    sealed class SlowHandler : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage r, CancellationToken ct)
        {
            await Task.Delay(Timeout.Infinite, ct);
            return new HttpResponseMessage();
        }
    }
    sealed class StaticHandler(System.Net.HttpStatusCode code, string body) : HttpMessageHandler
    {
        public Uri? Last;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage r, CancellationToken ct)
        {
            Last = r.RequestUri;
            return Task.FromResult(new HttpResponseMessage(code) { Content = new StringContent(body) });
        }
    }

    [Fact] // 逾時視為失敗
    public async Task Client_Timeout_ThrowsOperationCanceled()
    {
        var c = new MisClient(new HttpClient(new SlowHandler()), Options.Create(new QuoteOptions { TimeoutSeconds = 1 }), TimeProvider.System);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => c.GetAsync("tse_2330.tw", default));
    }

    [Fact]
    public async Task Client_BuildsUrl_AndRejectsNon2xx()
    {
        var ok = new StaticHandler(System.Net.HttpStatusCode.OK, "{}");
        var c = new MisClient(new HttpClient(ok), Options.Create(new QuoteOptions()), TimeProvider.System);
        Assert.Equal("{}", await c.GetAsync("tse_2330.tw|otc_6488.tw", default));
        Assert.Contains("ex_ch=tse_2330.tw%7Cotc_6488.tw&json=1&delay=0&_=", ok.Last!.AbsoluteUri);

        var bad = new MisClient(new HttpClient(new StaticHandler(System.Net.HttpStatusCode.ServiceUnavailable, "x")), Options.Create(new QuoteOptions()), TimeProvider.System);
        await Assert.ThrowsAsync<HttpRequestException>(() => bad.GetAsync("x", default));
    }
}
