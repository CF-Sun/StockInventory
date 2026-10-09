using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using StockInventory.Core;
using StockInventory.Data;
using StockInventory.Quotes;
using StockInventory.Web.Services;
using Xunit;
using A = StockInventory.Web.Tests.AdminApiTests;

namespace StockInventory.Web.Tests;

/// <summary>
/// FR-26 API-15(IT-13 至 IT-17)。Web 測試使用 InMemory:不驗證實際 SQL Server 的 upsert、datetime2(0)、CHECK 與 FK,
/// 這些列入 SPEC §16.3 手動驗收。
/// </summary>
public class IntradayApiTests
{
    // 2026-10-07 週三 10:00 台北時間(盤中);Taipei 09:00 = 01:00Z
    static readonly DateTimeOffset Wed10 = new(2026, 10, 7, 2, 0, 0, TimeSpan.Zero);
    static readonly DateTimeOffset Sat10 = new(2026, 10, 10, 2, 0, 0, TimeSpan.Zero);
    static readonly DateOnly Wed = new(2026, 10, 7);

    sealed class StubMarket(MarketState s) : IMarketStatusSource
    {
        public MarketState State = s;
        public MarketStatusSnapshot Get() => new(State, false, null);
    }

    sealed class ThrowingService : IIntradayService
    {
        public Task<IntradayDto?> GetAsync(Guid userId, IReadOnlyList<string> symbols, DateOnly? tradeDate, DateTime? sinceUtc, CancellationToken ct = default) =>
            throw new InvalidOperationException("QuoteIntraday table is missing");
    }

    sealed record Env(TestFactory F, FakeTimeProvider Clock, StubMarket Market);

    static Env Make(DateTimeOffset? now = null, MarketState state = MarketState.Open, Dictionary<string, string?>? settings = null,
        Action<IServiceCollection>? more = null)
    {
        var clock = new FakeTimeProvider(now ?? Wed10);
        var market = new StubMarket(state);
        var f = new TestFactory(settings, sv =>
        {
            foreach (var d in sv.Where(d => d.ServiceType == typeof(TimeProvider) || d.ServiceType == typeof(IMarketStatusSource)).ToList()) sv.Remove(d);
            sv.AddSingleton<TimeProvider>(clock);
            sv.AddSingleton<IMarketStatusSource>(market);
            more?.Invoke(sv);
        });
        return new Env(f, clock, market);
    }

    static async Task SeedInstruments(TestFactory f, params string[] symbols)
    {
        using var s = f.Services.CreateScope();
        var db = s.ServiceProvider.GetRequiredService<AppDbContext>();
        foreach (var sym in symbols)
            db.Instruments.Add(new Instrument { Symbol = sym, Name = "名稱" + sym, Market = Market.Twse, Kind = InstrumentKind.Stock, UpdatedAtUtc = DateTime.UtcNow });
        await db.SaveChangesAsync();
    }

    static async Task Hold(HttpClient c, string csrf, params string[] symbols)
    {
        var pid = (await A.Json(await c.SendAsync(A.Req(HttpMethod.Post, "/api/portfolios", csrf, new { name = "P" })))).GetProperty("id").GetInt32();
        foreach (var sym in symbols)
            Assert.Equal(HttpStatusCode.Created, (await c.SendAsync(A.Req(HttpMethod.Post, $"/api/portfolios/{pid}/holdings", csrf,
                new { symbol = sym, totalCost = 1000, shares = 10 }))).StatusCode);
    }

    static async Task SeedPoints(TestFactory f, string symbol, DateOnly date, IEnumerable<(DateTime BucketUtc, decimal Price)> points)
    {
        using var s = f.Services.CreateScope();
        var db = s.ServiceProvider.GetRequiredService<AppDbContext>();
        db.QuoteIntraday.AddRange(points.Select(p => new QuoteIntradayRow { Symbol = symbol, BucketUtc = p.BucketUtc, TradeDate = date, Price = p.Price }));
        await db.SaveChangesAsync();
    }

    static async Task SeedQuote(TestFactory f, string symbol, DateOnly tradeDate, decimal? prevClose)
    {
        using var s = f.Services.CreateScope();
        var db = s.ServiceProvider.GetRequiredService<AppDbContext>();
        db.Quotes.Add(new QuoteRow { Symbol = symbol, LastPrice = 100m, PrevClose = prevClose, PriceSource = PriceSource.Trade, TradeDate = tradeDate, FetchedAtUtc = DateTime.UtcNow });
        await db.SaveChangesAsync();
    }

