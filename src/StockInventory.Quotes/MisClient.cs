using Microsoft.Extensions.Options;

namespace StockInventory.Quotes;

public sealed class MisClient(HttpClient http, IOptions<QuoteOptions> options, TimeProvider clock) : IMisClient
{
    public const string BaseUrl = "https://mis.twse.com.tw/stock/api/getStockInfo.jsp";

    public async Task<string> GetAsync(string exCh, CancellationToken ct)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(options.Value.TimeoutSeconds)); // 逾時視為失敗
        var url = $"{BaseUrl}?ex_ch={Uri.EscapeDataString(exCh)}&json=1&delay=0&_={clock.GetUtcNow().ToUnixTimeMilliseconds()}";
        using var res = await http.GetAsync(url, cts.Token);
        res.EnsureSuccessStatusCode();
        return await res.Content.ReadAsStringAsync(cts.Token);
    }
}
