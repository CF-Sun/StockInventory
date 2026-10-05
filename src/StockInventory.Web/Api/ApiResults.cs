namespace StockInventory.Web.Api;

/// <summary>§10.2 的 problem+json 回應。</summary>
public static class ApiResults
{
    public static IResult Problem(HttpContext ctx, int status, string code, string title) =>
        Results.Problem(statusCode: status, title: title,
            type: $"https://stockinventory.newsafety.hk/problems/{code.ToLowerInvariant().Replace('_', '-')}",
            extensions: new Dictionary<string, object?> { ["code"] = code });

    public static IResult Invalid(HttpContext ctx, IDictionary<string, string[]> errors) =>
        Results.Problem(statusCode: 400, title: "輸入資料有誤", type: "https://stockinventory.newsafety.hk/problems/validation",
            extensions: new Dictionary<string, object?> { ["code"] = "VALIDATION_FAILED", ["errors"] = errors });

    public static IResult Invalid(HttpContext ctx, string field, string message) =>
        Invalid(ctx, new Dictionary<string, string[]> { [field] = [message] });

    public static IResult NotFound(HttpContext ctx) => Problem(ctx, 404, "NOT_FOUND", "找不到資料");
}
