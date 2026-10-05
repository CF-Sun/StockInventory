using System.Net;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using StockInventory.Core;
using StockInventory.Data;
using Xunit;
using A = StockInventory.Web.Tests.AdminApiTests;

namespace StockInventory.Web.Tests;

public class PortfolioApiTests
{
    static async Task SeedInstruments(TestFactory f, int extra = 0)
    {
        using var s = f.Services.CreateScope();
        var db = s.ServiceProvider.GetRequiredService<AppDbContext>();
        db.Instruments.AddRange(
            new Instrument { Symbol = "0050", Name = "元大台灣50", Market = Market.Twse, Kind = InstrumentKind.Etf, UpdatedAtUtc = DateTime.UtcNow },
            new Instrument { Symbol = "2330", Name = "台積電", Market = Market.Twse, Kind = InstrumentKind.Stock, UpdatedAtUtc = DateTime.UtcNow },
            new Instrument { Symbol = "9999", Name = "已下市", Market = Market.Twse, Kind = InstrumentKind.Stock, IsActive = false, UpdatedAtUtc = DateTime.UtcNow });
        for (var i = 0; i < extra; i++)
            db.Instruments.Add(new Instrument { Symbol = $"T{i:D4}", Name = $"測試{i}", Market = Market.Tpex, Kind = InstrumentKind.Stock, UpdatedAtUtc = DateTime.UtcNow });
        await db.SaveChangesAsync();
    }

    static Task<HttpResponseMessage> Send(HttpClient c, string csrf, HttpMethod m, string url, object? body = null) =>
        c.SendAsync(A.Req(m, url, csrf, body));

    static async Task<int> NewPortfolio(HttpClient c, string csrf, string name)
    {
        var r = await Send(c, csrf, HttpMethod.Post, "/api/portfolios", new { name });
        Assert.Equal(HttpStatusCode.Created, r.StatusCode);
        return (await A.Json(r)).GetProperty("id").GetInt32();
    }

    static async Task<int> NewHolding(HttpClient c, string csrf, int pid, string symbol, long cost = 1000, long shares = 10)
    {
        var r = await Send(c, csrf, HttpMethod.Post, $"/api/portfolios/{pid}/holdings", new { symbol, totalCost = cost, shares });
        Assert.Equal(HttpStatusCode.Created, r.StatusCode);
        return (await A.Json(r)).GetProperty("holdingId").GetInt32();
    }

