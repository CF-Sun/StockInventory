using Microsoft.EntityFrameworkCore;
using StockInventory.Core;
using StockInventory.Data;
using StockInventory.Web.Security;
using static StockInventory.Web.Api.ApiResults;

namespace StockInventory.Web.Api;

public sealed record SettingsRequest(int? ColorScheme, bool? DeductFees, decimal? FeeRate, decimal? FeeDiscount, int[]? SelectedPortfolioIds);
public sealed record SettingsDto(int ColorScheme, bool DeductFees, decimal FeeRate, decimal FeeDiscount, int[]? SelectedPortfolioIds);

public static class SettingsApi
{
    public static void MapSettingsApi(this WebApplication app)
    {
        var g = app.MapGroup("/api/settings").RequireAuthorization();

        g.MapGet("", async (AppDbContext db, ICurrentUser me) =>
        {
            var s = await db.UserSettings.AsNoTracking().FirstOrDefaultAsync(x => x.UserId == me.UserId);
            return Results.Json(s is null ? Defaults() : ToDto(s));
        });

        // 整份覆寫:所有欄位都必須提供
        g.MapPut("", async (SettingsRequest r, AppDbContext db, ICurrentUser me, HttpContext ctx) =>
        {
            var uid = me.UserId;
            var errors = new Dictionary<string, string[]>();
            if (r.ColorScheme is null || Validation.ColorScheme(r.ColorScheme.Value) is not null) errors["colorScheme"] = ["顏色慣例必須是 0 或 1"];
            if (r.DeductFees is null) errors["deductFees"] = ["請指定是否扣費"];
            if (r.FeeRate is null || Validation.FeeRate(r.FeeRate.Value) is not null) errors["feeRate"] = ["手續費率必須介於 0 到 0.01"];
            if (r.FeeDiscount is null || Validation.FeeDiscount(r.FeeDiscount.Value) is not null) errors["feeDiscount"] = ["手續費折扣必須介於 0.001 到 1"];
            if (errors.Count > 0) return Invalid(ctx, errors);

            // 不屬於自己的庫存 Id 會被丟棄;去除重複;空集合視為 NULL(= 全部)
            string? selected = null;
            if (r.SelectedPortfolioIds is { Length: > 0 } ids)
            {
                var owned = (await db.Portfolios.AsNoTracking().Where(p => p.UserId == uid).Select(p => p.PortfolioId).ToListAsync()).ToHashSet();
                var kept = ids.Where(owned.Contains).Distinct().ToList();
                selected = kept.Count == 0 ? null : string.Join(',', kept);
            }

            var s = await db.UserSettings.FirstOrDefaultAsync(x => x.UserId == uid);
            if (s is null) { s = new UserSettings { UserId = uid }; db.UserSettings.Add(s); }
            s.ColorScheme = (byte)r.ColorScheme!.Value;
            s.DeductFees = r.DeductFees!.Value;
            s.FeeRate = r.FeeRate!.Value;
            s.FeeDiscount = r.FeeDiscount!.Value;
            s.SelectedPortfolioIds = selected;
            s.UpdatedAtUtc = DateTime.UtcNow;
            await db.SaveChangesAsync();
            return Results.Json(ToDto(s));
        });
    }

    private static SettingsDto Defaults() => new(0, false, 0.001425m, 1.000m, null);

    private static SettingsDto ToDto(UserSettings s) => new(s.ColorScheme, s.DeductFees, s.FeeRate, s.FeeDiscount,
        string.IsNullOrEmpty(s.SelectedPortfolioIds) ? null
            : s.SelectedPortfolioIds.Split(',', StringSplitOptions.RemoveEmptyEntries).Select(int.Parse).ToArray());
}
