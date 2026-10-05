using System.Net.Http.Json;
using System.Net;
using System.Text.Json;
using Xunit;
using A = StockInventory.Web.Tests.AdminApiTests;

namespace StockInventory.Web.Tests;

public class SettingsApiTests
{
    static object Body(int color = 0, bool deduct = false, decimal rate = 0.001425m, decimal disc = 1.000m, int[]? sel = null) =>
        new { colorScheme = color, deductFees = deduct, feeRate = rate, feeDiscount = disc, selectedPortfolioIds = sel };

    static Task<HttpResponseMessage> Put(HttpClient c, string csrf, object body) =>
        c.SendAsync(A.Req(HttpMethod.Put, "/api/settings", csrf, body));

    [Fact]
    public async Task Get_ReturnsDefaults_WhenNoRow()
    {
        using var f = new TestFactory();
        var (c, _, _) = await A.SignInAsync(f, "alice", "User");
        var s = await A.Json(await c.GetAsync("/api/settings"));
        Assert.Equal(0, s.GetProperty("colorScheme").GetInt32());
        Assert.False(s.GetProperty("deductFees").GetBoolean());
        Assert.Equal(0.001425m, s.GetProperty("feeRate").GetDecimal());
        Assert.Equal(1.000m, s.GetProperty("feeDiscount").GetDecimal());
        Assert.Equal(JsonValueKind.Null, s.GetProperty("selectedPortfolioIds").ValueKind);
    }

    [Fact]
    public async Task Put_OverwritesAll_AndPersists()
    {
        using var f = new TestFactory();
        var (c, csrf, _) = await A.SignInAsync(f, "alice", "User");
        var p1 = (await A.Json(await c.SendAsync(A.Req(HttpMethod.Post, "/api/portfolios", csrf, new { name = "A" })))).GetProperty("id").GetInt32();
        var p2 = (await A.Json(await c.SendAsync(A.Req(HttpMethod.Post, "/api/portfolios", csrf, new { name = "B" })))).GetProperty("id").GetInt32();

        var res = await Put(c, csrf, Body(1, true, 0.002m, 0.28m, [p2, p1, p2]));
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        var s = await A.Json(await c.GetAsync("/api/settings"));
        Assert.Equal(1, s.GetProperty("colorScheme").GetInt32());
        Assert.True(s.GetProperty("deductFees").GetBoolean());
        Assert.Equal(0.002m, s.GetProperty("feeRate").GetDecimal());
        Assert.Equal(0.28m, s.GetProperty("feeDiscount").GetDecimal());
        Assert.Equal(new[] { p2, p1 }, s.GetProperty("selectedPortfolioIds").EnumerateArray().Select(x => x.GetInt32()).ToArray()); // 去重、保留順序

        await Put(c, csrf, Body()); // 整份覆寫:selectedPortfolioIds 回到 null
        var s2 = await A.Json(await c.GetAsync("/api/settings"));
        Assert.Equal(JsonValueKind.Null, s2.GetProperty("selectedPortfolioIds").ValueKind);
        Assert.Equal(0, s2.GetProperty("colorScheme").GetInt32());
    }

    [Fact]
    public async Task Put_DropsOtherUsersPortfolioIds()
    {
        using var f = new TestFactory();
        var (a, ca, _) = await A.SignInAsync(f, "alice", "User");
        var (b, cb, _) = await A.SignInAsync(f, "bob", "User");
        var pa = (await A.Json(await a.SendAsync(A.Req(HttpMethod.Post, "/api/portfolios", ca, new { name = "A" })))).GetProperty("id").GetInt32();
        var pb = (await A.Json(await b.SendAsync(A.Req(HttpMethod.Post, "/api/portfolios", cb, new { name = "B" })))).GetProperty("id").GetInt32();

        await Put(b, cb, Body(sel: [pa, pb, 9999]));
        var s = await A.Json(await b.GetAsync("/api/settings"));
        Assert.Equal(new[] { pb }, s.GetProperty("selectedPortfolioIds").EnumerateArray().Select(x => x.GetInt32()).ToArray());

        await Put(b, cb, Body(sel: [pa])); // 全被丟棄 → null(全部)
        Assert.Equal(JsonValueKind.Null, (await A.Json(await b.GetAsync("/api/settings"))).GetProperty("selectedPortfolioIds").ValueKind);

        // 使用者之間互不影響
        Assert.Equal(JsonValueKind.Null, (await A.Json(await a.GetAsync("/api/settings"))).GetProperty("selectedPortfolioIds").ValueKind);
    }

    [Theory]
    [InlineData(2, 0.001425, 1.0, "colorScheme")]
    [InlineData(0, 0.0101, 1.0, "feeRate")]
    [InlineData(0, -0.001, 1.0, "feeRate")]
    [InlineData(0, 0.001425, 0.0009, "feeDiscount")]
    [InlineData(0, 0.001425, 1.001, "feeDiscount")]
    public async Task Put_OutOfRange_Returns400WithFieldError(int color, double rate, double disc, string field)
    {
        using var f = new TestFactory();
        var (c, csrf, _) = await A.SignInAsync(f, "alice", "User");
        var res = await Put(c, csrf, Body(color, false, (decimal)rate, (decimal)disc));
        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
        var j = await A.Json(res);
        Assert.Equal("VALIDATION_FAILED", j.GetProperty("code").GetString());
        Assert.True(j.GetProperty("errors").TryGetProperty(field, out _));
    }

    [Theory]
    [InlineData(0, 0, 1)] [InlineData(1, 0.01, 0.001)]
    public async Task Put_Boundaries_Accepted(int color, double rate, double disc)
    {
        using var f = new TestFactory();
        var (c, csrf, _) = await A.SignInAsync(f, "alice", "User");
        Assert.Equal(HttpStatusCode.OK, (await Put(c, csrf, Body(color, false, (decimal)rate, (decimal)disc))).StatusCode);
    }

    [Fact]
    public async Task Put_MissingFields_Returns400()
    {
        using var f = new TestFactory();
        var (c, csrf, _) = await A.SignInAsync(f, "alice", "User");
        Assert.Equal(HttpStatusCode.BadRequest, (await Put(c, csrf, new { colorScheme = 0 })).StatusCode);
    }

    [Fact]
    public async Task AuthAndCsrf_Enforced_AndPageServed()
    {
        using var f = new TestFactory();
        Assert.Equal(HttpStatusCode.Unauthorized, (await LoginFlowTests.NewClient(f).GetAsync("/api/settings")).StatusCode);
        var (c, _, _) = await A.SignInAsync(f, "alice", "User");
        var noCsrf = await c.PutAsJsonAsync("/api/settings", Body());
        Assert.Equal(HttpStatusCode.BadRequest, noCsrf.StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await c.GetAsync("/Settings")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await c.GetAsync("/js/settings.js")).StatusCode);
    }
}
