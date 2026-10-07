using StockInventory.Web.Hubs;
using StockInventory.Web.Security;
using StockInventory.Web.Services;

namespace StockInventory.Web.Api;

public static class ViewApi
{
    public static void MapViewApi(this WebApplication app)
    {
        // API-06:portfolioIds 省略 = 全部;merge 預設 true
        app.MapGet("/api/holdings", async (string? portfolioIds, bool? merge, ViewService views, ICurrentUser me,
                ConnectionRegistry registry, HttpContext ctx, CancellationToken ct) =>
            {
                registry.TouchPolling(); // 輪詢備援也算「有人在看」(見 ConnectionRegistry)
                ctx.Response.Headers.CacheControl = "no-store"; // 股價資料不可被瀏覽器或代理快取
                return Results.Json(await views.BuildAsync(me.UserId, ViewService.ParseIds(portfolioIds), merge ?? true, ct));
            })
            .RequireAuthorization();
    }
}
