using System.Globalization;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using StockInventory.Core;

namespace StockInventory.Quotes;

public sealed record InstrumentInfo(string Symbol, string Name, Market Market, InstrumentKind Kind);

/// <summary>
/// 證交所 ISIN 公告頁(C_public.jsp)解析。2026-10-06 實測:欄位為「代號及名稱(以全形空白分隔)| ISIN | 上市日 | 市場別 | 產業別 | CFICode | 備註」,
/// 並以只有一個儲存格的列標示區段(股票、ETF、認購(售)權證…)。
/// 分辨方式(依 CFICode):ESVUFR = 股票(含創新板);EP 開頭 = 特別股(視為股票,證交稅率相同、MIS 報價方式相同);CE 開頭 = ETF。
/// ETN、DR、受益證券(REIT、資產基礎證券)、權證不納入:稅率或交易方式與 SPEC 的股票/ETF 假設不同。
/// </summary>
public static class IsinParser
{
    private static readonly Regex Cells = new(@"<td[^>]*>(.*?)</td>", RegexOptions.Singleline | RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex RowStart = new(@"<tr\s*>", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex Tags = new(@"<[^>]+>", RegexOptions.Compiled);
    private static readonly Regex IsinCode = new(@"^[A-Z]{2}[A-Z0-9]{9}[0-9]$", RegexOptions.Compiled);

    public static IReadOnlyList<InstrumentInfo> Parse(string html, Market market)
    {
        var result = new List<InstrumentInfo>();
        // 整頁可能只有一行,所以以 <tr> 切列,不能逐行處理
        foreach (var row in RowStart.Split(html))
        {
            var cells = Cells.Matches(row).Select(m => System.Net.WebUtility.HtmlDecode(Tags.Replace(m.Groups[1].Value, "")).Trim()).ToArray();
            if (cells.Length < 6 || !IsinCode.IsMatch(cells[1])) continue;

            var cfi = cells[5];
            InstrumentKind kind;
            if (cfi == "ESVUFR" || cfi.StartsWith("EP", StringComparison.Ordinal)) kind = InstrumentKind.Stock;
            else if (cfi.StartsWith("CE", StringComparison.Ordinal)) kind = InstrumentKind.Etf;
            else continue;

            var split = cells[0].IndexOf('　'); // 全形空白
            if (split <= 0) split = cells[0].IndexOf(' ');
            if (split <= 0) continue;
            var symbol = cells[0][..split].Trim();
            var name = cells[0][(split + 1)..].Trim();
            if (symbol.Length is 0 or > 10 || name.Length == 0) continue;
            if (name.Length > 50) name = name[..50];
            result.Add(new InstrumentInfo(symbol, name, market, kind));
        }
        return result;
    }
}

/// <summary>
/// 證交所休市日(https://www.twse.com.tw/rwd/zh/holidaySchedule/holidaySchedule?date=YYYY&amp;response=json)解析。
/// 2026-10-06 實測:回應包含(1)週六日的假日,(2)「開始交易日」「最後交易日」這類其實有交易的公告,
/// 所以依 SPEC「週六日不存入」排除週末,並排除名稱含「開始交易」「最後交易」者。
/// </summary>
public static class HolidayParser
{
    public static IReadOnlyList<(DateOnly Date, string Description)> Parse(string json)
    {
        JsonDocument doc;
        try { doc = JsonDocument.Parse(json); }
        catch (JsonException ex) { throw new QuoteParseException("休市日回應不是有效的 JSON", ex); }

        using (doc)
        {
            if (doc.RootElement.ValueKind != JsonValueKind.Object
                || !doc.RootElement.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array)
                throw new QuoteParseException("休市日回應缺少 data");

            var list = new List<(DateOnly, string)>();
            foreach (var row in data.EnumerateArray())
            {
                if (row.ValueKind != JsonValueKind.Array || row.GetArrayLength() < 2) continue;
                if (!DateOnly.TryParseExact(row[0].GetString(), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date)) continue;
                var name = (row[1].GetString() ?? "").Trim();
                if (date.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday) continue;
                if (name.Contains("開始交易") || name.Contains("最後交易")) continue;
                list.Add((date, name.Length > 50 ? name[..50] : name));
            }
            return list;
        }
    }
}

public interface IReferenceDataSource
{
    /// <summary>strMode:2 = 上市、4 = 上櫃。回傳已解碼(Big5)的 HTML。</summary>
    Task<string> GetIsinHtmlAsync(int mode, CancellationToken ct);
    Task<string> GetHolidayJsonAsync(int year, CancellationToken ct);
}

public sealed class HttpReferenceDataSource(HttpClient http) : IReferenceDataSource
{
    static HttpReferenceDataSource() => Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

    public async Task<string> GetIsinHtmlAsync(int mode, CancellationToken ct)
    {
        using var res = await http.GetAsync($"https://isin.twse.com.tw/isin/C_public.jsp?strMode={mode}", ct);
        res.EnsureSuccessStatusCode();
        var bytes = await res.Content.ReadAsByteArrayAsync(ct);
        return Encoding.GetEncoding(950).GetString(bytes); // 實測為 Big5(ms950)
    }

    public async Task<string> GetHolidayJsonAsync(int year, CancellationToken ct)
    {
        using var res = await http.GetAsync($"https://www.twse.com.tw/rwd/zh/holidaySchedule/holidaySchedule?date={year}&response=json", ct);
        res.EnsureSuccessStatusCode();
        return await res.Content.ReadAsStringAsync(ct);
    }
}
