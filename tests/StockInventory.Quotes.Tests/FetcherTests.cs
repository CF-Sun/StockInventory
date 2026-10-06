using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
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

        public Env(int batchSize = 30)
        {
            var db = Guid.NewGuid().ToString();
            Sp = new ServiceCollection().AddDbContext<AppDbContext>(o => o.UseInMemoryDatabase(db)).BuildServiceProvider();
            Fetcher = new QuoteFetcher(Sp.GetRequiredService<IServiceScopeFactory>(), Client, Cache, Connections, [Sub],
                Options.Create(new QuoteOptions { BatchSize = batchSize }), Options.Create(new MarketOptions()), Clock,
                NullLogger<QuoteFetcher>.Instance, delay: (t, ct) => { Clock.Advance(t); return Task.CompletedTask; });
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

    [Fact]
    public async Task NoConnections_WaitsIdleInterval_ButPriorityRequestBypasses()
    {
        var e = new Env { };
        e.Connections.N = 0;
        await e.Seed(("2330", Market.Twse), ("0050", Market.Twse));
        e.Client.Respond = _ => Body(("2330", "1", "1"));

        await e.Fetcher.TickAsync(default);                 // 第一次:從未抓過 → 抓
        Assert.Single(e.Client.Calls);
        e.Clock.Advance(TimeSpan.FromSeconds(60));
        await e.Fetcher.TickAsync(default);                 // 無連線且 < 300 秒 → 不抓
        Assert.Single(e.Client.Calls);

        e.Fetcher.RequestImmediateFetch("0050");            // 新增持股後優先抓
        await e.Fetcher.TickAsync(default);
        Assert.Equal(2, e.Client.Calls.Count);
        Assert.StartsWith("tse_0050.tw", e.Client.Calls[1]); // 優先代號排最前

        e.Clock.Advance(TimeSpan.FromSeconds(301));
        await e.Fetcher.TickAsync(default);                 // 過了 300 秒 → 再抓
        Assert.Equal(3, e.Client.Calls.Count);
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

    [Fact] // 方案 a:無連線時門檻為 Idle(300) + Stale(180) = 480 秒
    public async Task Stale_WhenNoConnections_UsesIdlePlusStaleThreshold()
    {
        var e = new Env();
        e.Connections.N = 0;
        await e.Seed(("2330", Market.Twse));
        e.Client.Respond = _ => Body(("2330", "1", "1"));
        await e.Fetcher.TickAsync(default);                       // t0 成功
        e.Client.Fail = true;

        e.Clock.Advance(TimeSpan.FromSeconds(200));
        await e.Fetcher.TickAsync(default);
        Assert.False(e.Fetcher.Get().Stale);                      // 正常閒置間隔內(舊規則 180 秒會誤判為 stale)

        e.Clock.Advance(TimeSpan.FromSeconds(280));               // 距 t0 = 480 秒
        Assert.False(e.Fetcher.Get().Stale);                      // 剛好 480 秒不算
        e.Clock.Advance(TimeSpan.FromSeconds(1));
        Assert.True(e.Fetcher.Get().Stale);                       // 481 秒才算

        e.Connections.N = 1;                                      // 有人連線後回到 180 秒門檻
        Assert.True(e.Fetcher.Get().Stale);
    }

    [Fact] // 閒置期間正常抓取(每 300 秒一次)不應觸發任何 stale 通知
    public async Task IdleNormalOperation_NeverFlagsStale()
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