    static DateTime Z(int day, int h, int m) => new(2026, 10, day, h, m, 0, DateTimeKind.Utc);

    static string Iso(DateTime t) => t.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'");

    static JsonElement Series(JsonElement body, string symbol) =>
        body.GetProperty("series").EnumerateArray().First(x => x.GetProperty("symbol").GetString() == symbol);

    static async Task AssertProblem(HttpResponseMessage r, HttpStatusCode status, string code)
    {
        Assert.Equal(status, r.StatusCode);
        Assert.Equal("application/problem+json", r.Content.Headers.ContentType?.MediaType);
        Assert.Equal(code, (await A.Json(r)).GetProperty("code").GetString());
    }

    // ---------- IT-13 資料隔離 ----------
    [Fact]
    public async Task IT13_Isolation_OtherUsersSymbolsNonHoldingsAndMixed_AllReturn404()
    {
        var env = Make(); using var f = env.F;
        await SeedInstruments(f, "2330", "0050", "1101", "2317");
        var (alice, aCsrf, _) = await A.SignInAsync(f, "alice", "User");
        var (bob, bCsrf, _) = await A.SignInAsync(f, "bob", "User");
        await Hold(alice, aCsrf, "2330", "0050");
        await Hold(bob, bCsrf, "1101");

        await AssertProblem(await alice.GetAsync("/api/intraday?symbols=1101"), HttpStatusCode.NotFound, "NOT_FOUND");   // 他人持股
        await AssertProblem(await alice.GetAsync("/api/intraday?symbols=2317"), HttpStatusCode.NotFound, "NOT_FOUND");   // 主檔有但沒持有
        await AssertProblem(await alice.GetAsync("/api/intraday?symbols=9999"), HttpStatusCode.NotFound, "NOT_FOUND");   // 不存在
        await AssertProblem(await alice.GetAsync("/api/intraday?symbols=2330,1101"), HttpStatusCode.NotFound, "NOT_FOUND"); // 混入一個非持股 → 整批 404
        await AssertProblem(await bob.GetAsync("/api/intraday?symbols=2330"), HttpStatusCode.NotFound, "NOT_FOUND");

        var ok = await alice.GetAsync("/api/intraday?symbols=2330,0050");
        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
        Assert.Equal(["2330", "0050"], (await A.Json(ok)).GetProperty("series").EnumerateArray().Select(x => x.GetProperty("symbol").GetString()).ToArray());
    }

    [Fact] // 同一代號被兩位使用者持有,各自只能讀自己的請求;他人庫存刪除後即失去權限
    public async Task IT13_HoldingRemoved_BecomesNotFound_AndAnonymousGets401()
    {
        var env = Make(); using var f = env.F;
        await SeedInstruments(f, "2330");
        var (alice, csrf, _) = await A.SignInAsync(f, "alice", "User");
        await Hold(alice, csrf, "2330");
        Assert.Equal(HttpStatusCode.OK, (await alice.GetAsync("/api/intraday?symbols=2330")).StatusCode);

        var hid = (await A.Json(await alice.GetAsync("/api/portfolios/1/holdings"))).EnumerateArray().First().GetProperty("holdingId").GetInt32();
        Assert.Equal(HttpStatusCode.NoContent, (await alice.SendAsync(A.Req(HttpMethod.Delete, $"/api/holdings/{hid}", csrf))).StatusCode);
        await AssertProblem(await alice.GetAsync("/api/intraday?symbols=2330"), HttpStatusCode.NotFound, "NOT_FOUND");

        Assert.Equal(HttpStatusCode.Unauthorized, (await LoginFlowTests.NewClient(f).GetAsync("/api/intraday?symbols=2330")).StatusCode);
    }

