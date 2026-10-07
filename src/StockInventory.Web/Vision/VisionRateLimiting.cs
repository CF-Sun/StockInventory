using System.Globalization;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.RateLimiting;
using StockInventory.Web.Security;

namespace StockInventory.Web.Vision;

/// <summary>標記需要套用辨識速率限制的端點(API-13)。</summary>
public sealed class VisionRateLimited;

/// <summary>
/// SEC-23:ASP.NET Core 內建 RateLimiter。串聯三個限制(都只作用於標記 <see cref="VisionRateLimited"/> 的端點):
/// 每使用者同時 1 個、每使用者每小時 N 次、全站每日 M 次(以台北時間日界分區,重啟歸零)。
/// 分區鍵為 ICurrentUser.UserId。429 以 problem+json 回應並附 Retry-After(秒)。
/// </summary>
public static class VisionRateLimiting
{
    private static readonly TimeSpan TaipeiOffset = TimeSpan.FromHours(8);
    private const int DefaultRetryAfterSeconds = 5;

    public static IServiceCollection AddVisionRateLimiter(this IServiceCollection services, IConfiguration cfg)
    {
        var perHour = Math.Max(1, cfg.GetValue("Vision:MaxCallsPerUserPerHour", 10));
        var perDay = Math.Max(1, cfg.GetValue("Vision:MaxCallsPerDay", 200));

        services.AddRateLimiter(o =>
        {
            o.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
            o.GlobalLimiter = PartitionedRateLimiter.CreateChained(
                // 順序:先同時數(被拒不消耗每小時額度),再每小時,最後每日
                PartitionedRateLimiter.Create<HttpContext, string>(ctx => UserPartition(ctx, "c", uid =>
                    new ConcurrencyLimiterOptions { PermitLimit = 1, QueueLimit = 0 })),
                PartitionedRateLimiter.Create<HttpContext, string>(ctx => UserHourPartition(ctx, perHour)),
                PartitionedRateLimiter.Create<HttpContext, string>(ctx => DayPartition(ctx, perDay)));

            o.OnRejected = async (context, ct) =>
            {
                var seconds = context.Lease.TryGetMetadata(MetadataName.RetryAfter, out var ra)
                    ? Math.Max(1, (int)Math.Ceiling(ra.TotalSeconds)) : DefaultRetryAfterSeconds;
                var http = context.HttpContext;
                http.Response.Headers.RetryAfter = seconds.ToString(CultureInfo.InvariantCulture);
                http.Response.Headers.CacheControl = "no-store";
                await Problems.WriteAsync(http, 429, "RATE_LIMITED", "操作過於頻繁,請稍後再試");
            };
        });
        return services;
    }

    private static bool IsVision(HttpContext ctx) =>
        ctx.GetEndpoint()?.Metadata.GetMetadata<VisionRateLimited>() is not null;

    private static Guid? CurrentUserId(HttpContext ctx)
    {
        var me = ctx.RequestServices.GetRequiredService<ICurrentUser>();
        return me.IsAuthenticated ? me.UserId : null;
    }

    private static RateLimitPartition<string> UserPartition(HttpContext ctx, string kind, Func<Guid, ConcurrencyLimiterOptions> opts)
    {
        if (!IsVision(ctx) || CurrentUserId(ctx) is not { } uid) return RateLimitPartition.GetNoLimiter("none");
        return RateLimitPartition.GetConcurrencyLimiter($"{kind}:{uid}", _ => opts(uid));
    }

    private static RateLimitPartition<string> UserHourPartition(HttpContext ctx, int perHour)
    {
        if (!IsVision(ctx) || CurrentUserId(ctx) is not { } uid) return RateLimitPartition.GetNoLimiter("none");
        return RateLimitPartition.GetFixedWindowLimiter($"h:{uid}", _ => new FixedWindowRateLimiterOptions
        {
            PermitLimit = perHour, Window = TimeSpan.FromHours(1), QueueLimit = 0, AutoReplenishment = true,
        });
    }

    private static RateLimitPartition<string> DayPartition(HttpContext ctx, int perDay)
    {
        if (!IsVision(ctx) || CurrentUserId(ctx) is null) return RateLimitPartition.GetNoLimiter("none");
        var now = ctx.RequestServices.GetRequiredService<TimeProvider>().GetUtcNow();
        var local = now + TaipeiOffset;
        var nextBoundary = new DateTimeOffset(local.Date.AddDays(1), TimeSpan.Zero) - TaipeiOffset;
        var remaining = nextBoundary - now;
        // 鍵含日期:換日即換新的限制器;視窗長度等於距日界剩餘時間,所以 Retry-After 即「到日界的秒數」
        return RateLimitPartition.GetFixedWindowLimiter($"d:{local:yyyyMMdd}", _ => new FixedWindowRateLimiterOptions
        {
            PermitLimit = perDay, Window = remaining < TimeSpan.FromSeconds(1) ? TimeSpan.FromSeconds(1) : remaining,
            QueueLimit = 0, AutoReplenishment = false,
        });
    }
}
