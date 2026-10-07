using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using StockInventory.Core;
using StockInventory.Data;
using StockInventory.Web.Vision;
using static StockInventory.Web.Tests.VisionTests;
using A = StockInventory.Web.Tests.AdminApiTests;

namespace StockInventory.Web.Tests;

public class VisionApiTests
{
    static async Task<(TestFactory F, HttpClient C, string Csrf, int Pid)> Setup(StubRecognizer? stub = null,
        Dictionary<string, string?>? extra = null, bool enabled = true, string user = "alice")
    {
        var f = Factory(stub ?? new StubRecognizer(), extra, enabled);
        await SeedInstruments(f);
        var (c, csrf, _) = await A.SignInAsync(f, user, "User");
        return (f, c, csrf, await NewPortfolio(c, csrf));
    }

    [Fact] // IT-09:成功與回應內容
    public async Task Recognize_Success_ReturnsDraft_NoStore_AndWritesNothing()
    {
        var stub = new StubRecognizer((_, _, _) => Task.FromResult(new RecognitionOutput(true,
            [new RecognizedRow("2330", 1000, 520000), new RecognizedRow("0050", 200, null), new RecognizedRow(null, 5, 1)])));
        var (f, c, csrf, pid) = await Setup(stub);
        using var _f = f;
        var r = await c.SendAsync(ImageReq(pid, csrf, Img.Png()));
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        Assert.Contains("no-store", r.Headers.CacheControl!.ToString());
        var j = await A.Json(r);
        Assert.Equal(3, j.GetProperty("recognizedCount").GetInt32());
        Assert.Equal(200, j.GetProperty("remainingSlots").GetInt32());
        var items = j.GetProperty("items");
        Assert.Equal("r1", items[0].GetProperty("clientId").GetString());
        Assert.Equal("台積電", items[0].GetProperty("name").GetString());
        Assert.Equal("OK", items[0].GetProperty("status").GetString());
        Assert.Equal("INVALID", items[1].GetProperty("status").GetString());
        Assert.Equal("NOT_FOUND", items[2].GetProperty("status").GetString());
        Assert.False(items[0].TryGetProperty("unitWarning", out _));
        Assert.Equal(JsonValueKindNull, items[0].GetProperty("existing").ValueKind);

        using var s = f.Services.CreateScope();
        var db = s.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.Equal(0, await db.Holdings.CountAsync());
        Assert.Equal(0, await db.HoldingChanges.CountAsync());
    }
    const System.Text.Json.JsonValueKind JsonValueKindNull = System.Text.Json.JsonValueKind.Null;

    [Fact]
    public async Task Recognize_SendsReencodedJpeg_WithoutExif()
    {
        var stub = new StubRecognizer();
        var (f, c, csrf, pid) = await Setup(stub);
        using var _f = f;
        var original = Img.JpegWithExifGps();
        Assert.True(Img.ContainsAscii(original, Img.ExifMarker));
        var r = await c.SendAsync(ImageReq(pid, csrf, original, fileName: "IMG_secret_name.jpg", contentType: "image/jpeg"));
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        Assert.Equal("image/jpeg", stub.LastMime);
        Assert.NotNull(stub.LastImage);
        Assert.False(Img.ContainsAscii(stub.LastImage!, Img.ExifMarker));
        Assert.False(Img.ContainsAscii(stub.LastImage!, "Exif"));
    }

    [Fact]
    public async Task Recognize_Existing_And_Duplicate_And_Inactive()
    {
        var stub = new StubRecognizer((_, _, _) => Task.FromResult(new RecognitionOutput(true,
            [new RecognizedRow("2330", 2000, 9), new RecognizedRow("9999", 1, 1), new RecognizedRow("0050", 1, 1), new RecognizedRow("0050", 2, 2)])));
        var (f, c, csrf, pid) = await Setup(stub);
        using var _f = f;
        var add = await c.SendAsync(A.Req(HttpMethod.Post, $"/api/portfolios/{pid}/holdings", csrf, new { symbol = "2330", totalCost = 500, shares = 1000 }));
        Assert.Equal(HttpStatusCode.Created, add.StatusCode);
        var j = await A.Json(await c.SendAsync(ImageReq(pid, csrf, Img.Png())));
        var items = j.GetProperty("items");
        Assert.Equal(199, j.GetProperty("remainingSlots").GetInt32());
        Assert.Equal("EXISTS", items[0].GetProperty("status").GetString());
        Assert.Equal(1000, items[0].GetProperty("existing").GetProperty("shares").GetInt64());
        Assert.Equal(500, items[0].GetProperty("existing").GetProperty("totalCost").GetInt64());
        Assert.Equal("INACTIVE", items[1].GetProperty("status").GetString());
        Assert.Equal("DUPLICATE_IN_IMAGE", items[2].GetProperty("status").GetString());
        Assert.Equal("DUPLICATE_IN_IMAGE", items[3].GetProperty("status").GetString());
    }