    // ---------- IT-14 參數驗證 ----------
    [Theory]
    [InlineData("")]
    [InlineData("symbols=")]
    [InlineData("symbols=%20,%20")]
    [InlineData("symbols=23$0")]
    [InlineData("symbols=2330;DROP")]
    [InlineData("symbols=12345678901")]                                     // 11 字元
    [InlineData("symbols=2330&sinceUtc=2026-10-07T01:00:00Z")]              // 只給 sinceUtc
    [InlineData("symbols=2330&tradeDate=2026-10-07")]                       // 只給 tradeDate
    [InlineData("symbols=2330&tradeDate=2026/10/07&sinceUtc=2026-10-07T01:00:00Z")]
    [InlineData("symbols=2330&tradeDate=2026-13-45&sinceUtc=2026-10-07T01:00:00Z")]
    [InlineData("symbols=2330&tradeDate=2026-10-07&sinceUtc=yesterday")]
    [InlineData("symbols=2330&tradeDate=2026-10-07&sinceUtc=2026-10-07T01:00:00")]   // 沒有 Z
    public async Task IT14_InvalidParameters_Return400(string query)
    {
        var env = Make(); using var f = env.F;
        await SeedInstruments(f, "2330");
        var (c, csrf, _) = await A.SignInAsync(f, "alice", "User");
        await Hold(c, csrf, "2330");
        await AssertProblem(await c.GetAsync("/api/intraday?" + query), HttpStatusCode.BadRequest, "VALIDATION_FAILED");
    }

    [Fact]
    public async Task IT14_FiftyAllowed_FiftyOneRejected()
    {
        var env = Make(); using var f = env.F;
        var symbols = Enumerable.Range(1000, 51).Select(i => i.ToString()).ToArray();
        await SeedInstruments(f, symbols);
        var (c, csrf, _) = await A.SignInAsync(f, "alice", "User");
        await Hold(c, csrf, symbols);
        var ok = await c.GetAsync("/api/intraday?symbols=" + string.Join(',', symbols.Take(50)));
        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
        Assert.Equal(50, (await A.Json(ok)).GetProperty("series").GetArrayLength());
        await AssertProblem(await c.GetAsync("/api/intraday?symbols=" + string.Join(',', symbols)), HttpStatusCode.BadRequest, "VALIDATION_FAILED");
    }

    [Fact] // 去除空白、轉大寫、去重(保留第一次出現的順序)
    public async Task IT14_NormalizesCaseWhitespaceAndDuplicates()
    {
        var env = Make(); using var f = env.F;
        await SeedInstruments(f, "2330", "00632R");
        var (c, csrf, _) = await A.SignInAsync(f, "alice", "User");
        await Hold(c, csrf, "2330", "00632R");
        var r = await c.GetAsync("/api/intraday?symbols=00632r,%202330%20,00632R,2330,,");
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        var series = (await A.Json(r)).GetProperty("series");
        Assert.Equal(["00632R", "2330"], series.EnumerateArray().Select(x => x.GetProperty("symbol").GetString()).ToArray());
        Assert.Equal("名稱00632R", series[0].GetProperty("name").GetString());
    }

    // ---------- IT-15 資料內容、增量、休市日 ----------
    static async Task<(HttpClient C, TestFactory F)> SetupWithSeries(Env env)
    {
        var f = env.F;
        await SeedInstruments(f, "2330", "0050", "6488");
        var (c, csrf, _) = await A.SignInAsync(f, "alice", "User");
        await Hold(c, csrf, "2330", "0050", "6488");
        // 2330:09:00、09:01、09:02、09:10(缺口 8 分鐘)、09:11
        await SeedPoints(f, "2330", Wed, [(Z(7, 1, 0), 100m), (Z(7, 1, 1), 103m), (Z(7, 1, 2), 101m), (Z(7, 1, 10), 99m), (Z(7, 1, 11), 102m)]);
        await SeedQuote(f, "2330", Wed, 100m);
        // 0050:整日 271 個分鐘(09:00 至 13:30),價格逐分鐘 +0.01,最高點在中段會被降採樣略過
        await SeedPoints(f, "0050", Wed, Enumerable.Range(0, 271).Select(i => (Z(7, 1, 0).AddMinutes(i), i == 41 ? 500m : 100m + i * 0.01m)));
        await SeedQuote(f, "0050", Wed.AddDays(-1), 99m);                // 報價日期不是顯示日 → 沒有昨收
        return (c, f);
    }

