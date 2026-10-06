using System.Globalization;
using System.Text.Json;
using StockInventory.Core;

namespace StockInventory.Quotes;

public sealed class QuoteParseException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>解析後的單檔報價。LastPrice 為 null 表示沒有可用價格,呼叫端不得覆蓋舊值(§7.2 順序 4)。</summary>
public sealed record ParsedQuote(string Symbol, decimal? LastPrice, decimal? PrevClose, PriceSource? Source, DateTime? QuoteTimeUtc);

public static class MisParser
{
    /// <summary>§7.1 解析 MIS 回應。非 JSON、缺 msgArray 一律視為失敗(丟出 QuoteParseException)。</summary>
    public static IReadOnlyList<ParsedQuote> Parse(string body)
    {
        JsonDocument doc;
        try { doc = JsonDocument.Parse(body); }
        catch (JsonException ex) { throw new QuoteParseException("回應不是有效的 JSON", ex); }

        using (doc)
        {
            if (doc.RootElement.ValueKind != JsonValueKind.Object
                || !doc.RootElement.TryGetProperty("msgArray", out var arr) || arr.ValueKind != JsonValueKind.Array)
                throw new QuoteParseException("回應缺少 msgArray");

            var list = new List<ParsedQuote>();
            foreach (var e in arr.EnumerateArray())
            {
                if (e.ValueKind != JsonValueKind.Object) continue;
                var symbol = Str(e, "c");
                if (string.IsNullOrWhiteSpace(symbol)) continue;
                list.Add(ParseOne(symbol.Trim(), e));
            }
            return list;
        }
    }

    private static ParsedQuote ParseOne(string symbol, JsonElement e)
    {
        // z 在沒有當下成交時常為 "-"(實測:盤中 0050、2330 的 z 都是 "-"),此時改用回應中 trade 物件的最近成交價 trade.z(實測存在)
        var z = Num(Str(e, "z")) ?? (e.TryGetProperty("trade", out var tr) && tr.ValueKind == JsonValueKind.Object ? Num(Str(tr, "z")) : null);
        var ask = Num(FirstLevel(Str(e, "a")));
        var bid = Num(FirstLevel(Str(e, "b")));
        var y = Num(Str(e, "y"));

        DateTime? time = long.TryParse(Str(e, "tlong"), NumberStyles.None, CultureInfo.InvariantCulture, out var ms) && ms > 0
            ? DateTimeOffset.FromUnixTimeMilliseconds(ms).UtcDateTime : null;

        decimal? last; PriceSource? source;
        if (z is not null) { last = z; source = PriceSource.Trade; }
        else if (ask is not null && bid is not null) { last = (ask.Value + bid.Value) / 2m; source = PriceSource.MidQuote; }
        else if (y is not null) { last = y; source = PriceSource.PrevClose; }
        else { last = null; source = null; }

        return new ParsedQuote(symbol, last, y, source, time);
    }

    private static string? Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) ? v.ValueKind switch
        {
            JsonValueKind.String => v.GetString(),
            JsonValueKind.Number => v.GetRawText(),
            _ => null,
        } : null;

    /// <summary>五檔以 _ 分隔,取第 1 檔(可能有結尾的 _)。</summary>
    private static string? FirstLevel(string? levels) => levels?.Split('_', StringSplitOptions.TrimEntries).FirstOrDefault();

    /// <summary>"-"、空字串、非數字、≤ 0 一律視為沒有這個值。</summary>
    private static decimal? Num(string? s) =>
        decimal.TryParse(s, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var v) && v > 0 ? v : null;
}
