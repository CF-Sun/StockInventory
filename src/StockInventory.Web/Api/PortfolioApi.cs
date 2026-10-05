using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using StockInventory.Core;
using StockInventory.Data;
using StockInventory.Quotes;
using StockInventory.Web.Options;
using StockInventory.Web.Security;
using static StockInventory.Web.Api.ApiResults;

namespace StockInventory.Web.Api;

public sealed record PortfolioRequest(string? Name);
public sealed record OrderRequest(List<int>? Ids);
public sealed record HoldingCreateRequest(string? Symbol, decimal? TotalCost, decimal? Shares);
public sealed record HoldingUpdateRequest(decimal? TotalCost, decimal? Shares);

public static class PortfolioApi
{
    public static void MapPortfolioApi(this WebApplication app)
    {
        var g = app.MapGroup("/api").RequireAuthorization();

        g.MapGet("/portfolios", async (AppDbContext db, ICurrentUser me) =>
        {
            var uid = me.UserId;
            var list = await db.Portfolios.AsNoTracking().Where(p => p.UserId == uid)
                .OrderBy(p => p.SortOrder)
                .Select(p => new PortfolioDto(p.PortfolioId, p.Name, p.SortOrder, p.Holdings.Count)).ToListAsync();
            return Results.Json(list);
        });

        g.MapPost("/portfolios", async (PortfolioRequest r, AppDbContext db, ICurrentUser me, HttpContext ctx,
            IOptions<LimitsOptions> limits) =>
        {
            var uid = me.UserId;
            var name = Validation.NormalizePortfolioName(r.Name);
            if (Validation.PortfolioName(name) is { } err) return Invalid(ctx, "name", err);

            if (await db.Portfolios.CountAsync(p => p.UserId == uid) >= limits.Value.MaxPortfoliosPerUser)
                return Problem(ctx, 422, "LIMIT_EXCEEDED", $"每人最多 {limits.Value.MaxPortfoliosPerUser} 個庫存");
            if (await db.Portfolios.AnyAsync(p => p.UserId == uid && p.Name == name))
                return Problem(ctx, 409, "DUPLICATE", "庫存名稱已存在");

            var max = await db.Portfolios.Where(p => p.UserId == uid).Select(p => (int?)p.SortOrder).MaxAsync() ?? 0;
            var now = DateTime.UtcNow;
            var p = new Portfolio { UserId = uid, Name = name, SortOrder = max + 1, CreatedAtUtc = now, UpdatedAtUtc = now };
            db.Portfolios.Add(p);
            try { await db.SaveChangesAsync(); }
            catch (DbUpdateException) { return Problem(ctx, 409, "DUPLICATE", "庫存名稱已存在"); }
            return Results.Json(new PortfolioDto(p.PortfolioId, p.Name, p.SortOrder, 0), statusCode: 201);
        });

        // 固定路徑 /order 先於 {id:int}
        g.MapPut("/portfolios/order", async (OrderRequest r, AppDbContext db, ICurrentUser me, HttpContext ctx) =>
        {
            var uid = me.UserId;
            var owned = await db.Portfolios.Where(p => p.UserId == uid).ToListAsync();
            var ids = r.Ids ?? [];
            if (ids.Count != owned.Count || ids.Distinct().Count() != ids.Count
                || !ids.ToHashSet().SetEquals(owned.Select(p => p.PortfolioId)))
                return Invalid(ctx, "ids", "ids 必須恰好等於所有庫存 Id,不多不少、不重複");

            var now = DateTime.UtcNow;
            for (var i = 0; i < ids.Count; i++)
            {
                var p = owned.First(x => x.PortfolioId == ids[i]);
                if (p.SortOrder != i + 1) { p.SortOrder = i + 1; p.UpdatedAtUtc = now; }
            }
            await db.SaveChangesAsync();
            return Results.NoContent();
        });

        g.MapPut("/portfolios/{id:int}", async (int id, PortfolioRequest r, AppDbContext db, ICurrentUser me, HttpContext ctx) =>
        {
            var uid = me.UserId;
            var p = await db.Portfolios.FirstOrDefaultAsync(x => x.PortfolioId == id && x.UserId == uid);
            if (p is null) return NotFound(ctx);
            var name = Validation.NormalizePortfolioName(r.Name);
            if (Validation.PortfolioName(name) is { } err) return Invalid(ctx, "name", err);
            if (await db.Portfolios.AnyAsync(x => x.UserId == uid && x.Name == name && x.PortfolioId != id))
                return Problem(ctx, 409, "DUPLICATE", "庫存名稱已存在");

            p.Name = name;
            p.UpdatedAtUtc = DateTime.UtcNow;
            try { await db.SaveChangesAsync(); }
            catch (DbUpdateException) { return Problem(ctx, 409, "DUPLICATE", "庫存名稱已存在"); }
            var count = await db.Holdings.CountAsync(h => h.PortfolioId == id);
            return Results.Json(new PortfolioDto(p.PortfolioId, p.Name, p.SortOrder, count));
        });

        // §5.4:每筆持股各寫一筆 D、刪除庫存、並從 SelectedPortfolioIds 移除;同一次 SaveChanges 為單一交易
        g.MapDelete("/portfolios/{id:int}", async (int id, AppDbContext db, ICurrentUser me, HttpContext ctx) =>
        {
            var uid = me.UserId;
            var p = await db.Portfolios.Include(x => x.Holdings).FirstOrDefaultAsync(x => x.PortfolioId == id && x.UserId == uid);
            if (p is null) return NotFound(ctx);

            var now = DateTime.UtcNow;
            foreach (var h in p.Holdings)
                db.HoldingChanges.Add(Change(uid, p, h.Symbol, 'D', h, null, now));

            var settings = await db.UserSettings.FirstOrDefaultAsync(s => s.UserId == uid);
            if (settings?.SelectedPortfolioIds is { Length: > 0 } sel)
            {
                var kept = sel.Split(',', StringSplitOptions.RemoveEmptyEntries).Where(x => x != id.ToString()).ToList();
                settings.SelectedPortfolioIds = kept.Count == 0 ? null : string.Join(',', kept);
                settings.UpdatedAtUtc = now;
            }
            db.Portfolios.Remove(p); // Holdings 由 FK 級聯刪除
            await db.SaveChangesAsync();
            return Results.NoContent();
        });

        // 規格外的輔助端點:列出單一庫存的持股(不含價格),供 /Portfolios 頁面使用
        g.MapGet("/portfolios/{id:int}/holdings", async (int id, AppDbContext db, ICurrentUser me, HttpContext ctx) =>
        {
            var uid = me.UserId;
            if (!await db.Portfolios.AnyAsync(x => x.PortfolioId == id && x.UserId == uid)) return NotFound(ctx);
            var rows = await db.Holdings.AsNoTracking().Where(h => h.PortfolioId == id)
                .OrderBy(h => h.Symbol)
                .Select(h => new HoldingListDto(h.HoldingId, h.Symbol, h.Instrument!.Name, h.TotalCost, h.Shares)).ToListAsync();
            return Results.Json(rows);
        });

        g.MapPost("/portfolios/{id:int}/holdings", async (int id, HoldingCreateRequest r, AppDbContext db, ICurrentUser me,
            HttpContext ctx, IOptions<LimitsOptions> limits, IFetchRequester fetch) =>
        {
            var uid = me.UserId;
            var p = await db.Portfolios.AsNoTracking().FirstOrDefaultAsync(x => x.PortfolioId == id && x.UserId == uid);
            if (p is null) return NotFound(ctx);

            var symbol = Validation.NormalizeSymbol(r.Symbol);
            var errors = new Dictionary<string, string[]>();
            if (Validation.Symbol(symbol) is { } e1) errors["symbol"] = [e1];
            if (Validation.TotalCost(r.TotalCost) is { } e2) errors["totalCost"] = [e2];
            if (Validation.Shares(r.Shares) is { } e3) errors["shares"] = [e3];
            if (errors.Count == 0 && !await db.Instruments.AnyAsync(i => i.Symbol == symbol && i.IsActive))
                errors["symbol"] = ["找不到此標的"];
            if (errors.Count > 0) return Invalid(ctx, errors);

            if (await db.Holdings.AnyAsync(h => h.PortfolioId == id && h.Symbol == symbol))
                return Problem(ctx, 409, "DUPLICATE", "此庫存已有相同標的");
            if (await db.Holdings.CountAsync(h => h.PortfolioId == id) >= limits.Value.MaxHoldingsPerPortfolio)
                return Problem(ctx, 422, "LIMIT_EXCEEDED", $"每個庫存最多 {limits.Value.MaxHoldingsPerPortfolio} 檔持股");

            var now = DateTime.UtcNow;
            var h = new Holding { PortfolioId = id, Symbol = symbol, TotalCost = (long)r.TotalCost!.Value,
                Shares = (long)r.Shares!.Value, CreatedAtUtc = now, UpdatedAtUtc = now };
            db.Holdings.Add(h);
            db.HoldingChanges.Add(Change(uid, p, symbol, 'A', null, h, now));
            try { await db.SaveChangesAsync(); }
            catch (DbUpdateException) { return Problem(ctx, 409, "DUPLICATE", "此庫存已有相同標的"); }

            fetch.RequestImmediateFetch(symbol);
            return Results.Json(HoldingDto.From(h), statusCode: 201);
        });

        g.MapPut("/holdings/{id:int}", async (int id, HoldingUpdateRequest r, AppDbContext db, ICurrentUser me, HttpContext ctx) =>
        {
            var uid = me.UserId;
            var h = await OwnedHolding(db, id, uid).Include(x => x.Portfolio).FirstOrDefaultAsync();
            if (h is null) return NotFound(ctx);

            var errors = new Dictionary<string, string[]>();
            if (Validation.TotalCost(r.TotalCost) is { } e2) errors["totalCost"] = [e2];
            if (Validation.Shares(r.Shares) is { } e3) errors["shares"] = [e3];
            if (errors.Count > 0) return Invalid(ctx, errors);

            var cost = (long)r.TotalCost!.Value;
            var shares = (long)r.Shares!.Value;
            if (cost != h.TotalCost || shares != h.Shares) // 值沒變時不寫、也不報錯
            {
                var old = new Holding { TotalCost = h.TotalCost, Shares = h.Shares };
                var now = DateTime.UtcNow;
                h.TotalCost = cost;
                h.Shares = shares;
                h.UpdatedAtUtc = now;
                db.HoldingChanges.Add(Change(uid, h.Portfolio!, h.Symbol, 'U', old, h, now));
                await db.SaveChangesAsync();
            }
            return Results.Json(HoldingDto.From(h));
        });

        g.MapDelete("/holdings/{id:int}", async (int id, AppDbContext db, ICurrentUser me, HttpContext ctx) =>
        {
            var uid = me.UserId;
            var h = await OwnedHolding(db, id, uid).Include(x => x.Portfolio).FirstOrDefaultAsync();
            if (h is null) return NotFound(ctx);

            db.HoldingChanges.Add(Change(uid, h.Portfolio!, h.Symbol, 'D', h, null, DateTime.UtcNow));
            db.Holdings.Remove(h);
            await db.SaveChangesAsync();
            return Results.NoContent();
        });

        // API-10 標的搜尋(僅 IsActive = 1)
        g.MapGet("/instruments/search", async (string? q, int? limit, AppDbContext db, HttpContext ctx) =>
        {
            var term = (q ?? "").Trim();
            if (term.Length is < 1 or > 20) return Invalid(ctx, "q", "搜尋字串必須是 1 到 20 個字元");
            var take = Math.Clamp(limit ?? 10, 1, 20);
            var sym = term.ToUpperInvariant();

            var rows = await db.Instruments.AsNoTracking()
                .Where(i => i.IsActive && (i.Symbol.StartsWith(sym) || i.Name.Contains(term)))
                .OrderBy(i => i.Symbol == sym ? 0 : i.Symbol.StartsWith(sym) ? 1 : 2)
                .ThenBy(i => i.Symbol)
                .Take(take).ToListAsync();
            return Results.Json(rows.Select(i => new
            {
                symbol = i.Symbol, name = i.Name,
                market = i.Market == Market.Twse ? "TWSE" : "TPEx",
                kind = i.Kind == InstrumentKind.Etf ? "etf" : "stock",
            }));
        });
    }

    private static IQueryable<Holding> OwnedHolding(AppDbContext db, int id, Guid uid) =>
        db.Holdings.Where(h => h.HoldingId == id && h.Portfolio!.UserId == uid);

    private static HoldingChange Change(Guid uid, Portfolio p, string symbol, char action, Holding? old, Holding? now, DateTime at) => new()
    {
        UserId = uid, PortfolioId = p.PortfolioId, PortfolioName = p.Name, Symbol = symbol, Action = action,
        OldTotalCost = old?.TotalCost, OldShares = old?.Shares,
        NewTotalCost = now?.TotalCost, NewShares = now?.Shares, ChangedAtUtc = at,
    };
}

public sealed record HoldingListDto(int HoldingId, string Symbol, string Name, long TotalCost, long Shares);

public sealed record PortfolioDto(int Id, string Name, int SortOrder, int HoldingCount);

public sealed record HoldingDto(int HoldingId, int PortfolioId, string Symbol, long TotalCost, long Shares)
{
    public static HoldingDto From(Holding h) => new(h.HoldingId, h.PortfolioId, h.Symbol, h.TotalCost, h.Shares);
}