    [Fact] // IT-09:認證與權限
    public async Task Recognize_Auth_Csrf_Ownership()
    {
        var (f, c, csrf, pid) = await Setup();
        using var _f = f;
        var anon = StockInventory.Web.Tests.LoginFlowTests.NewClient(f);
        // 既有行為:CSRF 中介層早於授權,匿名 POST(拿不到有效權杖)在 /api 一律先得到 400,與 API-07 等相同;匿名者絕不會通過
        var anonRes = await anon.SendAsync(ImageReq(pid, null, Img.Png()));
        Assert.True(anonRes.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.BadRequest);
        await AssertProblem(await c.SendAsync(ImageReq(pid, null, Img.Png())), HttpStatusCode.BadRequest, "VALIDATION_FAILED");
        var (b, cb, _) = await A.SignInAsync(f, "bob", "User");
        await AssertProblem(await b.SendAsync(ImageReq(pid, cb, Img.Png())), HttpStatusCode.NotFound, "NOT_FOUND");
        await AssertProblem(await c.SendAsync(ImageReq(99999, csrf, Img.Png())), HttpStatusCode.NotFound, "NOT_FOUND");
    }

    [Theory] // IT-09:consent
    [InlineData(null)]
    [InlineData("false")]
    [InlineData("")]
    [InlineData("yes")]
    public async Task Recognize_ConsentRequired(string? consent)
    {
        var stub = new StubRecognizer();
        var (f, c, csrf, pid) = await Setup(stub);
        using var _f = f;
        await AssertProblem(await c.SendAsync(ImageReq(pid, csrf, Img.Png(), consent)), HttpStatusCode.BadRequest, "CONSENT_REQUIRED");
        Assert.Equal(0, stub.Calls);
    }

    [Fact] // IT-09:缺 image、多檔
    public async Task Recognize_MissingImage_And_MultipleFiles_Are400()
    {
        var stub = new StubRecognizer();
        var (f, c, csrf, pid) = await Setup(stub);
        using var _f = f;
        await AssertProblem(await c.SendAsync(ImageReq(pid, csrf, null)), HttpStatusCode.BadRequest, "VALIDATION_FAILED");
        await AssertProblem(await c.SendAsync(ImageReq(pid, csrf, Img.Png(), files: 2)), HttpStatusCode.BadRequest, "VALIDATION_FAILED");
        var json = new HttpRequestMessage(HttpMethod.Post, $"/api/portfolios/{pid}/holdings/recognize") { Content = JsonContent.Create(new { }) };
        json.Headers.Add("X-CSRF-TOKEN", csrf);
        await AssertProblem(await c.SendAsync(json), HttpStatusCode.BadRequest, "VALIDATION_FAILED");
        Assert.Equal(0, stub.Calls);
    }