    [Fact] // IT-01
    public async Task DataIsolation_OtherUsersResources_Return404()
    {
        using var f = new TestFactory();
        await SeedInstruments(f);
        var (a, ca, _) = await A.SignInAsync(f, "alice", "User");
        var (b, cb, _) = await A.SignInAsync(f, "bob", "User");
        var pid = await NewPortfolio(a, ca, "Alice");
        var hid = await NewHolding(a, ca, pid, "0050");

        Assert.Equal(HttpStatusCode.NotFound, (await Send(b, cb, HttpMethod.Put, $"/api/portfolios/{pid}", new { name = "x" })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await Send(b, cb, HttpMethod.Delete, $"/api/portfolios/{pid}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await Send(b, cb, HttpMethod.Post, $"/api/portfolios/{pid}/holdings", new { symbol = "2330", totalCost = 1, shares = 1 })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await Send(b, cb, HttpMethod.Put, $"/api/holdings/{hid}", new { totalCost = 5, shares = 5 })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await Send(b, cb, HttpMethod.Delete, $"/api/holdings/{hid}")).StatusCode);

        Assert.Equal(0, (await A.Json(await b.GetAsync("/api/portfolios"))).GetArrayLength());
        var mine = await A.Json(await a.GetAsync("/api/portfolios"));
        Assert.Equal("Alice", mine[0].GetProperty("name").GetString());
        Assert.Equal(1, mine[0].GetProperty("holdingCount").GetInt32());
    }

    [Fact] // IT-02
    public async Task Duplicates_Return409()
    {
        using var f = new TestFactory();
        await SeedInstruments(f);
        var (c, csrf, _) = await A.SignInAsync(f, "alice", "User");
        var p1 = await NewPortfolio(c, csrf, "長期");
        var p2 = await NewPortfolio(c, csrf, "短期");
        Assert.Equal(HttpStatusCode.Conflict, (await Send(c, csrf, HttpMethod.Post, "/api/portfolios", new { name = " 長期 " })).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await Send(c, csrf, HttpMethod.Put, $"/api/portfolios/{p2}", new { name = "長期" })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await Send(c, csrf, HttpMethod.Put, $"/api/portfolios/{p2}", new { name = "短期" })).StatusCode); // 改成自己的名字不算重複

        await NewHolding(c, csrf, p1, "0050");
        var dup = await Send(c, csrf, HttpMethod.Post, $"/api/portfolios/{p1}/holdings", new { symbol = " 0050 ", totalCost = 1, shares = 1 });
        Assert.Equal(HttpStatusCode.Conflict, dup.StatusCode);
        Assert.Equal("DUPLICATE", (await A.Json(dup)).GetProperty("code").GetString());
        await NewHolding(c, csrf, p2, "0050"); // 跨庫存可重複
    }

    [Fact] // IT-03
    public async Task Limits_Return422()
    {
        using var f = new TestFactory();
        await SeedInstruments(f, extra: 201);
        var (c, csrf, uid) = await A.SignInAsync(f, "alice", "User");
        for (var i = 1; i <= 20; i++) await NewPortfolio(c, csrf, $"P{i}");
        var over = await Send(c, csrf, HttpMethod.Post, "/api/portfolios", new { name = "P21" });
        Assert.Equal(HttpStatusCode.UnprocessableEntity, over.StatusCode);
        Assert.Equal("LIMIT_EXCEEDED", (await A.Json(over)).GetProperty("code").GetString());

        int pid;
        using (var s = f.Services.CreateScope())
        {
            var db = s.ServiceProvider.GetRequiredService<AppDbContext>();
            pid = await db.Portfolios.Where(p => p.UserId == uid).Select(p => p.PortfolioId).FirstAsync();
            for (var i = 0; i < 200; i++)
                db.Holdings.Add(new Holding { PortfolioId = pid, Symbol = $"T{i:D4}", TotalCost = 1, Shares = 1, CreatedAtUtc = DateTime.UtcNow, UpdatedAtUtc = DateTime.UtcNow });
            await db.SaveChangesAsync();
        }
        var r = await Send(c, csrf, HttpMethod.Post, $"/api/portfolios/{pid}/holdings", new { symbol = "T0200", totalCost = 1, shares = 1 });
        Assert.Equal(HttpStatusCode.UnprocessableEntity, r.StatusCode);
    }

    [Fact] // IT-04
    public async Task DeletePortfolio_CascadesHoldings_WritesD_AndCleansSelection()
    {
        using var f = new TestFactory();
        await SeedInstruments(f);
        var (c, csrf, uid) = await A.SignInAsync(f, "alice", "User");
        var p1 = await NewPortfolio(c, csrf, "A");
        var p2 = await NewPortfolio(c, csrf, "B");
        await NewHolding(c, csrf, p1, "0050");
        await NewHolding(c, csrf, p1, "2330");
        using (var s = f.Services.CreateScope())
        {
            var db = s.ServiceProvider.GetRequiredService<AppDbContext>();
            db.UserSettings.Add(new UserSettings { UserId = uid, SelectedPortfolioIds = $"{p1},{p2}", UpdatedAtUtc = DateTime.UtcNow });
            await db.SaveChangesAsync();
        }

        Assert.Equal(HttpStatusCode.NoContent, (await Send(c, csrf, HttpMethod.Delete, $"/api/portfolios/{p1}")).StatusCode);

        using var s2 = f.Services.CreateScope();
        var db2 = s2.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.Empty(await db2.Holdings.Where(h => h.PortfolioId == p1).ToListAsync());
        var ds = await db2.HoldingChanges.Where(x => x.Action == 'D' && x.PortfolioId == p1).ToListAsync();
        Assert.Equal(2, ds.Count);
        Assert.All(ds, d => { Assert.Equal("A", d.PortfolioName); Assert.Null(d.NewShares); Assert.NotNull(d.OldShares); });
        Assert.Equal($"{p2}", (await db2.UserSettings.FindAsync(uid))!.SelectedPortfolioIds);

        await Send(c, csrf, HttpMethod.Delete, $"/api/portfolios/{p2}");
        using var s3 = f.Services.CreateScope();
        Assert.Null((await s3.ServiceProvider.GetRequiredService<AppDbContext>().UserSettings.FindAsync(uid))!.SelectedPortfolioIds);
    }

    [Fact] // IT-05
    public async Task HoldingChanges_A_U_D_WithCorrectValues_AndNoLogWhenUnchanged()
    {
        using var f = new TestFactory();
        await SeedInstruments(f);
        var (c, csrf, uid) = await A.SignInAsync(f, "alice", "User");
        var pid = await NewPortfolio(c, csrf, "A");
        var hid = await NewHolding(c, csrf, pid, "0050", 1000, 10);

        Assert.Equal(HttpStatusCode.OK, (await Send(c, csrf, HttpMethod.Put, $"/api/holdings/{hid}", new { totalCost = 1000, shares = 10 })).StatusCode); // 未變
        Assert.Equal(HttpStatusCode.OK, (await Send(c, csrf, HttpMethod.Put, $"/api/holdings/{hid}", new { totalCost = 2000, shares = 20 })).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await Send(c, csrf, HttpMethod.Delete, $"/api/holdings/{hid}")).StatusCode);

        using var s = f.Services.CreateScope();
        var all = await s.ServiceProvider.GetRequiredService<AppDbContext>().HoldingChanges
            .Where(x => x.UserId == uid).OrderBy(x => x.ChangeId).ToListAsync();
        Assert.Equal("AUD", new string(all.Select(x => x.Action).ToArray()));
        Assert.Equal((null, null, 1000L, 10L), (all[0].OldTotalCost, all[0].OldShares, all[0].NewTotalCost, all[0].NewShares));
        Assert.Equal((1000L, 10L, 2000L, 20L), (all[1].OldTotalCost, all[1].OldShares, all[1].NewTotalCost, all[1].NewShares));
        Assert.Equal((2000L, 20L, null, null), (all[2].OldTotalCost, all[2].OldShares, all[2].NewTotalCost, all[2].NewShares));
    }

    [Fact]
    public async Task AddHolding_Validation_UnknownOrInactiveSymbol_BadNumbers()
    {
        using var f = new TestFactory();
        await SeedInstruments(f);
        var (c, csrf, _) = await A.SignInAsync(f, "alice", "User");
        var pid = await NewPortfolio(c, csrf, "A");
        foreach (var body in new object[]
        {
            new { symbol = "0000", totalCost = 1, shares = 1 },      // 不存在
            new { symbol = "9999", totalCost = 1, shares = 1 },      // 已停用
            new { symbol = "0050", totalCost = -1, shares = 1 },
            new { symbol = "0050", totalCost = 1, shares = 0 },
            new { symbol = "0050", totalCost = 1.5, shares = 1 },
        })
        {
            var r = await Send(c, csrf, HttpMethod.Post, $"/api/portfolios/{pid}/holdings", body);
            Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode);
            Assert.Equal("VALIDATION_FAILED", (await A.Json(r)).GetProperty("code").GetString());
        }
    }

