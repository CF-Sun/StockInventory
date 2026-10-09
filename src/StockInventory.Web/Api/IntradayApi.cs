using System.Globalization;
using System.Text.RegularExpressions;
using StockInventory.Quotes;
using StockInventory.Web.Security;
using StockInventory.Web.Services;

namespace StockInventory.Web.Api;

/// <summary>API-15 GET /api/intraday(FR-26,SPEC §8.5)。</summary>
public static partial class IntradayApi
{
    public const int MaxSymbols = 50;

    [GeneratedRegex("^[0-9A-Z]{1,10}$")]
    private static partial Regex SymbolPattern();

    public static void MapIntradayApi(this WebApplication app)
    {
        app.MapGet("/api/intraday", HandleAsync).RequireAuthorization();
    }

    private static async Task<IResult> HandleAsync(string? symbols, string? tradeDate, string? sinceUtc,
        IIntradayService service, IntradayConfig config, IntradayRateLimiter limiter, ICurrentUser me,
        ILoggerFactory loggers, HttpContext ctx, CancellationToken ct)
    {
        ctx.Response.Headers.CacheControl = "no-store"; // 含使用者持股相關內容,所有回應(含錯誤)都不可快取

        // 總開關:前端據此靜默隱藏圖區
        if (!config.Value.Enabled)
            return ApiResults.Problem(ctx, 503, "FEATURE_DISABLED", "走勢功能已關閉");

        if (!limiter.TryAcquire(me.UserId, out var retryAfter))
        {
            ctx.Response.Headers.RetryAfter = retryAfter.ToString(CultureInfo.InvariantCulture);
            return ApiResults.Problem(ctx, 429, "RATE_LIMITED", "操作過於頻繁,請稍後再試");
        }

        var errors = new Dictionary<string, string[]>();
        var list = ParseSymbols(symbols, errors);
        DateOnly? date = null;
        if (tradeDate is not null)
        {
            if (DateOnly.TryParseExact(tradeDate, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d)) date = d;
            else errors["tradeDate"] = ["tradeDate 格式必須是 yyyy-MM-dd"];
        }
        DateTime? since = null;
        if (sinceUtc is not null)
        {
            if (sinceUtc.EndsWith('Z') && DateTime.TryParse(sinceUtc, CultureInfo.InvariantCulture,
                    DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var s))
                since = DateTime.SpecifyKind(s, DateTimeKind.Utc);
            else errors["sinceUtc"] = ["sinceUtc 必須是 UTC ISO 8601(結尾為 Z)"];
        }
        if ((tradeDate is null) != (sinceUtc is null) && !errors.ContainsKey("tradeDate") && !errors.ContainsKey("sinceUtc"))
            errors["sinceUtc"] = ["tradeDate 與 sinceUtc 必須一起提供"];
        if (errors.Count > 0) return ApiResults.Invalid(ctx, errors);

        try
        {
            var result = await service.GetAsync(me.UserId, list, date, since, ct);
            return result is null ? ApiResults.NotFound(ctx) : Results.Json(result);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return Results.StatusCode(499); // 用戶端已中止
        }
        catch (Exception ex)
        {
            // Q14:只影響走勢圖區;細節只寫日誌(不含代號與使用者),不回傳給用戶端
            loggers.CreateLogger("Intraday").LogError(ex, "API-15 走勢查詢失敗");
            return ApiResults.Problem(ctx, 500, "INTERNAL_ERROR", "發生未預期的錯誤,請稍後再試");
        }
    }

    /// <summary>去除空白、轉大寫、去重(保留請求順序);1 至 50 個,每個 1 至 10 字元的 [0-9A-Z]。</summary>
    private static List<string> ParseSymbols(string? raw, Dictionary<string, string[]> errors)
    {
        var list = new List<string>();
        if (!string.IsNullOrWhiteSpace(raw))
        {
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var part in raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                var s = part.ToUpperInvariant();
                if (!SymbolPattern().IsMatch(s))
                {
                    errors["symbols"] = ["symbols 含有不合法的代號(1 至 10 字元的英數字)"];
                    return [];
                }
                if (seen.Add(s)) list.Add(s);
            }
        }
        if (list.Count == 0) errors.TryAdd("symbols", ["symbols 不可為空"]);
        else if (list.Count > MaxSymbols) errors["symbols"] = [$"symbols 最多 {MaxSymbols} 個"];
        return list;
    }
}
