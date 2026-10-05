using System.Net;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.EntityFrameworkCore;
using StockInventory.Data;

namespace StockInventory.Web.Security;

public static class SecurityMiddleware
{
    /// <summary>SEC-11、SEC-10:安全標頭與 CSP。</summary>
    public static IApplicationBuilder UseSecurityHeaders(this IApplicationBuilder app) =>
        app.Use(async (ctx, next) =>
        {
            var h = ctx.Response.Headers;
            h["X-Content-Type-Options"] = "nosniff";
            h["Referrer-Policy"] = "strict-origin-when-cross-origin";
            h["Content-Security-Policy"] = "default-src 'self'; frame-ancestors 'none'";
            await next();
        });

    /// <summary>SEC-12:只信任 Cloudflare 的 IP 範圍(Cloudflare:ProxyCidrs),來源 IP 取自 CF-Connecting-IP。</summary>
    public static IServiceCollection AddCloudflareForwarding(this IServiceCollection services, IConfiguration cfg)
    {
        var cidrs = cfg.GetSection("Cloudflare:ProxyCidrs").Get<string[]>() ?? [];
        services.Configure<ForwardedHeadersOptions>(o =>
        {
            o.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
            o.ForwardedForHeaderName = "CF-Connecting-IP";
            o.KnownProxies.Clear();
            o.KnownNetworks.Clear(); // 未設定範圍時,不信任任何轉送標頭
            foreach (var c in cidrs)
            {
                var parts = c.Split('/');
                o.KnownNetworks.Add(new Microsoft.AspNetCore.HttpOverrides.IPNetwork(
                    IPAddress.Parse(parts[0]), int.Parse(parts[1])));
            }
        });
        return services;
    }

    /// <summary>SEC-09:/api 的 POST、PUT、DELETE 必須帶有效的 X-CSRF-TOKEN,否則 400。</summary>
    public static IApplicationBuilder UseApiCsrf(this IApplicationBuilder app) =>
        app.Use(async (ctx, next) =>
        {
            if (Problems.IsApi(ctx.Request) && (HttpMethods.IsPost(ctx.Request.Method)
                || HttpMethods.IsPut(ctx.Request.Method) || HttpMethods.IsDelete(ctx.Request.Method)))
            {
                var af = ctx.RequestServices.GetRequiredService<IAntiforgery>();
                if (!await af.IsRequestValidAsync(ctx))
                {
                    await Problems.WriteAsync(ctx, 400, "VALIDATION_FAILED", "防偽權杖缺失或無效");
                    return;
                }
            }
            await next();
        });

    /// <summary>SEC-02:必須先改密碼、再啟用 TOTP,完成前只允許進入設定頁、登出與靜態檔。</summary>
    public static IApplicationBuilder UseOnboardingGate(this IApplicationBuilder app) =>
        app.Use(async (ctx, next) =>
        {
            if (ctx.User.Identity?.IsAuthenticated != true || IsExempt(ctx.Request.Path)) { await next(); return; }

            var id = Guid.Parse(ctx.User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)!.Value);
            var db = ctx.RequestServices.GetRequiredService<AppDbContext>();
            var u = await db.Users.AsNoTracking().Where(x => x.Id == id)
                .Select(x => new { x.MustChangePassword, x.TwoFactorEnabled }).FirstOrDefaultAsync();
            string? target = u is null ? null
                : u.MustChangePassword ? "/Account/Manage/Password"
                : !u.TwoFactorEnabled ? "/Account/Manage/2fa" : null;
            if (target is null) { await next(); return; }

            if (Problems.IsApi(ctx.Request))
                await Problems.WriteAsync(ctx, 403, "FORBIDDEN", "請先完成改密碼與雙重驗證設定");
            else
                ctx.Response.Redirect(target);
        });

    private static bool IsExempt(PathString p) =>
        p.StartsWithSegments("/Account/Manage/Password") || p.StartsWithSegments("/Account/Manage/2fa")
        || p.StartsWithSegments("/Account/Logout") || p.StartsWithSegments("/health")
        || p.StartsWithSegments("/css") || p.StartsWithSegments("/js") || p.StartsWithSegments("/lib");
}
