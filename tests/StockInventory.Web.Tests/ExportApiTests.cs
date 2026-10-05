using System.Net;
using System.Text;
using Microsoft.Extensions.DependencyInjection;
using StockInventory.Core;
using StockInventory.Data;
using Xunit;
using A = StockInventory.Web.Tests.AdminApiTests;

namespace StockInventory.Web.Tests;

public class ExportApiTests // IT-08
{
    static async Task<(HttpClient c, string csrf, int p1, int p2, Guid uid)> Setup(TestFactory f)
    {
        using (var s = f.Services.CreateScope())
        {
            var db = s.ServiceProvider.GetRequiredService<AppDbContext>();
            db.Instruments.AddRange(
                new Instrument { Symbol = "0050", Name = "元大台灣50", Market = Market.Twse, Kind = InstrumentKind.Etf, UpdatedAtUtc = DateTime.UtcNow },
                new Instrument { Symbol = "2330", Name = "=HYPERLINK(\"http://evil\")", Market = Market.Twse, Kind = InstrumentKind.Stock, UpdatedAtUtc = DateTime.UtcNow },
                new Instrument { Symbol = "1101", Name = "台泥,水泥", Market = Market.Twse, Kind = InstrumentKind.Stock, UpdatedAtUtc = DateTime.UtcNow });
            await db.SaveChangesAsync();
        }
        var (c, csrf, uid) = await A.SignInAsync(f, "alice", "User");
        async Task<int> P(string n) => (await A.Json(await c.SendAsync(A.Req(HttpMethod.Post, "/api/portfolios", csrf, new { name = n })))).GetProperty("id").GetInt32();
        Task H(int pid, string sym, long cost, long sh) => c.SendAsync(A.Req(HttpMethod.Post, $"/api/portfolios/{pid}/holdings", csrf, new { symbol = sym, totalCost = cost, shares = sh }));
        var p1 = await P("+長期");
        var p2 = await P("短期");
        await H(p1, "0050", 130000, 1000); await H(p1, "2330", 520000, 1000); await H(p2, "0050", 150000, 1000); await H(p1, "1101", 5000, 100);
        return (c, csrf, p1, p2, uid);
    }

    static async Task<(byte[] raw, string[] lines, HttpResponseMessage res)> Get(HttpClient c, string url)
    {
        var res = await c.GetAsync(url);
        var raw = await res.Content.ReadAsByteArrayAsync();
        var text = Encoding.UTF8.GetString(raw).TrimStart('﻿');
        return (raw, text.Split("\r\n", StringSplitOptions.RemoveEmptyEntries), res);
    }

    [Fact]
    public async Task Merged_HasBom_Headers_FormulaGuard_NullsBlank()
    {
        using var f = new TestFactory();
        var (c, _, _, _, _) = await Setup(f);
        var (raw, lines, res) = await Get(c, "/api/export/holdings.csv");

        Assert.Equal(new byte[] { 0xEF, 0xBB, 0xBF }, raw.Take(3).ToArray());
        Assert.Equal("text/csv; charset=utf-8", res.Content.Headers.ContentType!.ToString());
        Assert.Matches(@"^attachment; filename=holdings-\d{8}-\d{4}\.csv", res.Content.Headers.ContentDisposition!.ToString().Replace("\"", ""));
        Assert.Equal("代號,名稱,市場,股數,總成本,均價,現價,漲跌,漲跌幅(%),市值,未實現損益,報酬率(%),今日損益,報價狀態", lines[0]);
        Assert.Equal(4, lines.Length); // 標題 + 0050(合併) + 2330 + 1101

        var etf = lines.Single(l => l.StartsWith("0050,"));
        Assert.Equal("0050,元大台灣50,TWSE,2000,280000,140.00,150.50,1.50,1.01,301000,21000,7.50,3000,live", etf);

        var evil = lines.Single(l => l.StartsWith("2330,"));
        Assert.StartsWith("2330,\"'=HYPERLINK(\"\"http://evil\"\")\",", evil); // 公式注入防護 + CSV 引號跳脫

        var missing = lines.Single(l => l.StartsWith("1101,"));
        Assert.Equal("1101,\"台泥,水泥\",TWSE,100,5000,50.00,,,,,,,,missing", missing); // null 為空白
    }

    [Fact]
    public async Task NotMerged_AddsPortfolioColumn_FirstAndGuardsIt()
    {
        using var f = new TestFactory();
        var (c, _, _, _, _) = await Setup(f);
        var (_, lines, _) = await Get(c, "/api/export/holdings.csv?merge=false");
        Assert.StartsWith("庫存,代號,名稱,", lines[0]);
        Assert.Equal(5, lines.Length); // 標題 + 4 筆持股
        Assert.Contains(lines, l => l.StartsWith("'+長期,0050,"));
        Assert.Contains(lines, l => l.StartsWith("短期,0050,"));
    }

    [Fact]
    public async Task DeductFees_AppendsTwoColumns()
    {
        using var f = new TestFactory();
        var (c, _, _, _, uid) = await Setup(f);
        using (var s = f.Services.CreateScope())
        {
            var db = s.ServiceProvider.GetRequiredService<AppDbContext>();
            db.UserSettings.Add(new UserSettings { UserId = uid, DeductFees = true, UpdatedAtUtc = DateTime.UtcNow });
            await db.SaveChangesAsync();
        }
        var (_, lines, _) = await Get(c, "/api/export/holdings.csv");
        Assert.EndsWith(",報價狀態,預估手續費,預估證交稅", lines[0]);
        Assert.Equal("0050,元大台灣50,TWSE,2000,280000,140.00,150.50,1.50,1.01,301000,20272,7.24,3000,live,428,300", lines.Single(l => l.StartsWith("0050,")));
    }

    [Fact]
    public async Task SelectionParam_And_Isolation_And_Auth()
    {
        using var f = new TestFactory();
        var (c, _, _, p2, _) = await Setup(f);
        var (_, one, _) = await Get(c, $"/api/export/holdings.csv?portfolioIds={p2}");
        Assert.Equal(2, one.Length);
        Assert.StartsWith("0050,元大台灣50,TWSE,1000,", one[1]);

        var (b, _, _) = await A.SignInAsync(f, "bob", "User");
        var (_, other, _) = await Get(b, "/api/export/holdings.csv");
        Assert.Single(other); // 只有標題,看不到 alice 的資料

        Assert.Equal(HttpStatusCode.Unauthorized, (await LoginFlowTests.NewClient(f).GetAsync("/api/export/holdings.csv")).StatusCode);
    }
}