    [Fact] // IT-09:格式
    public async Task Recognize_BadFormats_Are415()
    {
        var stub = new StubRecognizer();
        var (f, c, csrf, pid) = await Setup(stub);
        using var _f = f;
        // 副檔名與 Content-Type 都說是 PNG,但內容不是
        await AssertProblem(await c.SendAsync(ImageReq(pid, csrf, "not an image"u8.ToArray())), HttpStatusCode.UnsupportedMediaType, "UNSUPPORTED_MEDIA");
        await AssertProblem(await c.SendAsync(ImageReq(pid, csrf, Img.Heic(), fileName: "a.png")), HttpStatusCode.UnsupportedMediaType, "UNSUPPORTED_MEDIA");
        await AssertProblem(await c.SendAsync(ImageReq(pid, csrf, Img.Heic(), fileName: "a.heic", contentType: "image/heic")), HttpStatusCode.UnsupportedMediaType, "UNSUPPORTED_MEDIA");
        await AssertProblem(await c.SendAsync(ImageReq(pid, csrf, Img.Gif(), fileName: "a.png")), HttpStatusCode.UnsupportedMediaType, "UNSUPPORTED_MEDIA");
        var truncated = Img.Jpeg(400, 400); // 魔術位元組正確但無法解碼
        await AssertProblem(await c.SendAsync(ImageReq(pid, csrf, truncated[..(truncated.Length / 3)], fileName: "a.jpg")), HttpStatusCode.UnsupportedMediaType, "UNSUPPORTED_MEDIA");
        Assert.Equal(0, stub.Calls);
        // JPEG、PNG、WebP 都接受(即使檔名與 Content-Type 亂填)
        Assert.Equal(HttpStatusCode.OK, (await c.SendAsync(ImageReq(pid, csrf, Img.WebP(), fileName: "x.txt", contentType: "text/plain"))).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await c.SendAsync(ImageReq(pid, csrf, Img.JpegWithExifGps(), fileName: "x.png"))).StatusCode);
    }

    [Fact] // IT-09:超大、超像素
    public async Task Recognize_TooLarge_Are413()
    {
        var stub = new StubRecognizer();
        var (f, c, csrf, pid) = await Setup(stub, new() { ["Vision:MaxImageBytes"] = "3000" });
        using var _f = f;
        await AssertProblem(await c.SendAsync(ImageReq(pid, csrf, new byte[5000])), HttpStatusCode.RequestEntityTooLarge, "PAYLOAD_TOO_LARGE");
        await AssertProblem(await c.SendAsync(ImageReq(pid, csrf, new byte[200_000])), HttpStatusCode.RequestEntityTooLarge, "PAYLOAD_TOO_LARGE");
        Assert.Equal(0, stub.Calls);
    }

    [Theory] // IT-09:超像素(長邊、總像素)
    [InlineData(4097, 10)]
    [InlineData(4000, 4001)]
    public async Task Recognize_TooManyPixels_Are413(int w, int h)
    {
        var stub = new StubRecognizer();
        var (f, c, csrf, pid) = await Setup(stub);
        using var _f = f;
        await AssertProblem(await c.SendAsync(ImageReq(pid, csrf, Img.Png(w, h))), HttpStatusCode.RequestEntityTooLarge, "PAYLOAD_TOO_LARGE");
        Assert.Equal(0, stub.Calls);
    }

    [Fact] // IT-09:辨識結果
    public async Task Recognize_NothingRecognized_Is422()
    {
        foreach (var output in new[] { new RecognitionOutput(false, []), new RecognitionOutput(true, []), new RecognitionOutput(false, [new("2330", 1, 1)]) })
        {
            var (f, c, csrf, pid) = await Setup(new StubRecognizer((_, _, _) => Task.FromResult(output)));
            using var _f = f;
            await AssertProblem(await c.SendAsync(ImageReq(pid, csrf, Img.Png())), HttpStatusCode.UnprocessableEntity, "NOTHING_RECOGNIZED");
        }
    }

    [Fact] // IT-09:逾時、失敗
    public async Task Recognize_UpstreamTimeout_504_UpstreamFailure_502()
    {
        var (f, c, csrf, pid) = await Setup(new StubRecognizer((_, _, _) => throw new VisionTimeoutException()));
        using (f) await AssertProblem(await c.SendAsync(ImageReq(pid, csrf, Img.Png())), HttpStatusCode.GatewayTimeout, "UPSTREAM_TIMEOUT");
        var (f2, c2, csrf2, pid2) = await Setup(new StubRecognizer((_, _, _) => throw new VisionUpstreamException("x", 500)));
        using (f2) await AssertProblem(await c2.SendAsync(ImageReq(pid2, csrf2, Img.Png())), HttpStatusCode.BadGateway, "UPSTREAM_ERROR");
    }

    [Fact] // IT-09:第 11 次 429
    public async Task Recognize_11thCall_Is429_WithRetryAfter()
    {
        var (f, c, csrf, pid) = await Setup();
        using var _f = f;
        for (var i = 0; i < 10; i++)
            Assert.Equal(HttpStatusCode.OK, (await c.SendAsync(ImageReq(pid, csrf, Img.Png()))).StatusCode);
        var r = await c.SendAsync(ImageReq(pid, csrf, Img.Png()));
        await AssertProblem(r, HttpStatusCode.TooManyRequests, "RATE_LIMITED");
        var retry = int.Parse(r.Headers.GetValues("Retry-After").Single());
        Assert.InRange(retry, 1, 3600);

        // 其他使用者不受影響
        var (b, cb, _) = await A.SignInAsync(f, "bob", "User");
        var bp = await NewPortfolio(b, cb, "B");
        Assert.Equal(HttpStatusCode.OK, (await b.SendAsync(ImageReq(bp, cb, Img.Png()))).StatusCode);
    }

    [Fact] // IT-09:同時 1 個
    public async Task Recognize_SecondConcurrentRequest_Is429()
    {
        var entered = new TaskCompletionSource();
        var release = new TaskCompletionSource();
        var stub = new StubRecognizer(async (_, _, _) => { entered.TrySetResult(); await release.Task; return StubRecognizer.One(); });
        var (f, c, csrf, pid) = await Setup(stub);
        using var _f = f;
        var first = c.SendAsync(ImageReq(pid, csrf, Img.Png()));
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(20));
        var second = await c.SendAsync(ImageReq(pid, csrf, Img.Png()));
        await AssertProblem(second, HttpStatusCode.TooManyRequests, "RATE_LIMITED");
        Assert.True(second.Headers.Contains("Retry-After"));
        release.SetResult();
        Assert.Equal(HttpStatusCode.OK, (await first).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await c.SendAsync(ImageReq(pid, csrf, Img.Png()))).StatusCode); // 釋放後可再用
    }

    [Fact] // SEC-23:全站每日上限
    public async Task Recognize_SiteDailyLimit_Is429()
    {
        var (f, c, csrf, pid) = await Setup(extra: new() { ["Vision:MaxCallsPerDay"] = "2" });
        using var _f = f;
        var (b, cb, _) = await A.SignInAsync(f, "bob", "User");
        var bp = await NewPortfolio(b, cb, "B");
        Assert.Equal(HttpStatusCode.OK, (await c.SendAsync(ImageReq(pid, csrf, Img.Png()))).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await b.SendAsync(ImageReq(bp, cb, Img.Png()))).StatusCode);
        var r = await c.SendAsync(ImageReq(pid, csrf, Img.Png()));
        await AssertProblem(r, HttpStatusCode.TooManyRequests, "RATE_LIMITED");
        Assert.True(r.Headers.Contains("Retry-After"));
    }

    [Fact] // IT-09:未設定金鑰時功能停用,網站仍可啟動
    public async Task Recognize_WithoutApiKey_Is503_AndSiteStillWorks()
    {
        var (f, c, csrf, pid) = await Setup(enabled: false);
        using var _f = f;
        await AssertProblem(await c.SendAsync(ImageReq(pid, csrf, Img.Png())), HttpStatusCode.ServiceUnavailable, "UPSTREAM_ERROR");
        Assert.Equal(HttpStatusCode.OK, (await c.GetAsync("/api/portfolios")).StatusCode);
    }

    [Fact] // IT-11
    public async Task OtherUsersPortfolio_Recognize_And_Batch_Return404()
    {
        var (f, c, csrf, pid) = await Setup();
        using var _f = f;
        var (b, cb, _) = await A.SignInAsync(f, "bob", "User");
        await AssertProblem(await b.SendAsync(ImageReq(pid, cb, Img.Png())), HttpStatusCode.NotFound, "NOT_FOUND");
        var batch = await b.SendAsync(A.Req(HttpMethod.Post, $"/api/portfolios/{pid}/holdings/batch", cb,
            new { items = new[] { new { symbol = "2330", shares = 1, totalCost = 1 } } }));
        await AssertProblem(batch, HttpStatusCode.NotFound, "NOT_FOUND");
        using var s = f.Services.CreateScope();
        Assert.Equal(0, await s.ServiceProvider.GetRequiredService<AppDbContext>().Holdings.CountAsync());
    }
}
