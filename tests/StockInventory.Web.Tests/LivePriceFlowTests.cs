using System.Collections.Concurrent;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using StockInventory.Core;
using StockInventory.Data;
using StockInventory.Quotes;
using StockInventory.Web.Services;
using Xunit;
using A = StockInventory.Web.Tests.AdminApiTests;

namespace StockInventory.Web.Tests;

/// <summary>端對端:真正的 QuoteFetcher + SignalR Hub + 假 MIS(每次呼叫價格 +0.05),驗證盤中畫面資料每 5 秒更新。</summary>
public class LivePriceFlowTests
{
    // 2026-10-07 週三 10:00 台北時間(盤中)
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

    static (TestFactory F, FakeTimeProvider Clock, RisingMis Mis) Make()
    {
        var clock = new FakeTimeProvider(Wed10);
        var mis = new RisingMis();
        var f = new TestFactory(new()
        {
            ["Quote:Enabled"] = "true",
            ["Quote:ActiveIntervalSeconds"] = "5",
        }, sv =>
        {
            foreach (var d in sv.Where(d => d.ServiceType == typeof(TimeProvider) || d.ServiceType == typeof(IMisClient)).ToList()) sv.Remove(d);
            sv.AddSingleton<TimeProvider>(clock);
            sv.AddSingleton<IMisClient>(mis);
        });
        return (f, clock, mis);
    }

    static async Task SeedHolding(TestFactory f, HttpClient c, string csrf)
    {
        using (var s = f.Services.CreateScope())
        {
            var db = s.ServiceProvider.GetRequiredService<AppDbContext>();
            db.Instruments.Add(new Instrument { Symbol = "0050", Name = "元大台灣50", Market = Market.Twse, Kind = InstrumentKind.Etf, UpdatedAtUtc = DateTime.UtcNow });
            await db.SaveChangesAsync();
        }
        var pid = (await A.Json(await c.SendAsync(A.Req(HttpMethod.Post, "/api/portfolios", csrf, new { name = "P" })))).GetProperty("id").GetInt32();
        await c.SendAsync(A.Req(HttpMethod.Post, $"/api/portfolios/{pid}/holdings", csrf, new { symbol = "0050", totalCost = 100000, shares = 1000 }));
    }

    /// <summary>每隔一小段真實時間,把假時鐘推進 1 秒,讓 PeriodicTimer 與抓價輪次運轉(約 20 倍速)。</summary>
    static async Task Drive(FakeTimeProvider clock, int fakeSeconds)
    {
        for (var i = 0; i < fakeSeconds; i++) { clock.Advance(TimeSpan.FromSeconds(1)); await Task.Delay(40); }
    }

    [Fact] // 有 SignalR 連線:每 5 秒抓一次,每次都推播新價格
    public async Task OpenPageWithSignalR_ReceivesNewPriceEveryFiveSeconds()
    {
        var (f, clock, mis) = Make();
        using var _ = f;
        var h = new CookieHandler(f.Server.CreateHandler());
        var http = new HttpClient(h) { BaseAddress = new Uri("https://localhost") };
        var (_, csrf, _) = await A.SignInAsync(f, "alice", http, "User");
        await SeedHolding(f, http, csrf);

        var prices = new ConcurrentQueue<decimal?>();
        var conn = new HubConnectionBuilder().WithUrl(new Uri("https://localhost/hubs/view"), o =>
        {
            o.HttpMessageHandlerFactory = _ => h;
            o.Transports = HttpTransportType.LongPolling;
        }).Build();
        conn.On<ViewDto>("ViewUpdated", v => prices.Enqueue(v.Rows.FirstOrDefault()?.LastPrice));
        await conn.StartAsync();
        await conn.InvokeAsync("SetView", (int[]?)null, true);

        await Drive(clock, 40); // 假時鐘 40 秒 ≈ 8 輪
        var distinct = prices.Where(p => p is not null).Distinct().Count();
        Assert.True(distinct >= 5, $"收到 {prices.Count} 次推播,只有 {distinct} 種不同價格;抓價呼叫 {mis.Calls} 次");
        await conn.StopAsync();
    }

    [Fact] // 沒有 SignalR 連線、只靠頁面每 5 秒輪詢 API-06(連線失敗時的備援):價格也必須持續更新
    public async Task PollingOnly_StillGetsFreshPricesEveryFiveSeconds()
    {
        var (f, clock, mis) = Make();
        using var _ = f;
        var http = new HttpClient(new CookieHandler(f.Server.CreateHandler())) { BaseAddress = new Uri("https://localhost") };
        var (_, csrf, _) = await A.SignInAsync(f, "alice", http, "User");
        await SeedHolding(f, http, csrf);

        var seen = new HashSet<decimal?>();
        for (var round = 0; round < 8; round++)
        {
            var v = await A.Json(await http.GetAsync("/api/holdings"));
            seen.Add(v.GetProperty("rows")[0].GetProperty("lastPrice").ValueKind == System.Text.Json.JsonValueKind.Null
                ? null : v.GetProperty("rows")[0].GetProperty("lastPrice").GetDecimal());
            await Drive(clock, 5);
        }
        Assert.True(seen.Count(p => p is not null) >= 5, $"輪詢 8 次只看到 {seen.Count(p => p is not null)} 種價格;抓價呼叫 {mis.Calls} 次(無連線時退化為每 300 秒一次)");
    }
}
