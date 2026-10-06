using Microsoft.AspNetCore.Diagnostics;

namespace StockInventory.Web.Security;

/// <summary>SEC-14:正式環境不顯示例外細節;/api 回 §10.2 的 500 INTERNAL_ERROR,頁面導向通用錯誤頁。細節只寫日誌。</summary>
public static class ErrorHandling
{
    public static async Task HandleAsync(HttpContext ctx)
    {
        var ex = ctx.Features.Get<IExceptionHandlerFeature>()?.Error;
        ctx.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger("Unhandled")
            .LogError(ex, "未預期錯誤 {Method} {Path}", ctx.Request.Method, ctx.Request.Path.Value);

        if (Problems.IsApi(ctx.Request) || ctx.Request.Path.StartsWithSegments("/hubs"))
            await Problems.WriteAsync(ctx, 500, "INTERNAL_ERROR", "發生未預期的錯誤,請稍後再試");
        else
            ctx.Response.Redirect("/Error");
    }
}