    [Fact]
    public async Task IT15_FullQuery_ReturnsPointsGapsAxisTrendHighLow()
    {
        var env = Make(); using var _ = env.F;
        var (c, _) = await SetupWithSeries(env);
        var r = await c.GetAsync("/api/intraday?symbols=2330,6488");
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        Assert.Equal("no-store", r.Headers.CacheControl?.ToString());
        var body = await A.Json(r);
        Assert.Equal(Wed10.UtcDateTime, body.GetProperty("asOf").GetDateTime().ToUniversalTime());

        var s = Series(body, "2330");
        Assert.Equal("ok", s.GetProperty("status").GetString());
        Assert.Equal("2026-10-07", s.GetProperty("tradeDate").GetString());
        Assert.True(s.GetProperty("isToday").GetBoolean());
        Assert.False(s.GetProperty("incremental").GetBoolean());
        Assert.Equal("名稱2330", s.GetProperty("name").GetString());
        Assert.Equal(Z(7, 1, 0), s.GetProperty("axis").GetProperty("openUtc").GetDateTime().ToUniversalTime());   // 09:00 Taipei
        Assert.Equal(Z(7, 5, 30), s.GetProperty("axis").GetProperty("closeUtc").GetDateTime().ToUniversalTime()); // 13:30 Taipei
        Assert.Equal(100m, s.GetProperty("prevClose").GetDecimal());
        Assert.Equal(103m, s.GetProperty("high").GetDecimal());
        Assert.Equal(99m, s.GetProperty("low").GetDecimal());
        Assert.Equal(98.8m, s.GetProperty("yMin").GetDecimal());       // 範圍 99–103(昨收 100 在範圍內),邊距 5% × 4 = 0.2
        Assert.Equal(103.2m, s.GetProperty("yMax").GetDecimal());
        Assert.Equal("up", s.GetProperty("trend").GetString());         // 最後 102 > 昨收 100
        var pts = s.GetProperty("points").EnumerateArray().ToList();
        Assert.Equal(5, pts.Count);
        Assert.Equal([false, false, false, true, false], pts.Select(p => p.GetProperty("gap").GetBoolean()).ToArray()); // 09:02 → 09:10 缺口
        Assert.Equal(Z(7, 1, 0), pts[0].GetProperty("t").GetDateTime().ToUniversalTime());
        Assert.Equal(100m, pts[0].GetProperty("p").GetDecimal());

        var empty = Series(body, "6488");                                // 沒有任何點
        Assert.Equal("noData", empty.GetProperty("status").GetString());
        Assert.Equal("2026-10-07", empty.GetProperty("tradeDate").GetString());
        Assert.True(empty.GetProperty("isToday").GetBoolean());
        Assert.Equal(0, empty.GetProperty("points").GetArrayLength());
        Assert.Equal(JsonValueKind.Null, empty.GetProperty("trend").ValueKind);
        Assert.Equal(JsonValueKind.Null, empty.GetProperty("prevClose").ValueKind);
        Assert.Equal(JsonValueKind.Null, empty.GetProperty("yMin").ValueKind);
        Assert.NotEqual(JsonValueKind.Null, empty.GetProperty("axis").ValueKind);
    }

    [Fact] // 昨收只在 Quotes.TradeDate = 顯示日時才有;沒有時趨勢改比第一點,Y 軸不含昨收
    public async Task IT15_PrevClose_OnlyWhenQuoteDateMatches()
    {
        var env = Make(); using var _ = env.F;
        var (c, _) = await SetupWithSeries(env);
        var s = Series(await A.Json(await c.GetAsync("/api/intraday?symbols=0050")), "0050");
        Assert.Equal(JsonValueKind.Null, s.GetProperty("prevClose").ValueKind);
        Assert.Equal(500m, s.GetProperty("high").GetDecimal());
    }

    [Fact] // 降採樣:271 點 → step 4、≤ 90、含最後一點;high/low 取降採樣前;缺口依原始序列
    public async Task IT15_FullQuery_DownsamplesTo90_ButHighLowFromRawData()
    {
        var env = Make(); using var _ = env.F;
        var (c, _) = await SetupWithSeries(env);
        var s = Series(await A.Json(await c.GetAsync("/api/intraday?symbols=0050")), "0050");
        var pts = s.GetProperty("points").EnumerateArray().ToList();
        Assert.Equal(68, pts.Count);
        Assert.Equal(Z(7, 5, 30), pts[^1].GetProperty("t").GetDateTime().ToUniversalTime());
        Assert.DoesNotContain(pts, p => p.GetProperty("p").GetDecimal() == 500m);    // 索引 41 被略過
        Assert.Equal(500m, s.GetProperty("high").GetDecimal());                      // 但 high 仍是 500
        Assert.Equal(100m, s.GetProperty("low").GetDecimal());
    }

