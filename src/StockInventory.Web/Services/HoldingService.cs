using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using StockInventory.Core;
using StockInventory.Data;
using StockInventory.Quotes;
using StockInventory.Web.Options;

namespace StockInventory.Web.Services;

/// <summary>代號已存在於同庫存時的處理方式。Reject 為 API-07(回 409),Skip、Update 為 API-14。</summary>
public enum ExistsMode { Reject, Skip, Update }

public sealed record HoldingWriteInput(string? Symbol, decimal? Shares, decimal? TotalCost, ExistsMode OnExists);

public enum HoldingApplyStatus { Ok, Invalid, Duplicate, LimitExceeded }

public sealed record SkippedHolding(string Symbol, string Reason);

public sealed class HoldingApplyResult
{
    public HoldingApplyStatus Status { get; init; }
    public IDictionary<string, string[]> Errors { get; init; } = new Dictionary<string, string[]>();
    public List<Holding> Created { get; init; } = [];
    public List<Holding> Updated { get; init; } = [];
    public List<SkippedHolding> Skipped { get; init; } = [];
    public int MaxHoldings { get; init; }
}

/// <summary>
/// 新增或更新持股的共用領域服務(API-07、API-14):驗證、唯一性、上限、HoldingChanges 建立都在這裡。
/// 先在記憶體完成全部驗證與上限計算,再以一次 SaveChanges(單一交易)寫入,全成功或全不寫。
/// </summary>
public sealed class HoldingService(AppDbContext db, IOptions<LimitsOptions> limits, IFetchRequester fetch,
    ILogger<HoldingService> log)
{
    /// <param name="indexedKeys">true 時錯誤鍵為 items[i].field(API-14),否則為 field(API-07)。</param>
    public async Task<HoldingApplyResult> ApplyAsync(Guid userId, Portfolio portfolio,
        IReadOnlyList<HoldingWriteInput> items, bool indexedKeys, CancellationToken ct = default)
    {
        var max = limits.Value.MaxHoldingsPerPortfolio;
        HoldingApplyResult Fail(HoldingApplyStatus s, IDictionary<string, string[]>? errors = null) =>
            new() { Status = s, Errors = errors ?? new Dictionary<string, string[]>(), MaxHoldings = max };

        string Key(int i, string field) => indexedKeys ? $"items[{i}].{field}" : field;

        // 1. 欄位驗證
        var errors = new Dictionary<string, string[]>();
        var symbols = new string[items.Count];
        for (var i = 0; i < items.Count; i++)
        {
            var it = items[i];
            symbols[i] = Validation.NormalizeSymbol(it.Symbol);
            if (Validation.Symbol(symbols[i]) is { } e1) errors[Key(i, "symbol")] = [e1];
            if (Validation.TotalCost(it.TotalCost) is { } e2) errors[Key(i, "totalCost")] = [e2];
            if (Validation.Shares(it.Shares) is { } e3) errors[Key(i, "shares")] = [e3];
        }

        // 2. 請求內重複代號
        if (indexedKeys)
        {
            var seen = new HashSet<string>();
            for (var i = 0; i < items.Count; i++)
                if (symbols[i].Length > 0 && !seen.Add(symbols[i]) && !errors.ContainsKey(Key(i, "symbol")))
                    errors[Key(i, "symbol")] = ["請求內代號重複"];
        }

        // 3. 主檔(存在且 IsActive)
        var valid = symbols.Where(s => s.Length > 0).Distinct().ToList();
        var activeSet = valid.Count == 0 ? [] : (await db.Instruments.AsNoTracking()
            .Where(i => valid.Contains(i.Symbol) && i.IsActive).Select(i => i.Symbol).ToListAsync(ct)).ToHashSet();
        for (var i = 0; i < items.Count; i++)
            if (!errors.ContainsKey(Key(i, "symbol")) && !errors.ContainsKey(Key(i, "totalCost"))
                && !errors.ContainsKey(Key(i, "shares")) && !activeSet.Contains(symbols[i]))
                errors[Key(i, "symbol")] = ["找不到此標的"];
        if (errors.Count > 0) return Fail(HoldingApplyStatus.Invalid, errors);

        // 4. 同庫存現有持股
        var pid = portfolio.PortfolioId;
        var current = await db.Holdings.Where(h => h.PortfolioId == pid && valid.Contains(h.Symbol)).ToListAsync(ct);
        var bySymbol = current.ToDictionary(h => h.Symbol);
        if (items.Any((it, i) => it.OnExists == ExistsMode.Reject && bySymbol.ContainsKey(symbols[i])))
            return Fail(HoldingApplyStatus.Duplicate);

        // 5. 上限:現有持股數 + 實際新增筆數(不含 skip 與 update)
        var newCount = symbols.Count(s => !bySymbol.ContainsKey(s));
        if (newCount > 0 && await db.Holdings.CountAsync(h => h.PortfolioId == pid, ct) + newCount > max)
            return Fail(HoldingApplyStatus.LimitExceeded);

        // 6. 組出變更(尚未寫入)
        var now = DateTime.UtcNow;
        var result = new HoldingApplyResult { Status = HoldingApplyStatus.Ok, MaxHoldings = max };
        for (var i = 0; i < items.Count; i++)
        {
            var it = items[i];
            var symbol = symbols[i];
            var cost = (long)it.TotalCost!.Value;
            var shares = (long)it.Shares!.Value;
            if (bySymbol.TryGetValue(symbol, out var h))
            {
                if (it.OnExists != ExistsMode.Update) { result.Skipped.Add(new(symbol, "EXISTS")); continue; }
                if (h.TotalCost == cost && h.Shares == shares) { result.Skipped.Add(new(symbol, "UNCHANGED")); continue; }
                var old = new Holding { TotalCost = h.TotalCost, Shares = h.Shares };
                h.TotalCost = cost;
                h.Shares = shares;
                h.UpdatedAtUtc = now;
                db.HoldingChanges.Add(NewChange(userId, portfolio, symbol, 'U', old, h, now));
                result.Updated.Add(h);
            }
            else
            {
                var n = new Holding { PortfolioId = pid, Symbol = symbol, TotalCost = cost, Shares = shares,
                    CreatedAtUtc = now, UpdatedAtUtc = now };
                db.Holdings.Add(n);
                db.HoldingChanges.Add(NewChange(userId, portfolio, symbol, 'A', null, n, now));
                result.Created.Add(n);
            }
        }

        // 7. 單一 SaveChanges = 單一交易;併發唯一性衝突整批回復
        try { await db.SaveChangesAsync(ct); }
        catch (DbUpdateException ex)
        {
            log.LogWarning(ex, "批次寫入持股失敗(唯一性衝突或資料庫錯誤)");
            return Fail(HoldingApplyStatus.Duplicate);
        }

        foreach (var h in result.Created.Concat(result.Updated)) fetch.RequestImmediateFetch(h.Symbol);
        return result;
    }

    public static HoldingChange NewChange(Guid uid, Portfolio p, string symbol, char action, Holding? old, Holding? now, DateTime at) => new()
    {
        UserId = uid, PortfolioId = p.PortfolioId, PortfolioName = p.Name, Symbol = symbol, Action = action,
        OldTotalCost = old?.TotalCost, OldShares = old?.Shares,
        NewTotalCost = now?.TotalCost, NewShares = now?.Shares, ChangedAtUtc = at,
    };
}

internal static class EnumerableExtensions
{
    public static bool Any<T>(this IReadOnlyList<T> src, Func<T, int, bool> pred)
    {
        for (var i = 0; i < src.Count; i++) if (pred(src[i], i)) return true;
        return false;
    }
}
