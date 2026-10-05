namespace StockInventory.Core;

/// <summary>§10.1 驗證規則(純函式)。回傳 null 表示通過,否則為繁體中文錯誤訊息。</summary>
public static class Validation
{
    public const long MaxTotalCost = 999_999_999_999;
    public const long MaxShares = 1_000_000_000;

    public static string NormalizePortfolioName(string? raw) => (raw ?? "").Trim();
    public static string? PortfolioName(string normalized) =>
        normalized.Length is >= 1 and <= 30 ? null : "庫存名稱必須是 1 到 30 個字元";

    public static string NormalizeSymbol(string? raw) => (raw ?? "").Trim().ToUpperInvariant();
    public static string? Symbol(string normalized) =>
        normalized.Length is >= 1 and <= 10 ? null : "請輸入標的代碼";

    public static string? TotalCost(decimal? v) =>
        v is { } x && x == decimal.Truncate(x) && x >= 0 && x <= MaxTotalCost ? null : "總成本必須是 0 到 999,999,999,999 的整數";

    public static string? Shares(decimal? v) =>
        v is { } x && x == decimal.Truncate(x) && x >= 1 && x <= MaxShares ? null : "股數必須是 1 到 1,000,000,000 的整數";

    public static string? FeeRate(decimal v) => v is >= 0 and <= 0.01m ? null : "手續費率必須介於 0 到 0.01";
    public static string? FeeDiscount(decimal v) => v is >= 0.001m and <= 1m ? null : "手續費折扣必須介於 0.001 到 1";
    public static string? ColorScheme(int v) => v is 0 or 1 ? null : "顏色慣例必須是 0 或 1";
}