    [Fact] // 增量:tradeDate 相符 + sinceUtc → incremental = true,只含之後的點;gap、yMin/yMax、high/low 為整日值
    public async Task IT15_Incremental_OnlyNewPoints_WithWholeDayAxisAndGapAgainstSince()
    {
        var env = Make(); using var _ = env.F;
        var (c, _) = await SetupWithSeries(env);
        var url = $"/api/intraday?symbols=2330&tradeDate=2026-10-07&sinceUtc={Iso(Z(7, 1, 2))}";
        var s = Series(await A.Json(await c.GetAsync(url)), "2330");
        Assert.True(s.GetProperty("incremental").GetBoolean());
        Assert.Equal("ok", s.GetProperty("status").GetString());
        var pts = s.GetProperty("points").EnumerateArray().ToList();
        Assert.Equal([Z(7, 1, 10), Z(7, 1, 11)], pts.Select(p => p.GetProperty("t").GetDateTime().ToUniversalTime()).ToArray());
        Assert.True(pts[0].GetProperty("gap").GetBoolean());                         // 與 sinceUtc(09:02)相隔 8 分鐘
        Assert.False(pts[1].GetProperty("gap").GetBoolean());
        Assert.Equal(103m, s.GetProperty("high").GetDecimal());                      // 整日值,不是只看新點
        Assert.Equal(99m, s.GetProperty("low").GetDecimal());
        Assert.Equal(98.8m, s.GetProperty("yMin").GetDecimal());
        Assert.Equal(103.2m, s.GetProperty("yMax").GetDecimal());
        Assert.Equal("up", s.GetProperty("trend").GetString());

        // 緊接著的下一分鐘(180 秒內)不是缺口;沒有新點時 points 為空但 status 仍為 ok
        var near = Series(await A.Json(await c.GetAsync($"/api/intraday?symbols=2330&tradeDate=2026-10-07&sinceUtc={Iso(Z(7, 1, 8))}")), "2330");
        Assert.False(near.GetProperty("points")[0].GetProperty("gap").GetBoolean());      // 09:08 → 09:10 只差 2 分鐘
        var none = Series(await A.Json(await c.GetAsync($"/api/intraday?symbols=2330&tradeDate=2026-10-07&sinceUtc={Iso(Z(7, 1, 11))}")), "2330");
        Assert.True(none.GetProperty("incremental").GetBoolean());
        Assert.Equal("ok", none.GetProperty("status").GetString());
        Assert.Equal(0, none.GetProperty("points").GetArrayLength());
    }

    [Fact] // tradeDate 不符 → 忽略 sinceUtc 回完整資料(incremental = false);增量不降採樣
    public async Task IT15_TradeDateMismatch_ReturnsFullData()
    {
        var env = Make(); using var _ = env.F;
        var (c, _) = await SetupWithSeries(env);
        var s = Series(await A.Json(await c.GetAsync($"/api/intraday?symbols=2330&tradeDate=2026-10-06&sinceUtc={Iso(Z(7, 1, 10))}")), "2330");
        Assert.False(s.GetProperty("incremental").GetBoolean());
        Assert.Equal(5, s.GetProperty("points").GetArrayLength());
        Assert.Equal("2026-10-07", s.GetProperty("tradeDate").GetString());

        var inc = Series(await A.Json(await c.GetAsync($"/api/intraday?symbols=0050&tradeDate=2026-10-07&sinceUtc={Iso(Z(7, 1, 0))}")), "0050");
        Assert.True(inc.GetProperty("incremental").GetBoolean());
        Assert.Equal(270, inc.GetProperty("points").GetArrayLength());               // 增量不降採樣
    }

