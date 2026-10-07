using System.Diagnostics;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Microsoft.Net.Http.Headers;
using StockInventory.Core;
using StockInventory.Data;
using StockInventory.Web.Options;
using StockInventory.Web.Security;
using StockInventory.Web.Services;
using StockInventory.Web.Vision;
using static StockInventory.Web.Api.ApiResults;

namespace StockInventory.Web.Api;

public sealed record BatchHoldingItem(string? Symbol, decimal? Shares, decimal? TotalCost, string? OnExists);
public sealed record BatchHoldingsRequest(List<BatchHoldingItem>? Items);

/// <summary>FR-25:API-13 截圖辨識(不寫 DB)、API-14 批次確認寫入。</summary>
public static class HoldingImportApi
{
    private const int MaxBatchItems = 200;
    private const int MultipartOverheadBytes = 64 * 1024;

    public static void MapHoldingImportApi(this WebApplication app)
    {
        var g = app.MapGroup("/api").RequireAuthorization();

        g.MapPost("/portfolios/{id:int}/holdings/recognize", Recognize)
            .WithMetadata(new VisionRateLimited());

        g.MapPost("/portfolios/{id:int}/holdings/batch", Batch);
    }

    // ---------- API-13 ----------
    private static async Task<IResult> Recognize(int id, HttpContext ctx, AppDbContext db, ICurrentUser me,
        IOptions<VisionOptions> vision, IOptions<LimitsOptions> limits, IHoldingImageRecognizer recognizer,
        ILoggerFactory lf)
    {
        var log = lf.CreateLogger("FR25.Recognize");
        var sw = Stopwatch.StartNew();
        var uid = me.UserId;
        ctx.Response.Headers.CacheControl = "no-store";
        var opt = vision.Value;

        // SEC-20:日誌只記 UserId、耗時、結果碼、數量
        IResult Done(IResult r, int status, int count = 0)
        {
            log.LogInformation("截圖辨識 UserId={UserId} Status={Status} Count={Count} ElapsedMs={Ms}", uid, status, count, sw.ElapsedMilliseconds);
            return r;
        }

        if (!opt.IsEnabled) return Done(Problem(ctx, 503, "UPSTREAM_ERROR", "辨識功能尚未啟用"), 503);

        var p = await db.Portfolios.AsNoTracking().FirstOrDefaultAsync(x => x.PortfolioId == id && x.UserId == uid, ctx.RequestAborted);
        if (p is null) return Done(NotFound(ctx), 404);

        // 請求大小上限(Kestrel 層):略大於 MaxImageBytes 加表單開銷
        var maxBody = (long)opt.MaxImageBytes + MultipartOverheadBytes;
        if (ctx.Request.ContentLength > maxBody) return Done(TooLarge(ctx), 413);
        var sizeFeature = ctx.Features.Get<IHttpMaxRequestBodySizeFeature>();
        if (sizeFeature is { IsReadOnly: false }) sizeFeature.MaxRequestBodySize = maxBody;

        // 1. 以串流解析 multipart,全程記憶體(不使用 ReadFormAsync,避免大檔被緩衝到暫存檔;SEC-19)
        MultipartResult form;
        try { form = await ReadMultipartAsync(ctx.Request, opt.MaxImageBytes, ctx.RequestAborted); }
        catch (BadHttpRequestException ex) when (ex.StatusCode == StatusCodes.Status413PayloadTooLarge)
        { return Done(TooLarge(ctx), 413); }
        catch (Exception ex) when (ex is InvalidDataException or IOException or BadHttpRequestException)
        { return Done(Invalid(ctx, "image", "請以 multipart/form-data 上傳一張圖片"), 400); }

        if (form.Error is not null) return Done(Invalid(ctx, "image", form.Error), 400);
        if (!string.Equals(form.Consent?.Trim(), "true", StringComparison.OrdinalIgnoreCase))
            return Done(Problem(ctx, 400, "CONSENT_REQUIRED", "請先勾選同意傳送至第三方 AI 辨識服務"), 400);
        if (form.TooLarge) return Done(TooLarge(ctx), 413);
        if (form.Image is null) return Done(Invalid(ctx, "image", "請選擇一張圖片"), 400);

        // 2. 驗證並重新編碼(去 EXIF)
        SanitizedImage clean;
        try { clean = ImageSanitizer.Sanitize(form.Image); }
        catch (ImageRejectedException ex) when (ex.Reason == ImageRejection.TooLarge) { return Done(TooLarge(ctx), 413); }
        catch (ImageRejectedException) { return Done(Unsupported(ctx), 415); }

        // 3. 呼叫視覺服務
        RecognitionOutput output;
        try { output = await recognizer.RecognizeAsync(clean.Bytes, clean.MimeType, ctx.RequestAborted); }
        catch (VisionTimeoutException) { return Done(Problem(ctx, 504, "UPSTREAM_TIMEOUT", "辨識逾時,請重試"), 504); }
        catch (VisionUpstreamException) { return Done(Problem(ctx, 502, "UPSTREAM_ERROR", "辨識服務暫時無法使用,請稍後再試或手動新增"), 502); }
        catch (OperationCanceledException) when (ctx.RequestAborted.IsCancellationRequested) { return Done(Results.StatusCode(499), 499); }

        if (!output.Recognized || output.Rows.Count == 0)
            return Done(Problem(ctx, 422, "NOTHING_RECOGNIZED", "無法在圖片中找到持股資料,請確認是庫存截圖且內容清晰"), 422);

        // 4. 代號對應主檔、同庫存既有持股 → 草稿(不寫 DB)
        var symbols = output.Rows.Take(RecognitionNormalizer.MaxRows).Select(r => RecognitionNormalizer.SanitizeSymbol(r.Symbol))
            .Where(s => s is not null).Select(s => s!).Distinct().ToList();
        var instruments = (await db.Instruments.AsNoTracking().Where(i => symbols.Contains(i.Symbol)).ToListAsync(ctx.RequestAborted))
            .ToDictionary(i => i.Symbol, i => new InstrumentInfo(i.Name, i.IsActive));
        var existing = (await db.Holdings.AsNoTracking().Where(h => h.PortfolioId == id && symbols.Contains(h.Symbol)).ToListAsync(ctx.RequestAborted))
            .ToDictionary(h => h.Symbol, h => new ExistingHolding(h.HoldingId, h.Shares, h.TotalCost));
        var items = RecognitionNormalizer.Build(output.Rows, instruments, existing);

        var holdingCount = await db.Holdings.CountAsync(h => h.PortfolioId == id, ctx.RequestAborted);
        var remaining = Math.Max(0, limits.Value.MaxHoldingsPerPortfolio - holdingCount);
        return Done(Results.Json(new { recognizedCount = items.Count, remainingSlots = remaining, items }), 200, items.Count);
    }

