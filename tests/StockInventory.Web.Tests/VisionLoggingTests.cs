using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using static StockInventory.Web.Tests.VisionTests;
using A = StockInventory.Web.Tests.AdminApiTests;

namespace StockInventory.Web.Tests;

public class VisionLoggingTests
{
    const string ModelJson = """{"recognized":true,"rows":[{"symbol":"2330","shares":7654321,"totalCost":1357913}]}""";

    // IT-12
    [Fact]
    public async Task RecognizeAndBatch_DoNotLogSensitiveContent()
    {
        var sink = new CaptureSink();
        var handler = FakeHandler.Respond(HttpStatusCode.OK, FakeHandler.Envelope(ModelJson));
        using var f = Factory(extra: new() { ["Vision:Endpoint"] = "https://api.example.test/v1/messages" }, more: sv =>
        {
            sv.AddSingleton<Microsoft.Extensions.Logging.ILoggerFactory>(new CaptureLoggerFactory(sink));
            sv.AddHttpClient<StockInventory.Web.Vision.IHoldingImageRecognizer, StockInventory.Web.Vision.AnthropicHoldingRecognizer>()
                .ConfigurePrimaryHttpMessageHandler(() => handler);
        });
        await SeedInstruments(f);
        var (c, csrf, _) = await A.SignInAsync(f, "alice", "User");
        var pid = await NewPortfolio(c, csrf);

        var rec = await c.SendAsync(ImageReq(pid, csrf, Img.JpegWithExifGps(), fileName: "private-statement-name.jpg"));
        Assert.Equal(HttpStatusCode.OK, rec.StatusCode);
        var item = (await A.Json(rec)).GetProperty("items")[0];
        Assert.Equal(7654321, item.GetProperty("shares").GetInt64());
        Assert.Equal(1, handler.Calls);

        var batch = await c.SendAsync(A.Req(HttpMethod.Post, $"/api/portfolios/{pid}/holdings/batch", csrf,
            new { items = new[] { new { symbol = "2330", shares = 7654321, totalCost = 1357913 } } }));
        Assert.Equal(HttpStatusCode.Created, batch.StatusCode);

        // 失敗路徑也不得洩漏
        var bad = FakeHandler.Respond(HttpStatusCode.InternalServerError, "MODELSECRET-ERROR-BODY 2330 7654321");
        using var f2 = Factory(more: sv =>
        {
            sv.AddSingleton<Microsoft.Extensions.Logging.ILoggerFactory>(new CaptureLoggerFactory(sink));
            sv.AddHttpClient<StockInventory.Web.Vision.IHoldingImageRecognizer, StockInventory.Web.Vision.AnthropicHoldingRecognizer>()
                .ConfigurePrimaryHttpMessageHandler(() => bad);
        });
        var (c2, csrf2, _) = await A.SignInAsync(f2, "carol", "User");
        var pid2 = await NewPortfolio(c2, csrf2);
        await AssertProblem(await c2.SendAsync(ImageReq(pid2, csrf2, Img.Png())), HttpStatusCode.BadGateway, "UPSTREAM_ERROR");

        var logs = sink.All();
        Assert.Contains("截圖辨識", logs); // 確實有記錄(只含 UserId、結果碼、數量、耗時)
        foreach (var secret in new[] { "2330", "7654321", "1357913", "MODELSECRET", "private-statement-name", ".jpg", TestKey,
                     "SECRET-GPS", "recognized", "base64", "台積電" })
            Assert.DoesNotContain(secret, logs);
    }
}
