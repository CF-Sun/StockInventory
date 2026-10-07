using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using SkiaSharp;
using StockInventory.Core;
using StockInventory.Data;
using StockInventory.Quotes;
using StockInventory.Web.Vision;
using A = StockInventory.Web.Tests.AdminApiTests;

namespace StockInventory.Web.Tests;

/// <summary>測試用圖片產生器(不使用任何真實截圖)。</summary>
internal static class Img
{
    static byte[] Encode(int w, int h, SKColor color, SKEncodedImageFormat fmt, int quality = 90)
    {
        using var bmp = new SKBitmap(new SKImageInfo(w, h, SKColorType.Rgba8888, SKAlphaType.Premul));
        using (var c = new SKCanvas(bmp)) c.Clear(color);
        using var img = SKImage.FromBitmap(bmp);
        using var data = img.Encode(fmt, quality);
        return data.ToArray();
    }

    public static byte[] Png(int w = 32, int h = 32, SKColor? color = null) =>
        Encode(w, h, color ?? new SKColor(200, 30, 30), SKEncodedImageFormat.Png);

    public static byte[] Jpeg(int w = 32, int h = 32) => Encode(w, h, new SKColor(10, 120, 10), SKEncodedImageFormat.Jpeg);
    public static byte[] WebP() => Encode(32, 32, new SKColor(10, 10, 200), SKEncodedImageFormat.Webp);

    public const string ExifMarker = "SECRET-GPS-MARKER";

    /// <summary>在 JPEG 的 SOI 後插入帶有標記字串的 APP1(Exif)區段,用來驗證重新編碼後中繼資料已移除。</summary>
    public static byte[] JpegWithExifGps()
    {
        var jpeg = Jpeg();
        var payload = new List<byte>("Exif\0\0"u8.ToArray());
        payload.AddRange("II*\0"u8.ToArray());
        payload.AddRange(new byte[] { 8, 0, 0, 0, 0, 0 });
        payload.AddRange(System.Text.Encoding.ASCII.GetBytes(ExifMarker));
        var len = payload.Count + 2;
        var seg = new List<byte> { 0xFF, 0xE1, (byte)(len >> 8), (byte)(len & 0xFF) };
        seg.AddRange(payload);
        return [.. jpeg.Take(2), .. seg, .. jpeg.Skip(2)];
    }

    public static bool ContainsAscii(byte[] haystack, string needle) =>
        System.Text.Encoding.Latin1.GetString(haystack).Contains(needle, StringComparison.Ordinal);

    public static byte[] Heic() => [0, 0, 0, 0x18, (byte)'f', (byte)'t', (byte)'y', (byte)'p', (byte)'h', (byte)'e', (byte)'i', (byte)'c', 0, 0, 0, 0];
    public static byte[] Gif() => System.Text.Encoding.ASCII.GetBytes("GIF89a\x01\x00\x01\x00\x80\x00\x00\xff\xff\xff\x00\x00\x00;");
}

internal sealed class StubRecognizer(Func<byte[], string, CancellationToken, Task<RecognitionOutput>>? impl = null) : IHoldingImageRecognizer
{
    public int Calls;
    public byte[]? LastImage;
    public string? LastMime;

    public static RecognitionOutput One(string? symbol = "2330", decimal? shares = 1000, decimal? cost = 520000) =>
        new(true, [new RecognizedRow(symbol, shares, cost)]);

    public async Task<RecognitionOutput> RecognizeAsync(ReadOnlyMemory<byte> image, string mimeType, CancellationToken ct)
    {
        Interlocked.Increment(ref Calls);
        LastImage = image.ToArray();
        LastMime = mimeType;
        return impl is null ? One() : await impl(LastImage, mimeType, ct);
    }
}

internal sealed class FakeFetch : IFetchRequester
{
    public List<string> Symbols { get; } = [];
    public void RequestImmediateFetch(string symbol) { lock (Symbols) Symbols.Add(symbol); }
}

internal sealed class CaptureSink
{
    public List<string> Lines { get; } = [];
    public void Add(string s) { lock (Lines) Lines.Add(s); }
    public string All() { lock (Lines) return string.Join("\n", Lines); }
}

