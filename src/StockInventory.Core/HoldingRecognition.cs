using System.Text.RegularExpressions;

namespace StockInventory.Core;

/// <summary>視覺模型回傳的一列(已嚴格解析,數字為圖上原樣數值;單位一律為股,不換算)。</summary>
public sealed record RecognizedRow(string? Symbol, decimal? Shares, decimal? TotalCost);

/// <summary>同庫存已存在的持股。</summary>
public sealed record ExistingHolding(int HoldingId, long Shares, long TotalCost);

/// <summary>標的主檔中與辨識有關的欄位。</summary>
public sealed record InstrumentInfo(string Name, bool IsActive);

public static class RecognitionStatus
{
    public const string Ok = "OK";
    public const string Exists = "EXISTS";
    public const string NotFound = "NOT_FOUND";
    public const string Inactive = "INACTIVE";
    public const string Invalid = "INVALID";
    public const string DuplicateInImage = "DUPLICATE_IN_IMAGE";
}

public static class RecognitionWarning
{
    public const string CostZero = "COST_ZERO";
    public const string CostMissing = "COST_MISSING";
    public const string SharesMissing = "SHARES_MISSING";
    public const string OddLot = "ODD_LOT";
}

/// <summary>核對表的一列草稿(API-13 的 items[])。不寫資料庫。</summary>
public sealed record DraftItem(
    string ClientId, string? Symbol, string? Name, long? Shares, long? TotalCost,
    string Status, IReadOnlyList<string> Warnings, ExistingHolding? Existing);

/// <summary>FR-25 辨識結果正規化與狀態判定(純函式,不做 I/O)。</summary>
public static class RecognitionNormalizer
{
    public const int MaxRows = 200;
    public const long OddLotThreshold = 1000;

    // 只接受英數代號(含特別股字尾);其餘(含提示注入文字)一律視為 null,不回顯
    private static readonly Regex SymbolPattern = new("^[A-Z0-9]{1,10}$", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public static string? SanitizeSymbol(string? raw)
    {
        var s = Validation.NormalizeSymbol(raw);
        return SymbolPattern.IsMatch(s) ? s : null;
    }

    /// <param name="instruments">key 為正規化代號。</param>
    /// <param name="existing">同庫存現有持股,key 為代號。</param>
    public static List<DraftItem> Build(IReadOnlyList<RecognizedRow> rows,
        IReadOnlyDictionary<string, InstrumentInfo> instruments,
        IReadOnlyDictionary<string, ExistingHolding> existing)
    {
        var taken = rows.Take(MaxRows).ToList();
        var symbols = taken.Select(r => SanitizeSymbol(r.Symbol)).ToList();
        var dupSymbols = symbols.Where(s => s is not null).GroupBy(s => s!).Where(g => g.Count() > 1)
            .Select(g => g.Key).ToHashSet();

        var result = new List<DraftItem>(taken.Count);
        for (var i = 0; i < taken.Count; i++)
        {
            var row = taken[i];
            var symbol = symbols[i];
            var warnings = new List<string>();

            long? shares = Validation.Shares(row.Shares) is null ? (long)row.Shares!.Value : null;
            long? cost = Validation.TotalCost(row.TotalCost) is null ? (long)row.TotalCost!.Value : null;
            if (row.Shares is null) warnings.Add(RecognitionWarning.SharesMissing);
            if (row.TotalCost is null) warnings.Add(RecognitionWarning.CostMissing);
            if (cost == 0) warnings.Add(RecognitionWarning.CostZero);
            if (shares is < OddLotThreshold) warnings.Add(RecognitionWarning.OddLot);

            InstrumentInfo? info = null;
            if (symbol is not null) instruments.TryGetValue(symbol, out info);
            ExistingHolding? exist = null;
            if (symbol is not null) existing.TryGetValue(symbol, out exist);

            string status;
            if (symbol is null || info is null) status = RecognitionStatus.NotFound;
            else if (!info.IsActive) status = RecognitionStatus.Inactive;
            else if (dupSymbols.Contains(symbol)) status = RecognitionStatus.DuplicateInImage;
            else if (shares is null || cost is null) status = RecognitionStatus.Invalid;
            else if (exist is not null) status = RecognitionStatus.Exists;
            else status = RecognitionStatus.Ok;

            result.Add(new DraftItem($"r{i + 1}", symbol, info?.Name, shares, cost, status, warnings,
                status == RecognitionStatus.Exists ? exist : null));
        }
        return result;
    }
}
