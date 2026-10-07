using System.Net;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using StockInventory.Data;
using static StockInventory.Web.Tests.VisionTests;
using A = StockInventory.Web.Tests.AdminApiTests;

namespace StockInventory.Web.Tests;

public class BatchHoldingsApiTests
{
    static async Task<(TestFactory F, HttpClient C, string Csrf, int Pid, FakeFetch Fetch)> Setup(int extraInstruments = 0)
    {
        var fetch = new FakeFetch();
        var f = Factory(fetch: fetch);
        await SeedInstruments(f, extraInstruments);
        var (c, csrf, _) = await A.SignInAsync(f, "alice", "User");
        return (f, c, csrf, await NewPortfolio(c, csrf), fetch);
    }

    static Task<HttpResponseMessage> Batch(HttpClient c, string csrf, int pid, params object[] items) =>
        c.SendAsync(A.Req(HttpMethod.Post, $"/api/portfolios/{pid}/holdings/batch", csrf, new { items }));

    static async Task<(int Holdings, List<HoldingChange> Changes)> State(TestFactory f)
    {
        using var s = f.Services.CreateScope();
        var db = s.ServiceProvider.GetRequiredService<AppDbContext>();
        return (await db.Holdings.CountAsync(), await db.HoldingChanges.AsNoTracking().OrderBy(x => x.ChangeId).ToListAsync());
    }

    [Fact] // IT-10:新增寫 A、呼叫 RequestImmediateFetch
    public async Task Batch_Creates_WritesAChanges_AndRequestsFetch()
    {
        var (f, c, csrf, pid, fetch) = await Setup();
        using var _f = f;
        var r = await Batch(c, csrf, pid,
            new { symbol = " 2330 ", shares = 1000, totalCost = 520000 }, new { symbol = "0050", shares = 200, totalCost = 0 });
        Assert.Equal(HttpStatusCode.Created, r.StatusCode);
        var j = await A.Json(r);
        Assert.Equal(2, j.GetProperty("created").GetArrayLength());
        Assert.Equal("2330", j.GetProperty("created")[0].GetProperty("symbol").GetString());
        Assert.True(j.GetProperty("created")[0].GetProperty("holdingId").GetInt32() > 0);
        Assert.Equal(0, j.GetProperty("updated").GetArrayLength());
        Assert.Equal(0, j.GetProperty("skipped").GetArrayLength());

        var (n, changes) = await State(f);
        Assert.Equal(2, n);
        Assert.All(changes, x => { Assert.Equal('A', x.Action); Assert.Null(x.OldShares); Assert.Null(x.OldTotalCost); Assert.Equal("P", x.PortfolioName); });
        Assert.Equal([1000L, 200L], changes.Select(x => x.NewShares!.Value));
        Assert.Equal(["0050", "2330"], fetch.Symbols.OrderBy(x => x));
    }

    [Fact] // IT-10:任一筆無效 → 整批不寫
    public async Task Batch_AnyInvalid_WritesNothing()
    {
        var (f, c, csrf, pid, fetch) = await Setup();
        using var _f = f;
        var r = await Batch(c, csrf, pid,
            new { symbol = "2330", shares = 1000, totalCost = 1 },
            new { symbol = "ZZZZ", shares = 1, totalCost = 1 },
            new { symbol = "9999", shares = 1, totalCost = 1 },
            new { symbol = "0050", shares = 1.5, totalCost = -1 });
        await AssertProblem(r, HttpStatusCode.BadRequest, "VALIDATION_FAILED");
        var errors = (await A.Json(r)).GetProperty("errors");
        Assert.Equal("找不到此標的", errors.GetProperty("items[1].symbol")[0].GetString());
        Assert.Equal("找不到此標的", errors.GetProperty("items[2].symbol")[0].GetString());
        Assert.True(errors.TryGetProperty("items[3].shares", out _));
        Assert.True(errors.TryGetProperty("items[3].totalCost", out _));
        Assert.False(errors.TryGetProperty("items[0].symbol", out _));
        var (n, changes) = await State(f);
        Assert.Equal(0, n);
        Assert.Empty(changes);
        Assert.Empty(fetch.Symbols);
    }

    [Fact] // IT-10:請求內重複代號
    public async Task Batch_DuplicateInRequest_Is400()
    {
        var (f, c, csrf, pid, _) = await Setup();
        using var _f = f;
        var r = await Batch(c, csrf, pid, new { symbol = "2330", shares = 1, totalCost = 1 }, new { symbol = " 2330", shares = 2, totalCost = 2 });
        await AssertProblem(r, HttpStatusCode.BadRequest, "VALIDATION_FAILED");
        Assert.True((await A.Json(r)).GetProperty("errors").TryGetProperty("items[1].symbol", out _));
        Assert.Equal(0, (await State(f)).Holdings);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(201)]
    public async Task Batch_ItemCountOutOfRange_Is400(int count)
    {
        var (f, c, csrf, pid, _) = await Setup();
        using var _f = f;
        var items = Enumerable.Range(0, count).Select(_ => (object)new { symbol = "2330", shares = 1, totalCost = 1 }).ToArray();
        await AssertProblem(await Batch(c, csrf, pid, items), HttpStatusCode.BadRequest, "VALIDATION_FAILED");
        var none = await c.SendAsync(A.Req(HttpMethod.Post, $"/api/portfolios/{pid}/holdings/batch", csrf, new { }));
        await AssertProblem(none, HttpStatusCode.BadRequest, "VALIDATION_FAILED");
    }

    [Fact]
    public async Task Batch_BadOnExists_Is400()
    {
        var (f, c, csrf, pid, _) = await Setup();
        using var _f = f;
        var r = await Batch(c, csrf, pid, new { symbol = "2330", shares = 1, totalCost = 1, onExists = "overwrite" });
        await AssertProblem(r, HttpStatusCode.BadRequest, "VALIDATION_FAILED");
        Assert.True((await A.Json(r)).GetProperty("errors").TryGetProperty("items[0].onExists", out _));
    }