    [Fact] // 休市日:顯示最近一筆紀錄的交易日、isToday = false;超過保留期或沒有資料 → noData、tradeDate = null、axis = null
    public async Task IT15_Holiday_ShowsLatestTradingDay_NotToday()
    {
        var env = Make(Sat10, MarketState.Holiday); using var f = env.F;
        await SeedInstruments(f, "2330", "0050", "6488");
        var (c, csrf, _) = await A.SignInAsync(f, "alice", "User");
        await Hold(c, csrf, "2330", "0050", "6488");
        var thu = new DateOnly(2026, 10, 8);
        await SeedPoints(f, "2330", thu, [(Z(8, 1, 0), 100m), (Z(8, 1, 1), 101m)]);
        await SeedPoints(f, "2330", Wed, [(Z(7, 1, 0), 90m)]);                      // 較早的交易日不採用
        await SeedQuote(f, "2330", thu, 99m);
        await SeedPoints(f, "0050", new DateOnly(2026, 10, 1), [(Z(1, 1, 0), 100m)]); // 9 天前:超過保留期

        var body = await A.Json(await c.GetAsync("/api/intraday?symbols=2330,0050,6488"));
        var s = Series(body, "2330");
        Assert.Equal("ok", s.GetProperty("status").GetString());
        Assert.Equal("2026-10-08", s.GetProperty("tradeDate").GetString());
        Assert.False(s.GetProperty("isToday").GetBoolean());
        Assert.Equal(2, s.GetProperty("points").GetArrayLength());
        Assert.Equal(99m, s.GetProperty("prevClose").GetDecimal());
        Assert.Equal(Z(8, 1, 0), s.GetProperty("axis").GetProperty("openUtc").GetDateTime().ToUniversalTime());
        foreach (var sym in new[] { "0050", "6488" })
        {
            var n = Series(body, sym);
            Assert.Equal("noData", n.GetProperty("status").GetString());
            Assert.Equal(JsonValueKind.Null, n.GetProperty("tradeDate").ValueKind);
            Assert.Equal(JsonValueKind.Null, n.GetProperty("axis").ValueKind);
            Assert.False(n.GetProperty("isToday").GetBoolean());
        }
    }

    [Fact] // 盤後(Closed)仍顯示今日整天的線
    public async Task IT15_AfterClose_StillShowsToday()
    {
        var env = Make(new DateTimeOffset(2026, 10, 7, 8, 0, 0, TimeSpan.Zero), MarketState.Closed); using var f = env.F;   // 16:00 Taipei
        await SeedInstruments(f, "2330");
        var (c, csrf, _) = await A.SignInAsync(f, "alice", "User");
        await Hold(c, csrf, "2330");
        await SeedPoints(f, "2330", Wed, [(Z(7, 1, 0), 100m), (Z(7, 5, 30), 101m)]);
        var s = Series(await A.Json(await c.GetAsync("/api/intraday?symbols=2330")), "2330");
        Assert.True(s.GetProperty("isToday").GetBoolean());
        Assert.Equal(2, s.GetProperty("points").GetArrayLength());
        Assert.Equal("up", s.GetProperty("trend").GetString());                      // 無昨收:最後一點與第一點比較
    }

    // ---------- IT-16 保留清理 ----------
    [Fact]
    public async Task IT16_Retention_DeletesOlderThan7Days_KeepsWithin_LeavesOtherTablesAlone()
    {
        var env = Make(); using var f = env.F;                                       // 今日 2026-10-07
        await SeedInstruments(f, "2330");
        var (c, csrf, _) = await A.SignInAsync(f, "alice", "User");
        await Hold(c, csrf, "2330");
        await SeedQuote(f, "2330", Wed, 100m);
        await SeedPoints(f, "2330", Wed.AddDays(-8), [(Z(7, 1, 0).AddDays(-8), 1m)]);   // 8 天前 → 刪
        await SeedPoints(f, "2330", Wed.AddDays(-30), [(Z(7, 1, 0).AddDays(-30), 1m)]); // 30 天前 → 刪
        await SeedPoints(f, "2330", Wed.AddDays(-7), [(Z(7, 1, 0).AddDays(-7), 1m)]);   // 剛好 7 天 → 留
        await SeedPoints(f, "2330", Wed.AddDays(-1), [(Z(7, 1, 0).AddDays(-1), 1m)]);
        await SeedPoints(f, "2330", Wed, [(Z(7, 1, 0), 1m)]);

        var job = f.Services.GetRequiredService<IntradayRetention>();
        Assert.Equal(2, await job.PurgeAsync(default));

        using var s = f.Services.CreateScope();
        var db = s.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.Equal([Wed.AddDays(-7), Wed.AddDays(-1), Wed], db.QuoteIntraday.OrderBy(x => x.TradeDate).Select(x => x.TradeDate).ToArray());
        Assert.Equal(1, db.Quotes.Count());
        Assert.Equal(1, db.Holdings.Count());
        Assert.Equal(1, db.Instruments.Count());
    }