    private sealed record MultipartResult(byte[]? Image, string? Consent, bool TooLarge, string? Error);

    private static async Task<MultipartResult> ReadMultipartAsync(HttpRequest req, int maxImageBytes, CancellationToken ct)
    {
        if (!MediaTypeHeaderValue.TryParse(req.ContentType, out var mt)
            || !string.Equals(mt.MediaType.Value, "multipart/form-data", StringComparison.OrdinalIgnoreCase))
            return new(null, null, false, "請以 multipart/form-data 上傳一張圖片");
        var boundary = HeaderUtilities.RemoveQuotes(mt.Boundary).Value;
        if (string.IsNullOrEmpty(boundary) || boundary.Length > 128)
            return new(null, null, false, "請以 multipart/form-data 上傳一張圖片");

        var reader = new MultipartReader(boundary, req.Body) { HeadersCountLimit = 16, HeadersLengthLimit = 16 * 1024 };
        byte[]? image = null;
        string? consent = null;
        var files = 0;
        var tooLarge = false;
        while (await reader.ReadNextSectionAsync(ct) is { } section)
        {
            if (!ContentDispositionHeaderValue.TryParse(section.ContentDisposition, out var cd)) continue;
            var name = HeaderUtilities.RemoveQuotes(cd.Name).Value;
            var isFile = cd.FileName.HasValue || cd.FileNameStar.HasValue; // 檔名本身一律不使用
            if (isFile)
            {
                if (++files > 1) return new(null, consent, false, "一次只能上傳一張圖片");
                if (!string.Equals(name, "image", StringComparison.Ordinal)) continue;
                using var buf = new MemoryStream();
                var chunk = new byte[81920];
                int n;
                while ((n = await section.Body.ReadAsync(chunk, ct)) > 0)
                {
                    if (buf.Length + n > maxImageBytes) { tooLarge = true; break; }
                    buf.Write(chunk, 0, n);
                }
                if (tooLarge) return new(null, consent, true, null); // 不再讀取其餘內容
                image = buf.ToArray();
            }
            else if (string.Equals(name, "consent", StringComparison.Ordinal))
            {
                using var sr = new StreamReader(section.Body);
                var chars = new char[16];
                var read = await sr.ReadBlockAsync(chars, 0, chars.Length);
                consent = new string(chars, 0, read);
            }
        }
        return new(image, consent, false, null);
    }

