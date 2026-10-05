using Microsoft.EntityFrameworkCore;
using StockInventory.Core;
using StockInventory.Data;
using StockInventory.Quotes;

namespace StockInventory.Web;

public static class HealthEndpoint
{
    public static async Task<IResult> HandleAsync(AppDbContext db, IMarketStatusSource market, ILogger<AppDbContext> log)
    {
        var dbOk = false;
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            dbOk = await db.Database.CanConnectAsync(cts.Token);
        }
        catch (Exception ex)
        {
            log.LogWarning("健康檢查:資料庫連線失敗 ({Type})", ex.GetType().Name); // 不記錄細節與連線字串
        }

        var m = market.Get();
        var down = !dbOk || (m.State == MarketState.Open && m.Stale);
        var body = new
        {
            status = down ? "down" : "ok",
            db = dbOk ? "ok" : "down",
            quotes = new
            {
                state = m.State.ToString().ToLowerInvariant(),
                stale = m.Stale,
                lastFetchedAtUtc = m.LastFetchedAtUtc is { } t ? DateTime.SpecifyKind(t, DateTimeKind.Utc) : (DateTime?)null,
            },
        };
        return Results.Json(body, statusCode: down ? 503 : 200);
    }
}