    [Fact] // Intraday:RetentionDays 可調;停用時不清理
    public async Task IT16_Retention_HonorsRetentionDays_AndDisabled()
    {
        var env = Make(settings: new() { ["Intraday:RetentionDays"] = "2" }); using var f = env.F;
        await SeedInstruments(f, "2330");
        await SeedPoints(f, "2330", Wed.AddDays(-3), [(Z(7, 1, 0).AddDays(-3), 1m)]);
        await SeedPoints(f, "2330", Wed.AddDays(-2), [(Z(7, 1, 0).AddDays(-2), 1m)]);
        Assert.Equal(1, await f.Services.GetRequiredService<IntradayRetention>().PurgeAsync(default));

        var off = Make(settings: new() { ["Intraday:Enabled"] = "false" }); using var f2 = off.F;
        await SeedInstruments(f2, "2330");
        await SeedPoints(f2, "2330", Wed.AddDays(-30), [(Z(7, 1, 0).AddDays(-30), 1m)]);
        Assert.Equal(0, await f2.Services.GetRequiredService<IntradayRetention>().PurgeAsync(default));
    }

    // ---------- IT-17 功能開關、資料來源例外、限流 ----------
    [Fact]
    public async Task IT17_Disabled_Returns503FeatureDisabled_AndHoldingsStillOk()
    {
        var env = Make(settings: new() { ["Intraday:Enabled"] = "false" }); using var f = env.F;
        await SeedInstruments(f, "2330");
        var (c, csrf, _) = await A.SignInAsync(f, "alice", "User");
        await Hold(c, csrf, "2330");
        var r = await c.GetAsync("/api/intraday?symbols=2330");
        await AssertProblem(r, HttpStatusCode.ServiceUnavailable, "FEATURE_DISABLED");
        Assert.Equal("no-store", r.Headers.CacheControl?.ToString());
        Assert.Equal(HttpStatusCode.OK, (await c.GetAsync("/api/holdings")).StatusCode);
    }

    [Fact]
    public async Task IT17_DataSourceException_Returns500Problem_HoldingsStillOk_NoDetailsLeaked()
    {
        var sink = new CaptureSink();
        var env = Make(more: sv =>
        {
            sv.AddSingleton<Microsoft.Extensions.Logging.ILoggerFactory>(new CaptureLoggerFactory(sink));
            foreach (var d in sv.Where(d => d.ServiceType == typeof(IIntradayService)).ToList()) sv.Remove(d);
            sv.AddScoped<IIntradayService, ThrowingService>();
        });
        using var f = env.F;
        await SeedInstruments(f, "2330");
        var (c, csrf, _) = await A.SignInAsync(f, "alice", "User");
        await Hold(c, csrf, "2330");
        var r = await c.GetAsync("/api/intraday?symbols=2330");
        await AssertProblem(r, HttpStatusCode.InternalServerError, "INTERNAL_ERROR");
        Assert.DoesNotContain("QuoteIntraday", await r.Content.ReadAsStringAsync());   // 細節只寫日誌,不回傳
        Assert.Equal(HttpStatusCode.OK, (await c.GetAsync("/api/holdings")).StatusCode);

        // 日誌記錄了失敗,但不含代號
        var logs = sink.All();
        Assert.Contains("API-15", logs);
        // 應用程式自己的日誌不得含代號。(Microsoft.AspNetCore 的請求日誌在正式環境由 Serilog 壓到 Warning,測試用的擷取器不會套用該覆寫)
        foreach (var line in logs.Split('\n').Where(l => !l.TrimStart().StartsWith("[Microsoft.AspNetCore.")))
            Assert.DoesNotContain("2330", line);
    }

