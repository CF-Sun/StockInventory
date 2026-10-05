using System.Globalization;
using System.Text;
using Microsoft.Extensions.Options;
using StockInventory.Core;
using StockInventory.Web.Options;
using StockInventory.Web.Security;
using StockInventory.Web.Services;

namespace StockInventory.Web.Api;

public static class ExportApi
{
    public static void MapExportApi(this WebApplication app)
    {
        // API-12:參數同 API-06;UTF-8 含 BOM
        app.MapGet("/api/export/holdings.csv", async (string? portfolioIds, bool? merge, ViewService views, ICurrentUser me,
            IOptions<MarketOptions> market, TimeProvider clock, CancellationToken ct) =>
        {
            var doMerge = merge ?? true;
            var ctx = await views.BuildContextAsync(me.UserId, ViewService.ParseIds(portfolioIds), doMerge, ct);
            var csv = CsvWriter.Build(ctx, doMerge);

            var bytes = new UTF8Encoding(true).GetPreamble().Concat(Encoding.UTF8.GetBytes(csv)).ToArray();
            var local = TimeZoneInfo.ConvertTimeFromUtc(clock.GetUtcNow().UtcDateTime, ResolveZone(market.Value.TimeZoneId));
            return Results.File(bytes, "text/csv; charset=utf-8", $"holdings-{local:yyyyMMdd-HHmm}.csv");
        }).RequireAuthorization();
    }

    private static TimeZoneInfo ResolveZone(string id)
    {
        try { return TimeZoneInfo.FindSystemTimeZoneById(id); }
        catch (TimeZoneNotFoundException) { return TimeZoneInfo.FindSystemTimeZoneById("Asia/Taipei"); } // 非 Windows 主機
    }
}

public static class CsvWriter
{
    public static string Build(ViewService.ViewContext c, bool merge)
    {
        var sb = new StringBuilder();
        var head = new List<string>();
        if (!merge) head.Add("庫存");
        head.AddRange(["代號", "名稱", "市場", "股數", "總成本", "均價", "現價", "漲跌", "漲跌幅(%)", "市值", "未實現損益", "報酬率(%)", "今日損益", "報價狀態"]);
        if (c.DeductFees) head.AddRange(["預估手續費", "預估證交稅"]);
        sb.Append(string.Join(',', head)).Append("\r\n");

        foreach (var r in c.Result.Rows)
        {
            var f = new List<string>();
            if (!merge)
                f.Add(Text(r.PortfolioId is { } pid && c.PortfolioNames.TryGetValue(pid, out var pn) ? pn : ""));
            f.Add(Text(r.Symbol));
            f.Add(Text(r.Name));
            f.Add(Text(r.Market == Market.Twse ? "TWSE" : "TPEx"));
            f.Add(Num(r.Shares));
            f.Add(Num(r.TotalCost));
            f.Add(Fixed2(r.AvgCost));
            f.Add(Num(r.LastPrice));
            f.Add(Num(r.Change));
            f.Add(Fixed2(r.ChangePct));
            f.Add(Num(r.MarketValue));
            f.Add(Num(r.UnrealizedPnl));
            f.Add(Fixed2(r.ReturnRatePct));
            f.Add(Num(r.TodayPnl));
            f.Add(Text(r.QuoteStatus.ToString().ToLowerInvariant()));
            if (c.DeductFees) { f.Add(Num(r.EstFee)); f.Add(Num(r.EstTax)); }
            sb.Append(string.Join(',', f)).Append("\r\n");
        }
        return sb.ToString();
    }

    /// <summary>均價與百分比固定 2 位小數(§12.5)。</summary>
    private static string Fixed2(decimal? v) => v is null ? "" : v.Value.ToString("0.00", CultureInfo.InvariantCulture);

    /// <summary>數值為顯示值;null 為空白。</summary>
    private static string Num(IFormattable? v) => v is null ? "" : v.ToString(null, CultureInfo.InvariantCulture);

    /// <summary>字串欄位以 = + - @ 開頭時前面補 ',防止試算表公式注入;再依 CSV 規則加引號。</summary>
    internal static string Text(string s)
    {
        if (s.Length > 0 && s[0] is '=' or '+' or '-' or '@' or '\t' or '\r') s = "'" + s;
        return s.IndexOfAny([',', '"', '\r', '\n']) >= 0 ? "\"" + s.Replace("\"", "\"\"") + "\"" : s;
    }
}
