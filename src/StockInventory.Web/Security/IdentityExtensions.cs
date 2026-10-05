using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Identity;
using StockInventory.Data;
using StockInventory.Web.Options;

namespace StockInventory.Web.Security;

public static class IdentityExtensions
{
    public static IServiceCollection AddAppIdentity(this IServiceCollection services, IConfiguration cfg)
    {
        var auth = cfg.GetSection("Auth").Get<AuthOptions>() ?? new AuthOptions();

        services.AddIdentity<AppUser, IdentityRole<Guid>>(o =>
            {
                // SEC-01:長度 ≥ 12,不強制字元組合
                o.Password.RequiredLength = auth.MinPasswordLength;
                o.Password.RequireDigit = false;
                o.Password.RequireLowercase = false;
                o.Password.RequireUppercase = false;
                o.Password.RequireNonAlphanumeric = false;
                // SEC-04
                o.Lockout.AllowedForNewUsers = true;
                o.Lockout.MaxFailedAccessAttempts = auth.LockoutMaxFailures;
                o.Lockout.DefaultLockoutTimeSpan = TimeSpan.FromMinutes(auth.LockoutMinutes);
                o.User.RequireUniqueEmail = true;
            })
            .AddEntityFrameworkStores<AppDbContext>()
            .AddSignInManager<ActiveOnlySignInManager>()
            .AddDefaultTokenProviders();

        // SEC-05:HttpOnly、Secure、SameSite=Lax、閒置逾時(滑動過期)
        services.ConfigureApplicationCookie(o =>
        {
            o.Cookie.Name = "si.auth";
            o.Cookie.HttpOnly = true;
            o.Cookie.SecurePolicy = CookieSecurePolicy.Always;
            o.Cookie.SameSite = SameSiteMode.Lax;
            o.ExpireTimeSpan = TimeSpan.FromMinutes(auth.IdleMinutes);
            o.SlidingExpiration = true;
            o.LoginPath = "/Account/Login";
            o.AccessDeniedPath = "/Account/Login";
            // API 未登入回 401,不重新導向(§8 通則)
            o.Events.OnRedirectToLogin = ctx => Reject(ctx, 401, "UNAUTHENTICATED", "請先登入");
            o.Events.OnRedirectToAccessDenied = ctx => Reject(ctx, 403, "FORBIDDEN", "沒有權限");
        });

        // SEC-06:記住裝置 Cookie 期限
        services.Configure<CookieAuthenticationOptions>(IdentityConstants.TwoFactorRememberMeScheme, o =>
        {
            o.ExpireTimeSpan = TimeSpan.FromDays(auth.TrustDeviceDays);
            o.Cookie.HttpOnly = true;
            o.Cookie.SecurePolicy = CookieSecurePolicy.Always;
            o.Cookie.SameSite = SameSiteMode.Lax;
        });

        // 停用/重設密碼會更新 SecurityStamp,縮短驗證間隔讓既有登入盡快失效
        services.Configure<SecurityStampValidatorOptions>(o => o.ValidationInterval = TimeSpan.FromMinutes(1));

        services.AddHttpContextAccessor();
        services.AddScoped<ICurrentUser, CurrentUser>();
        return services;
    }

    private static Task Reject(RedirectContext<CookieAuthenticationOptions> ctx, int status, string code, string title)
    {
        if (Problems.IsApi(ctx.Request)) return Problems.WriteAsync(ctx.HttpContext, status, code, title);
        ctx.Response.Redirect(ctx.RedirectUri);
        return Task.CompletedTask;
    }
}

/// <summary>IsActive = 0 的帳號一律拒絕登入(§5.2、SEC-07)。</summary>
public sealed class ActiveOnlySignInManager(
    UserManager<AppUser> um, IHttpContextAccessor ctx, IUserClaimsPrincipalFactory<AppUser> pf,
    Microsoft.Extensions.Options.IOptions<IdentityOptions> opt, ILogger<SignInManager<AppUser>> log,
    Microsoft.AspNetCore.Authentication.IAuthenticationSchemeProvider schemes, IUserConfirmation<AppUser> conf)
    : SignInManager<AppUser>(um, ctx, pf, opt, log, schemes, conf)
{
    public override async Task<bool> CanSignInAsync(AppUser user) =>
        user.IsActive && await base.CanSignInAsync(user);
}
