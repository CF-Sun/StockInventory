using System.Security.Cryptography;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using StockInventory.Data;
using StockInventory.Web.Security;

using static StockInventory.Web.Api.ApiResults;

namespace StockInventory.Web.Api;

public sealed record CreateUserRequest(string? UserName, string? Email, string? DisplayName, string? InitialPassword);

public static class AdminApi
{
    public static void MapAdminApi(this WebApplication app)
    {
        // 前端取得防偽權杖,於 POST/PUT/DELETE 放入 X-CSRF-TOKEN(SEC-09)
        app.MapGet("/api/csrf", (HttpContext ctx, IAntiforgery af) =>
            Results.Json(new { token = af.GetAndStoreTokens(ctx).RequestToken })).RequireAuthorization();

        var g = app.MapGroup("/api/admin").RequireAuthorization(p => p.RequireRole(Roles.Admin));

        g.MapGet("/users", async (UserManager<AppUser> um) =>
        {
            var users = await um.Users.AsNoTracking().OrderBy(u => u.UserName).ToListAsync();
            var list = new List<object>();
            foreach (var u in users)
                list.Add(new
                {
                    id = u.Id, userName = u.UserName, email = u.Email, displayName = u.DisplayName,
                    isActive = u.IsActive, twoFactorEnabled = u.TwoFactorEnabled,
                    roles = await um.GetRolesAsync(u),
                });
            return Results.Json(list);
        });

        g.MapPost("/users", async (CreateUserRequest r, UserManager<AppUser> um, HttpContext ctx) =>
        {
            var errors = new Dictionary<string, string[]>();
            var userName = r.UserName?.Trim() ?? "";
            var email = r.Email?.Trim() ?? "";
            var display = r.DisplayName?.Trim() ?? "";
            if (userName.Length is < 1 or > 50) errors["userName"] = ["帳號必須是 1 到 50 個字元"];
            if (!System.Net.Mail.MailAddress.TryCreate(email, out _)) errors["email"] = ["Email 格式不正確"];
            if (display.Length > 50) errors["displayName"] = ["顯示名稱最多 50 個字元"];
            if (string.IsNullOrEmpty(r.InitialPassword)) errors["initialPassword"] = ["請輸入初始密碼"];
            if (errors.Count > 0) return Invalid(ctx, errors);

            if (await um.FindByNameAsync(userName) is not null)
                return Problem(ctx, 409, "DUPLICATE", "帳號名稱已存在");

            var user = new AppUser
            {
                UserName = userName, Email = email, EmailConfirmed = true, DisplayName = display,
                IsActive = true, MustChangePassword = true, CreatedAtUtc = DateTime.UtcNow,
            };
            var res = await um.CreateAsync(user, r.InitialPassword!);
            if (!res.Succeeded)
            {
                var codes = res.Errors.Select(e => e.Code).ToList();
                if (codes.Any(c => c.StartsWith("Duplicate")))
                    return Problem(ctx, 409, "DUPLICATE", "帳號名稱或 Email 已存在");
                var pwdErrors = res.Errors.Where(e => e.Code.StartsWith("Password"))
                    .Select(e => e.Code == "PasswordTooShort"
                        ? $"密碼至少需要 {um.Options.Password.RequiredLength} 個字元" : "密碼不符合規則").ToArray();
                return Invalid(ctx, new Dictionary<string, string[]>
                    { ["initialPassword"] = pwdErrors.Length > 0 ? pwdErrors : ["輸入資料有誤"] });
            }
            await um.AddToRoleAsync(user, Roles.User);
            return Results.Json(new { id = user.Id, userName = user.UserName }, statusCode: 201);
        });

        g.MapPost("/users/{id:guid}/deactivate", (Guid id, UserManager<AppUser> um, ICurrentUser me, HttpContext ctx) =>
            SetActive(id, false, um, me, ctx));
        g.MapPost("/users/{id:guid}/activate", (Guid id, UserManager<AppUser> um, ICurrentUser me, HttpContext ctx) =>
            SetActive(id, true, um, me, ctx));

        g.MapPost("/users/{id:guid}/reset-password", async (Guid id, UserManager<AppUser> um, HttpContext ctx) =>
        {
            var user = await um.FindByIdAsync(id.ToString());
            if (user is null) return Problem(ctx, 404, "NOT_FOUND", "找不到使用者");

            var temp = RandomPassword(16);
            var token = await um.GeneratePasswordResetTokenAsync(user);
            var res = await um.ResetPasswordAsync(user, token, temp); // 同時更新 SecurityStamp
            if (!res.Succeeded) return Problem(ctx, 500, "INTERNAL_ERROR", "重設密碼失敗");
            user.MustChangePassword = true;
            await um.UpdateAsync(user);
            await um.ResetAccessFailedCountAsync(user);
            await um.SetLockoutEndDateAsync(user, null);
            return Results.Json(new { temporaryPassword = temp }); // 僅此一次回傳,不記日誌
        });

        g.MapPost("/users/{id:guid}/reset-2fa", async (Guid id, UserManager<AppUser> um, HttpContext ctx) =>
        {
            var user = await um.FindByIdAsync(id.ToString());
            if (user is null) return Problem(ctx, 404, "NOT_FOUND", "找不到使用者");

            await um.SetTwoFactorEnabledAsync(user, false);
            await um.ResetAuthenticatorKeyAsync(user);
            await um.RemoveAuthenticationTokenAsync(user, "[AspNetUserStore]", "RecoveryCodes"); // 清除還原碼
            await um.UpdateSecurityStampAsync(user); // 使記住裝置與既有登入失效
            return Results.NoContent();
        });
    }

    private static async Task<IResult> SetActive(Guid id, bool active, UserManager<AppUser> um, ICurrentUser me, HttpContext ctx)
    {
        var user = await um.FindByIdAsync(id.ToString());
        if (user is null) return Problem(ctx, 404, "NOT_FOUND", "找不到使用者");
        if (!active && user.Id == me.UserId)
            return Invalid(ctx, new Dictionary<string, string[]> { ["id"] = ["不可停用自己"] });

        user.IsActive = active;
        await um.UpdateAsync(user);
        if (!active) await um.UpdateSecurityStampAsync(user); // 使既有登入失效
        return Results.NoContent();
    }

    private static string RandomPassword(int length)
    {
        const string alphabet = "ABCDEFGHJKLMNPQRSTUVWXYZabcdefghijkmnopqrstuvwxyz23456789";
        return string.Create(length, 0, (span, _) =>
        {
            for (var i = 0; i < span.Length; i++) span[i] = alphabet[RandomNumberGenerator.GetInt32(alphabet.Length)];
        });
    }
}