    [Fact]
    public async Task IT17_RateLimit_429WithRetryAfter_PerUser_ResetsNextMinute()
    {
        var env = Make(settings: new() { ["Intraday:MaxRequestsPerMinute"] = "3" }); using var f = env.F;
        await SeedInstruments(f, "2330");
        var (alice, aCsrf, _) = await A.SignInAsync(f, "alice", "User");
        var (bob, bCsrf, _) = await A.SignInAsync(f, "bob", "User");
        await Hold(alice, aCsrf, "2330");
        await Hold(bob, bCsrf, "2330");

        for (var i = 0; i < 3; i++) Assert.Equal(HttpStatusCode.OK, (await alice.GetAsync("/api/intraday?symbols=2330")).StatusCode);
        var limited = await alice.GetAsync("/api/intraday?symbols=2330");
        await AssertProblem(limited, HttpStatusCode.TooManyRequests, "RATE_LIMITED");
        var retry = int.Parse(limited.Headers.GetValues("Retry-After").Single());
        Assert.InRange(retry, 1, 60);
        Assert.Equal(HttpStatusCode.OK, (await alice.GetAsync("/api/holdings")).StatusCode);          // 只限制走勢 API
        Assert.Equal(HttpStatusCode.OK, (await bob.GetAsync("/api/intraday?symbols=2330")).StatusCode); // 以使用者分區

        env.Clock.Advance(TimeSpan.FromSeconds(retry));                                                // 下一分鐘恢復
        Assert.Equal(HttpStatusCode.OK, (await alice.GetAsync("/api/intraday?symbols=2330")).StatusCode);
    }

    [Fact] // 驗證失敗的請求也消耗額度(避免用錯誤請求繞過限制)
    public async Task IT17_InvalidRequestsCountTowardsRateLimit()
    {
        var env = Make(settings: new() { ["Intraday:MaxRequestsPerMinute"] = "2" }); using var f = env.F;
        var (c, _, _) = await A.SignInAsync(f, "alice", "User");
        Assert.Equal(HttpStatusCode.BadRequest, (await c.GetAsync("/api/intraday")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await c.GetAsync("/api/intraday")).StatusCode);
        Assert.Equal(HttpStatusCode.TooManyRequests, (await c.GetAsync("/api/intraday")).StatusCode);
    }

    [Fact] // 設定值超出範圍時回報設定錯誤並改用預設(網站仍可啟動)
    public void InvalidIntradaySettings_FallBackToDefaults()
    {
        var env = Make(settings: new() { ["Intraday:MaxRequestsPerMinute"] = "0", ["Intraday:MaxPoints"] = "1" }); using var f = env.F;
        Assert.Equal(30, f.Services.GetRequiredService<IntradayConfig>().Value.MaxRequestsPerMinute);
        Assert.Equal(90, f.Services.GetRequiredService<IntradayConfig>().Value.MaxPoints);
        Assert.Equal(7, f.Services.GetRequiredService<IOptions<IntradayOptions>>().Value.RetentionDays);   // 預設值由 appsettings 提供
    }

    // ---------- 前端檔案的 CSP 守門(自動化瀏覽器測試是非目標,這裡只做靜態檢查) ----------
    [Fact]
    public async Task FrontendScript_DoesNotUseInlineStyleOrHtmlInjection()
    {
        var env = Make(); using var f = env.F;
        var js = await LoginFlowTests.NewClient(f).GetStringAsync("/js/intraday.js");
        Assert.NotEmpty(js);
        Assert.DoesNotMatch(new Regex(@"\.style\b|setAttribute\(\s*['""]style|cssText|innerHTML|outerHTML|insertAdjacentHTML|v-html|\beval\(|new Function|document\.write"), js);
        Assert.DoesNotMatch(new Regex(@"https?://(?!www\.w3\.org/2000/svg)"), js);     // 不載入外部資源(只允許 SVG 命名空間字串)
    }

    [Fact]
    public async Task IndexPage_LoadsIntradayScriptBeforeOverview()
    {
        var env = Make(); using var f = env.F;
        var (c, _, _) = await A.SignInAsync(f, "alice", "User");
        var html = await c.GetStringAsync("/");
        var i = html.IndexOf("/js/intraday.js", StringComparison.Ordinal);
        var o = html.IndexOf("/js/overview.js", StringComparison.Ordinal);
        Assert.True(i > 0 && o > i);
    }
}
