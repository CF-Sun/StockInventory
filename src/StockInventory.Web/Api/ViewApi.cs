using StockInventory.Web.Security;
using StockInventory.Web.Services;

namespace StockInventory.Web.Api;

public static class ViewApi
{
    public static void MapViewApi(this WebApplication app)
    {
        // API-06:portfolioIds 省略 = 全部;merge 預設 true
        app.MapGet("/api/holdings", async (string? portfolioIds, bool? merge, ViewService views, ICurrentUser me, CancellationToken ct) =>
            Results.Json(await views.BuildAsync(me.UserId, ViewService.ParseIds(portfolioIds), merge ?? true, ct)))
            .RequireAuthorization();
    }
}
