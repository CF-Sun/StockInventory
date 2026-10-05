using System.Net;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using StockInventory.Core;
using StockInventory.Data;
using Xunit;
using A = StockInventory.Web.Tests.AdminApiTests;

namespace StockInventory.Web.Tests;

public class ViewApiTests
{
    static async Task Seed(TestFactory f)
    {
        using var s = f.Services.CreateScope();
        var db = s.ServiceProvider.GetRequiredService<AppDbContext>();
        db.Instruments.AddRange(
            new Instrument { Symbol = "0050", Name = "元大台灣50", Market = Market.Twse, Kind = InstrumentKind.Etf, UpdatedAtUtc = DateTime.UtcNow },
            new Instrument { Symbol = "2330", Name = "台積電", Market = Market.Twse, Kind = InstrumentKind.Stock, UpdatedAtUtc = DateTime.UtcNow },
            new Instrument { Symbol = "1101", Name = "台泥", Market = Market.Twse, Kind = InstrumentKind.Stock, UpdatedAtUtc = DateTime.UtcNow }); // 假報價沒有 1101
        await db.SaveChangesAsync();
    }

    static async Task<int> P(HttpClient c, string csrf, string name) =>
        (await A.Json(await c.SendAsync(A.Req(HttpMethod.Post, "/api/portfolios", csrf, new { name })))).GetProperty("id").GetInt32();

    static Task H(HttpClient c, string csrf, int pid, string sym, long cost, long shares) =>
        c.SendAsync(A.Req(HttpMethod.Post, $"/api/portfolios/{pid}/holdings", csrf, new { symbol = sym, totalCost = cost, shares }));

    static async Task<(HttpClient c, string csrf, int p1, int p2)> Setup(TestFactory f, string user = "alice")
    {
        await Seed(f);
        var (c, csrf, _) = await A.SignInAsync(f, user, "User");
        var p1 = await P(c, csrf, "長期");
        var p2 = await P(c, csrf, "短期");
        await H(c, csrf, p1, "0050", 130000, 1000);
        await H(c, csrf, p1, "2330", 520000, 1000);
        await H(c, csrf, p2, "0050", 150000, 1000);
        return (c, csrf, p1, p2);
    }

    static JsonElement Row(JsonElement v, string sym) => v.GetProperty("rows").EnumerateArray().First(r => r.GetProperty("symbol").GetString() == sym);

    [Fact] // IT-07:全部 + merge(預設)
    public async Task All_Merged_ByDefault_WithSources()
    {
        using var f = new TestFactory();
        var (c, _, p1, p2) = await Setup(f);
        var v = await A.Json(await c.GetAsync("/api/holdings"));

        Assert.Equal(2, v.GetProperty("rows").GetArrayLength());
        var etf = Row(v, "0050");
        Assert.Equal(2000, etf.GetProperty("shares").GetInt64());
        Assert.Equal(280000, etf.GetProperty("totalCost").GetInt64());
        Assert.Equal(140.00m, etf.GetProperty("avgCost").GetDecimal());
        Assert.Equal(301000, etf.GetProperty("marketValue").GetInt64());
        Assert.Equal(21000, etf.GetProperty("unrealizedPnl").GetInt64());
        Assert.Equal(7.50m, etf.GetProperty("returnRatePct").GetDecimal());
        Assert.Equal(1.50m, etf.GetProperty("change").GetDecimal());
        Assert.Equal(1.01m, etf.GetProperty("changePct").GetDecimal());
        Assert.Equal(3000, etf.GetProperty("todayPnl").GetInt64());
        Assert.Equal("trade", etf.GetProperty("priceSource").GetString());
        Assert.Equal("live", etf.GetProperty("quoteStatus").GetString());
        Assert.Equal("etf", etf.GetProperty("kind").GetString());
        Assert.Equal("TWSE", etf.GetProperty("market").GetString());
        Assert.Equal(JsonValueKind.Null, etf.GetProperty("estFee").ValueKind); // null 不省略
        var src = etf.GetProperty("sources");
        Assert.Equal(2, src.GetArrayLength());
        Assert.Equal(new[] { p1, p2 }, src.EnumerateArray().Select(x => x.GetProperty("portfolioId").GetInt32()).OrderBy(x => x).ToArray());
        Assert.Equal(0, Row(v, "2330").GetProperty("sources").GetArrayLength());

        var s = v.GetProperty("summary");
        Assert.Equal(901000, s.GetProperty("marketValue").GetInt64());
        Assert.Equal(800000, s.GetProperty("totalCost").GetInt64());
        Assert.Equal(101000, s.GetProperty("unrealizedPnl").GetInt64());
        Assert.Equal(12.63m, s.GetProperty("returnRatePct").GetDecimal()); // TV-01
        Assert.Equal(-7000, s.GetProperty("todayPnl").GetInt64());
        Assert.Equal(0, s.GetProperty("missingQuoteCount").GetInt32());
        Assert.Equal("2330", v.GetProperty("rows")[0].GetProperty("symbol").GetString()); // 市值大到小
        Assert.Equal("closed", v.GetProperty("market").GetProperty("state").GetString());
    }