    [Fact] // IT-10:skip、update、UNCHANGED
    public async Task Batch_ExistingHoldings_Skip_Update_Unchanged()
    {
        var (f, c, csrf, pid, fetch) = await Setup();
        using var _f = f;
        await Batch(c, csrf, pid, new { symbol = "2330", shares = 10, totalCost = 1000 }, new { symbol = "0050", shares = 20, totalCost = 2000 });
        fetch.Symbols.Clear();

        // 預設 skip:不寫入
        var skip = await Batch(c, csrf, pid, new { symbol = "2330", shares = 99, totalCost = 99 });
        Assert.Equal(HttpStatusCode.Created, skip.StatusCode);
        var sj = await A.Json(skip);
        Assert.Equal("EXISTS", sj.GetProperty("skipped")[0].GetProperty("reason").GetString());
        Assert.Equal(2, (await State(f)).Changes.Count);
        Assert.Empty(fetch.Symbols);

        // update:寫 U、前後值正確;未變更的不寫並回 UNCHANGED
        var r = await Batch(c, csrf, pid,
            new { symbol = "2330", shares = 11, totalCost = 1500, onExists = "update" },
            new { symbol = "0050", shares = 20, totalCost = 2000, onExists = "update" });
        Assert.Equal(HttpStatusCode.Created, r.StatusCode);
        var j = await A.Json(r);
        Assert.Equal(1, j.GetProperty("updated").GetArrayLength());
        Assert.Equal(11, j.GetProperty("updated")[0].GetProperty("shares").GetInt64());
        Assert.Equal("UNCHANGED", j.GetProperty("skipped")[0].GetProperty("reason").GetString());
        Assert.Equal("0050", j.GetProperty("skipped")[0].GetProperty("symbol").GetString());

        var (n, changes) = await State(f);
        Assert.Equal(2, n);
        Assert.Equal(3, changes.Count);
        var u = changes[^1];
        Assert.Equal('U', u.Action);
        Assert.Equal("2330", u.Symbol);
        Assert.Equal((10L, 1000L, 11L, 1500L), (u.OldShares!.Value, u.OldTotalCost!.Value, u.NewShares!.Value, u.NewTotalCost!.Value));
        Assert.Equal(["2330"], fetch.Symbols);

        var list = await A.Json(await c.GetAsync($"/api/portfolios/{pid}/holdings"));
        Assert.Equal(1500, list.EnumerateArray().Single(x => x.GetProperty("symbol").GetString() == "2330").GetProperty("totalCost").GetInt64());
    }

    [Fact] // IT-10:現有 199 檔再新增 2 檔 → 422,整批不寫
    public async Task Batch_Over200_Is422_NothingWritten()
    {
        var (f, c, csrf, pid, fetch) = await Setup(extraInstruments: 199);
        using var _f = f;
        using (var s = f.Services.CreateScope())
        {
            var db = s.ServiceProvider.GetRequiredService<AppDbContext>();
            for (var i = 0; i < 199; i++)
                db.Holdings.Add(new Holding { PortfolioId = pid, Symbol = $"T{i:D4}", Shares = 1, TotalCost = 1, CreatedAtUtc = DateTime.UtcNow, UpdatedAtUtc = DateTime.UtcNow });
            await db.SaveChangesAsync();
        }
        var r = await Batch(c, csrf, pid, new { symbol = "2330", shares = 1, totalCost = 1 }, new { symbol = "0050", shares = 1, totalCost = 1 });
        await AssertProblem(r, HttpStatusCode.UnprocessableEntity, "LIMIT_EXCEEDED");
        var (n, changes) = await State(f);
        Assert.Equal(199, n);
        Assert.Empty(changes);
        Assert.Empty(fetch.Symbols);

        // 更新與略過不佔名額:199 檔 + 1 新增 = 200 可以
        var ok = await Batch(c, csrf, pid, new { symbol = "2330", shares = 1, totalCost = 1 }, new { symbol = "T0001", shares = 5, totalCost = 5, onExists = "update" });
        Assert.Equal(HttpStatusCode.Created, ok.StatusCode);
        Assert.Equal(200, (await State(f)).Holdings);
    }

    [Fact] // IT-10:與 API-07 共用服務,結果一致
    public async Task Batch_And_SingleCreate_ProduceSameRecords()
    {
        var (f, c, csrf, pid, _) = await Setup();
        using var _f = f;
        var pid2 = await NewPortfolio(c, csrf, "Q");
        Assert.Equal(HttpStatusCode.Created, (await c.SendAsync(A.Req(HttpMethod.Post, $"/api/portfolios/{pid}/holdings", csrf,
            new { symbol = "2330", totalCost = 777, shares = 33 }))).StatusCode);
        Assert.Equal(HttpStatusCode.Created, (await Batch(c, csrf, pid2, new { symbol = "2330", totalCost = 777, shares = 33 })).StatusCode);
        var (_, changes) = await State(f);
        Assert.Equal(2, changes.Count);
        Assert.Equal((changes[0].Action, changes[0].NewShares, changes[0].NewTotalCost, changes[0].OldShares),
            (changes[1].Action, changes[1].NewShares, changes[1].NewTotalCost, changes[1].OldShares));
        // API-07 對已存在的標的仍回 409
        Assert.Equal(HttpStatusCode.Conflict, (await c.SendAsync(A.Req(HttpMethod.Post, $"/api/portfolios/{pid}/holdings", csrf,
            new { symbol = "2330", totalCost = 1, shares = 1 }))).StatusCode);
    }
}
