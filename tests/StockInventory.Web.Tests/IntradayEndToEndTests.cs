using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using StockInventory.Core;
using StockInventory.Data;
using StockInventory.Quotes;
using Xunit;
using A = StockInventory.Web.Tests.AdminApiTests;

namespace StockInventory.Web.Tests;

/// <summary>
/// 端對端(FR-26):真正的 QuoteFetcher + IntradayRecorder + API-15,假 MIS、假時鐘。
/// 驗證 DI 接線:盤中沒有任何連線,仍每 30 秒抓價、每分鐘 flush 到 QuoteIntraday,API-15 讀得到。
/// </summary>
public class IntradayEndToEndTests
{
    // 2026-10-07 週三 10:00:00 台北時間(盤中),剛好在分鐘邊界
    static readonly DateTimeOffset Wed10 = new(2026, 10, 7, 2, 0, 0, TimeSpan.Zero);

    sealed class RisingMis : IMisClient
    {
        int _n;
        public int Calls => _n;
        public Task<string> GetAsync(string exCh, CancellationToken ct)
        {
            var price = 100m + Interlocked.Increment(ref _n) * 0.05m;
            var symbols = exCh.Split('|').Select(x => x.Split('_')[1].Replace(".tw", ""));
            var rows = string.Join(',', symbols.Select(s =>
                $"{{\"c\":\"{s}\",\"z\":\"{price:F4}\",\"y\":\"100.0000\",\"a\":\"-\",\"b\":\"-\",\"tlong\":\"{Wed10.ToUnixTimeMilliseconds()}\"}}"));
            return Task.FromResult($"{{\"msgArray\":[{rows}]}}");
        }
    }

    static async Task Drive(FakeTimeProvider clock, int fakeSeconds)
    {
        for (var i = 0; i < fakeSeconds; i++) { clock.Advance(TimeSpan.FromSeconds(1)); await Task.Delay(30); }
    }

    [Fact]
    public async Task OpenMarket_NobodyWatching_RecordsEveryMinute_AndApiReturnsTheLine()
    {
        var clock = new FakeTimeProvider(Wed10);
        var mis = new RisingMis();
        using var f = new TestFactory(new() { ["Quote:Enabled"] = "true" }, sv =>
        {
            foreach (var d in sv.Where(d => d.ServiceType == typeof(TimeProvider) || d.ServiceType == typeof(IMisClient)).ToList()) sv.Remove(d);
            sv.AddSingleton<TimeProvider>(clock);
            sv.AddSingleton<IMisClient>(mis);
        });
        var http = new HttpClient(new CookieHandler(f.Server.CreateHandler())) { BaseAddress = new Uri("https://localhost") };
        var (_, csrf, _) = await A.SignInAsync(f, "alice", http, "User");
        using (var s = f.Services.CreateScope())
        {
            var db = s.ServiceProvider.GetRequiredService<AppDbContext>();
            db.Instruments.Add(new Instrument { Symbol = "0050", Name = "元大台灣50", Market = Market.Twse, Kind = InstrumentKind.Etf, UpdatedAtUtc = DateTime.UtcNow });
            await db.SaveChangesAsync();
        }
        var pid = (await A.Json(await http.SendAsync(A.Req(HttpMethod.Post, "/api/portfolios", csrf, new { name = "P" })))).GetProperty("id").GetInt32();
        await http.SendAsync(A.Req(HttpMethod.Post, $"/api/portfolios/{pid}/holdings", csrf, new { symbol = "0050", totalCost = 1000, shares = 10 }));

        // 之後完全不呼叫 /api/holdings、不連 SignalR:沒有人在看
        await Drive(clock, 190);

        Assert.InRange(mis.Calls, 4, 9);                                  // 約每 30 秒一次(新增持股的立即抓取多一次),不是每 5 秒(約 38 次)
        var body = await A.Json(await http.GetAsync("/api/intraday?symbols=0050"));
        var s0 = body.GetProperty("series")[0];
        Assert.Equal("ok", s0.GetProperty("status").GetString());
        var pts = s0.GetProperty("points").EnumerateArray().ToList();
        Assert.InRange(pts.Count, 2, 4);                                  // 10:00、10:01、10:02(已 flush 的完整分鐘)
        Assert.All(pts, p => Assert.False(p.GetProperty("gap").GetBoolean()));   // 每 30 秒抓價 → 連續,沒有缺口
        Assert.Equal(pts.Select(p => p.GetProperty("t").GetDateTime()).OrderBy(t => t), pts.Select(p => p.GetProperty("t").GetDateTime()));
        Assert.Equal("up", s0.GetProperty("trend").GetString());          // 假 MIS 價格逐次上升,昨收 100,最新 > 100

        var stale = await A.Json(await http.GetAsync("/health"));          // 沒人看也不誤報報價中斷
        Assert.False(stale.GetProperty("quotes").GetProperty("stale").GetBoolean());
    }
}