internal sealed class CaptureLoggerFactory(CaptureSink sink) : ILoggerFactory
{
    public ILogger CreateLogger(string categoryName) => new CaptureLogger(categoryName, sink);
    public void AddProvider(ILoggerProvider provider) { }
    public void Dispose() { }

    sealed class CaptureLogger(string category, CaptureSink sink) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Information;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel)) return;
            var sb = new StringBuilder($"[{category}] {formatter(state, exception)}");
            if (state is IEnumerable<KeyValuePair<string, object?>> kv)
                foreach (var p in kv) sb.Append(' ').Append(p.Key).Append('=').Append(p.Value);
            if (exception is not null) sb.Append(' ').Append(exception);
            sink.Add(sb.ToString());
        }
    }
}

internal sealed class FakeHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> impl) : HttpMessageHandler
{
    public HttpRequestMessage? LastRequest;
    public string? LastBody;
    public int Calls;

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        Interlocked.Increment(ref Calls);
        LastRequest = request;
        LastBody = request.Content is null ? null : await request.Content.ReadAsStringAsync(ct);
        return await impl(request, ct);
    }

    public static FakeHandler Respond(HttpStatusCode code, string body) =>
        new((_, _) => Task.FromResult(new HttpResponseMessage(code) { Content = new StringContent(body, Encoding.UTF8, "application/json") }));

    /// <summary>Anthropic Messages API 回應外殼,text 為模型輸出文字。</summary>
    public static string Envelope(string modelText, string stopReason = "end_turn", bool thinkingFirst = false)
    {
        var blocks = new List<object>();
        if (thinkingFirst) blocks.Add(new { type = "thinking", thinking = "", signature = "x" });
        blocks.Add(new { type = "text", text = modelText });
        return JsonSerializer.Serialize(new { id = "msg_test", type = "message", role = "assistant", content = blocks, stop_reason = stopReason });
    }
}

internal static class VisionTests
{
    public const string TestKey = "test-placeholder-not-a-real-key";

    public static TestFactory Factory(StubRecognizer? stub = null, Dictionary<string, string?>? extra = null,
        bool enabled = true, FakeFetch? fetch = null, Action<IServiceCollection>? more = null)
    {
        var settings = new Dictionary<string, string?>();
        if (enabled) settings["Vision:ApiKey"] = TestKey;
        foreach (var kv in extra ?? []) settings[kv.Key] = kv.Value;
        return new TestFactory(settings, sv =>
        {
            if (stub is not null) { sv.RemoveAll<IHoldingImageRecognizer>(); sv.AddSingleton<IHoldingImageRecognizer>(stub); }
            if (fetch is not null) { sv.RemoveAll<IFetchRequester>(); sv.AddSingleton<IFetchRequester>(fetch); }
            more?.Invoke(sv);
        });
    }

    public static async Task SeedInstruments(TestFactory f, int extra = 0)
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

    public static async Task<int> NewPortfolio(HttpClient c, string csrf, string name = "P")
    {
        var r = await c.SendAsync(A.Req(HttpMethod.Post, "/api/portfolios", csrf, new { name }));
        Assert.Equal(HttpStatusCode.Created, r.StatusCode);
        return (await A.Json(r)).GetProperty("id").GetInt32();
    }

    public static HttpRequestMessage ImageReq(int pid, string? csrf, byte[]? image, string? consent = "true",
        string fileName = "shot.png", string contentType = "image/png", int files = 1)
    {
        var mc = new MultipartFormDataContent();
        if (consent is not null) mc.Add(new StringContent(consent), "consent");
        for (var i = 0; i < files && image is not null; i++)
        {
            var bc = new ByteArrayContent(image);
            bc.Headers.ContentType = MediaTypeHeaderValue.Parse(contentType);
            mc.Add(bc, "image", fileName);
        }
        var r = new HttpRequestMessage(HttpMethod.Post, $"/api/portfolios/{pid}/holdings/recognize") { Content = mc };
        if (csrf is not null) r.Headers.Add("X-CSRF-TOKEN", csrf);
        return r;
    }

    public static async Task AssertProblem(HttpResponseMessage r, HttpStatusCode status, string code)
    {
        Assert.Equal(status, r.StatusCode);
        Assert.Equal("application/problem+json", r.Content.Headers.ContentType?.MediaType);
        Assert.Equal(code, (await A.Json(r)).GetProperty("code").GetString());
    }
}
