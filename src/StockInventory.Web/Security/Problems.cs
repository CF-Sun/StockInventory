using System.Text.Json;

namespace StockInventory.Web.Security;

/// <summary>RFC 7807 problem+json,含 code 欄位(§10.2)。</summary>
public static class Problems
{
    public static async Task WriteAsync(HttpContext ctx, int status, string code, string title,
        IDictionary<string, string[]>? errors = null)
    {
        ctx.Response.StatusCode = status;
        ctx.Response.ContentType = "application/problem+json";
        var body = new Dictionary<string, object?>
        {
            ["type"] = $"https://stockinventory.newsafety.hk/problems/{code.ToLowerInvariant().Replace('_', '-')}",
            ["title"] = title,
            ["status"] = status,
            ["code"] = code,
        };
        if (errors is not null) body["errors"] = errors;
        await ctx.Response.WriteAsync(JsonSerializer.Serialize(body));
    }

    public static bool IsApi(HttpRequest r) => r.Path.StartsWithSegments("/api");
}
