using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Options;
using StockInventory.Data;
using StockInventory.Web.Options;

namespace StockInventory.Web.Security;

public static class Roles { public const string Admin = "Admin", User = "User"; }

/// <summary>啟動時確保角色存在;資料庫沒有任何使用者時依 Seed:* 建立第一位 Admin(§4、T1.2)。</summary>
public sealed class AdminSeeder(IServiceScopeFactory scopes, IOptions<SeedOptions> seed, ILogger<AdminSeeder> log)
    : IHostedService
{
    public async Task StartAsync(CancellationToken ct)
    {
        try { await SeedAsync(); }
        catch (Exception ex) { log.LogError("種子資料建立失敗 ({Type})", ex.GetType().Name); } // 不讓網站啟動失敗,也不記錄密碼
    }

    public async Task SeedAsync()
    {
        using var scope = scopes.CreateScope();
        var roles = scope.ServiceProvider.GetRequiredService<RoleManager<IdentityRole<Guid>>>();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<AppUser>>();

        foreach (var r in new[] { Roles.Admin, Roles.User })
            if (!await roles.RoleExistsAsync(r))
                await roles.CreateAsync(new IdentityRole<Guid>(r));

        if (users.Users.Any()) return;
        var s = seed.Value;
        if (string.IsNullOrWhiteSpace(s.AdminUserName) || string.IsNullOrWhiteSpace(s.AdminEmail)
            || string.IsNullOrEmpty(s.AdminInitialPassword))
        {
            log.LogWarning("資料庫沒有使用者,且未設定 Seed:Admin* 環境變數,未建立管理員");
            return;
        }

        var admin = new AppUser
        {
            UserName = s.AdminUserName, Email = s.AdminEmail, EmailConfirmed = true,
            DisplayName = s.AdminUserName, IsActive = true, MustChangePassword = true,
            CreatedAtUtc = DateTime.UtcNow,
        };
        var res = await users.CreateAsync(admin, s.AdminInitialPassword);
        if (!res.Succeeded)
        {
            log.LogError("建立管理員失敗:{Codes}", string.Join(",", res.Errors.Select(e => e.Code)));
            return;
        }
        await users.AddToRoleAsync(admin, Roles.Admin);
        log.LogInformation("已建立初始管理員 {UserName}", admin.UserName);
    }

    public Task StopAsync(CancellationToken ct) => Task.CompletedTask;
}