    [Fact]
    public async Task Order_MustMatchExactly_AndSortOrderIsMaxPlusOne()
    {
        using var f = new TestFactory();
        var (c, csrf, _) = await A.SignInAsync(f, "alice", "User");
        var a = await NewPortfolio(c, csrf, "A");
        var b = await NewPortfolio(c, csrf, "B");
        var d = await NewPortfolio(c, csrf, "C");
        Assert.Equal(HttpStatusCode.BadRequest, (await Send(c, csrf, HttpMethod.Put, "/api/portfolios/order", new { ids = new[] { a, b } })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await Send(c, csrf, HttpMethod.Put, "/api/portfolios/order", new { ids = new[] { a, a, b } })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await Send(c, csrf, HttpMethod.Put, "/api/portfolios/order", new { ids = new[] { a, b, d, 999 } })).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await Send(c, csrf, HttpMethod.Put, "/api/portfolios/order", new { ids = new[] { d, a, b } })).StatusCode);
        var list = await A.Json(await c.GetAsync("/api/portfolios"));
        Assert.Equal(new[] { d, a, b }, list.EnumerateArray().Select(x => x.GetProperty("id").GetInt32()).ToArray());
        Assert.Equal(new[] { 1, 2, 3 }, list.EnumerateArray().Select(x => x.GetProperty("sortOrder").GetInt32()).ToArray());
    }

    [Fact]
    public async Task Search_ActiveOnly_RankedBySymbolThenName()
    {
        using var f = new TestFactory();
        await SeedInstruments(f);
        var (c, _, _) = await A.SignInAsync(f, "alice", "User");
        var r = await A.Json(await c.GetAsync("/api/instruments/search?q=0050"));
        Assert.Equal("0050", r[0].GetProperty("symbol").GetString());
        Assert.Equal("etf", r[0].GetProperty("kind").GetString());
        Assert.Equal("TWSE", r[0].GetProperty("market").GetString());
        Assert.Equal(1, (await A.Json(await c.GetAsync("/api/instruments/search?q=%E5%8F%B0%E7%A9%8D"))).GetArrayLength());
        Assert.Equal(0, (await A.Json(await c.GetAsync("/api/instruments/search?q=9999"))).GetArrayLength()); // 已停用
        Assert.Equal(HttpStatusCode.BadRequest, (await c.GetAsync("/api/instruments/search?q=")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await c.GetAsync("/api/instruments/search?q=" + new string('1', 21))).StatusCode);
    }
}