    [Fact]
    public async Task MergeFalse_OneRowPerHolding()
    {
        using var f = new TestFactory();
        var (c, _, _, _) = await Setup(f);
        var v = await A.Json(await c.GetAsync("/api/holdings?merge=false"));
        Assert.Equal(3, v.GetProperty("rows").GetArrayLength());
        Assert.All(v.GetProperty("rows").EnumerateArray(), r => Assert.Equal(0, r.GetProperty("sources").GetArrayLength()));
    }

    [Fact]
    public async Task SinglePortfolio_And_MultiSelect()
    {
        using var f = new TestFactory();
        var (c, _, p1, p2) = await Setup(f);
        var single = await A.Json(await c.GetAsync($"/api/holdings?portfolioIds={p2}"));
        Assert.Equal(1, single.GetProperty("rows").GetArrayLength());
        Assert.Equal(1000, Row(single, "0050").GetProperty("shares").GetInt64());
        Assert.Equal(150500 - 150000, single.GetProperty("summary").GetProperty("unrealizedPnl").GetInt64());

        var both = await A.Json(await c.GetAsync($"/api/holdings?portfolioIds={p1},{p2}"));
        Assert.Equal(2000, Row(both, "0050").GetProperty("shares").GetInt64());
    }

    [Fact]
    public async Task InvalidIds_Ignored_AllInvalid_MeansAll()
    {
        using var f = new TestFactory();
        var (c, _, p1, _) = await Setup(f);
        var mixed = await A.Json(await c.GetAsync($"/api/holdings?portfolioIds={p1},9999,abc"));
        Assert.Equal(2, mixed.GetProperty("rows").GetArrayLength());
        Assert.Equal(1000, Row(mixed, "0050").GetProperty("shares").GetInt64()); // 只含 p1
        var bad = await A.Json(await c.GetAsync("/api/holdings?portfolioIds=9999,8888"));
        Assert.Equal(2000, Row(bad, "0050").GetProperty("shares").GetInt64());    // 視為全部
    }

    [Fact]
    public async Task OtherUsersPortfolioIds_AreIgnored()
    {
        using var f = new TestFactory();
        var (_, _, p1, _) = await Setup(f);
        var (b, cb, _) = await A.SignInAsync(f, "bob", "User");
        var mine = await P(b, cb, "Bob");
        await H(b, cb, mine, "2330", 1, 1);
        var v = await A.Json(await b.GetAsync($"/api/holdings?portfolioIds={p1}")); // alice 的 Id → 忽略 → 全部(只有 bob 的)
        Assert.Equal(1, v.GetProperty("rows").GetArrayLength());
        Assert.Equal(1, Row(v, "2330").GetProperty("shares").GetInt64());
    }

    [Fact]
    public async Task MissingQuote_Row_HasNulls_AndIsCounted()
    {
        using var f = new TestFactory();
        var (c, csrf, p1, _) = await Setup(f);
        await H(c, csrf, p1, "1101", 5000, 100);
        var v = await A.Json(await c.GetAsync("/api/holdings"));
        var r = Row(v, "1101");
        Assert.Equal("missing", r.GetProperty("quoteStatus").GetString());
        Assert.Equal(JsonValueKind.Null, r.GetProperty("marketValue").ValueKind);
        Assert.Equal(JsonValueKind.Null, r.GetProperty("lastPrice").ValueKind);
        Assert.Equal(1, v.GetProperty("summary").GetProperty("missingQuoteCount").GetInt32());
        Assert.Equal("1101", v.GetProperty("rows")[2].GetProperty("symbol").GetString()); // 缺價排最後
    }

    [Fact]
    public async Task DeductFees_UsesUserSettings()
    {
        using var f = new TestFactory();
        var (c, _, _, _) = await Setup(f);
        using (var s = f.Services.CreateScope())
        {
            var db = s.ServiceProvider.GetRequiredService<AppDbContext>();
            var uid = db.Users.Single(u => u.UserName == "alice").Id;
            db.UserSettings.Add(new UserSettings { UserId = uid, DeductFees = true, UpdatedAtUtc = DateTime.UtcNow });
            await db.SaveChangesAsync();
        }
        var v = await A.Json(await c.GetAsync("/api/holdings")); // TV-02:merge=true, deductFees=true
        var etf = Row(v, "0050");
        Assert.Equal(20272, etf.GetProperty("unrealizedPnl").GetInt64());
        Assert.Equal(7.24m, etf.GetProperty("returnRatePct").GetDecimal());
        Assert.Equal(428, etf.GetProperty("estFee").GetInt64());
        Assert.Equal(300, etf.GetProperty("estTax").GetInt64());
    }

    [Fact]
    public async Task Unauthenticated_Returns401()
    {
        using var f = new TestFactory();
        Assert.Equal(HttpStatusCode.Unauthorized, (await LoginFlowTests.NewClient(f).GetAsync("/api/holdings")).StatusCode);
    }
}