    private static IResult TooLarge(HttpContext ctx) => Problem(ctx, 413, "PAYLOAD_TOO_LARGE", "僅支援 JPG、PNG、WebP,且大小不超過 5 MB");
    private static IResult Unsupported(HttpContext ctx) => Problem(ctx, 415, "UNSUPPORTED_MEDIA", "僅支援 JPG、PNG、WebP,且大小不超過 5 MB");

    // ---------- API-14 ----------
    private static async Task<IResult> Batch(int id, BatchHoldingsRequest r, AppDbContext db, ICurrentUser me,
        HttpContext ctx, HoldingService svc, ILoggerFactory lf)
    {
        var uid = me.UserId;
        var p = await db.Portfolios.AsNoTracking().FirstOrDefaultAsync(x => x.PortfolioId == id && x.UserId == uid);
        if (p is null) return NotFound(ctx);

        var items = r.Items;
        if (items is null || items.Count is < 1 or > MaxBatchItems)
            return Invalid(ctx, "items", $"items 必須是 1 到 {MaxBatchItems} 筆");

        var inputs = new List<HoldingWriteInput>(items.Count);
        var errors = new Dictionary<string, string[]>();
        for (var i = 0; i < items.Count; i++)
        {
            var it = items[i];
            if (it is null) { errors[$"items[{i}]"] = ["項目不可為空"]; inputs.Add(new(null, null, null, ExistsMode.Skip)); continue; }
            var mode = (it.OnExists ?? "skip").Trim().ToLowerInvariant() switch
            {
                "skip" => (ExistsMode?)ExistsMode.Skip,
                "update" => ExistsMode.Update,
                _ => null,
            };
            if (mode is null) errors[$"items[{i}].onExists"] = ["onExists 必須是 skip 或 update"];
            inputs.Add(new(it.Symbol, it.Shares, it.TotalCost, mode ?? ExistsMode.Skip));
        }
        if (errors.Count > 0) return Invalid(ctx, errors);

        var res = await svc.ApplyAsync(uid, p, inputs, indexedKeys: true);
        switch (res.Status)
        {
            case HoldingApplyStatus.Invalid: return Invalid(ctx, res.Errors);
            case HoldingApplyStatus.Duplicate: return Problem(ctx, 409, "DUPLICATE", "此庫存已有相同標的");
            case HoldingApplyStatus.LimitExceeded:
                return Problem(ctx, 422, "LIMIT_EXCEEDED", $"每個庫存最多 {res.MaxHoldings} 檔持股");
        }

        // SEC-20:只記數量
        lf.CreateLogger("FR25.Batch").LogInformation("批次新增持股 UserId={UserId} Created={C} Updated={U} Skipped={S}",
            uid, res.Created.Count, res.Updated.Count, res.Skipped.Count);
        return Results.Json(new
        {
            created = res.Created.Select(Row),
            updated = res.Updated.Select(Row),
            skipped = res.Skipped.Select(s => new { symbol = s.Symbol, reason = s.Reason }),
        }, statusCode: 201);
    }

    private static object Row(Holding h) => new { holdingId = h.HoldingId, symbol = h.Symbol, shares = h.Shares, totalCost = h.TotalCost };
}