public class PortfolioPageTests
{
    [Fact]
    public async Task Page_Served_ListHoldings_IsOwnerScoped_NoPrices()
    {
        using var f = new TestFactory();
        using (var s = f.Services.CreateScope())
        {
            var db = s.ServiceProvider.GetRequiredService<AppDbContext>();
            db.Instruments.Add(new Instrument { Symbol = "0050", Name = "元大台灣50", Market = Market.Twse, Kind = InstrumentKind.Etf, UpdatedAtUtc = DateTime.UtcNow });
            await db.SaveChangesAsync();
        }
        var (a, ca, _) = await AdminApiTests.SignInAsync(f, "alice", "User");
        var (b, _, _) = await AdminApiTests.SignInAsync(f, "bob", "User");
        Assert.Equal(HttpStatusCode.OK, (await a.GetAsync("/Portfolios")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await a.GetAsync("/js/portfolios.js")).StatusCode);

        var created = await a.SendAsync(AdminApiTests.Req(HttpMethod.Post, "/api/portfolios", ca, new { name = "A" }));
        var pid = (await AdminApiTests.Json(created)).GetProperty("id").GetInt32();
        await a.SendAsync(AdminApiTests.Req(HttpMethod.Post, $"/api/portfolios/{pid}/holdings", ca, new { symbol = "0050", totalCost = 280000, shares = 2000 }));

        var list = await AdminApiTests.Json(await a.GetAsync($"/api/portfolios/{pid}/holdings"));
        Assert.Equal("元大台灣50", list[0].GetProperty("name").GetString());
        Assert.Equal(280000, list[0].GetProperty("totalCost").GetInt64());
        Assert.False(list[0].TryGetProperty("lastPrice", out _));
        Assert.Equal(HttpStatusCode.NotFound, (await b.GetAsync($"/api/portfolios/{pid}/holdings")).StatusCode);
    }
}
