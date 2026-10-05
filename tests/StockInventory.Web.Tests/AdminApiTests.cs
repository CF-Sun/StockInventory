using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;
using StockInventory.Data;
using Xunit;
using L = StockInventory.Web.Tests.LoginFlowTests;

namespace StockInventory.Web.Tests;

public class AdminApiTests
{
    const string Pw = "Plain-Password-123";

    /// <summary>直接在資料庫建立「已改密碼且已啟用 TOTP」的使用者,再走真實登入流程。</summary>
    internal static async Task<(HttpClient Client, string Csrf, Guid Id)> SignInAsync(TestFactory f, string name, params string[] roles) => await SignInAsync(f, name, null, roles);

    internal static async Task<(HttpClient Client, string Csrf, Guid Id)> SignInAsync(TestFactory f, string name, HttpClient? client, params string[] roles)
    {
        string key;
        Guid id;
        using (var scope = f.Services.CreateScope())
        {
            var um = scope.ServiceProvider.GetRequiredService<UserManager<AppUser>>();
            var u = new AppUser { UserName = name, Email = $"{name}@example.com", EmailConfirmed = true,
                MustChangePassword = false, CreatedAtUtc = DateTime.UtcNow };
            Assert.True((await um.CreateAsync(u, Pw)).Succeeded);
            foreach (var r in roles) await um.AddToRoleAsync(u, r);
            await um.ResetAuthenticatorKeyAsync(u);
            await um.SetTwoFactorEnabledAsync(u, true);
            key = (await um.GetAuthenticatorKeyAsync(u))!;
            id = u.Id;
        }
        var c = client ?? L.NewClient(f);
        await L.Login(c, name, Pw);
        await L.Post(c, "/Account/LoginWith2fa", new() { ["Code"] = L.Totp(key) });
        var csrf = JsonDocument.Parse(await c.GetStringAsync("/api/csrf")).RootElement.GetProperty("token").GetString()!;
        return (c, csrf, id);
    }

    internal static HttpRequestMessage Req(HttpMethod m, string url, string csrf, object? body = null)
    {
        var r = new HttpRequestMessage(m, url);
        r.Headers.Add("X-CSRF-TOKEN", csrf);
        if (body is not null) r.Content = JsonContent.Create(body);
        return r;
    }

    internal static async Task<JsonElement> Json(HttpResponseMessage r) => JsonDocument.Parse(await r.Content.ReadAsStringAsync()).RootElement;

    [Fact]
    public async Task NonAdmin_Gets403()
    {
        using var f = new TestFactory();
        var (c, _, _) = await SignInAsync(f, "alice", "User");
        Assert.Equal(HttpStatusCode.Forbidden, (await c.GetAsync("/api/admin/users")).StatusCode);
    }

    [Fact]
    public async Task Anonymous_Gets401()
    {
        using var f = new TestFactory();
        Assert.Equal(HttpStatusCode.Unauthorized, (await L.NewClient(f).GetAsync("/api/admin/users")).StatusCode);
    }

    [Fact]
    public async Task Create_List_Duplicate_WeakPassword()
    {
        using var f = new TestFactory();
        var (c, csrf, _) = await SignInAsync(f, "boss", "Admin");

        var weak = await c.SendAsync(Req(HttpMethod.Post, "/api/admin/users", csrf,
            new { userName = "bob", email = "bob@example.com", displayName = "Bob", initialPassword = "short" }));
        Assert.Equal(HttpStatusCode.BadRequest, weak.StatusCode);
        Assert.Equal("VALIDATION_FAILED", (await Json(weak)).GetProperty("code").GetString());

        var ok = await c.SendAsync(Req(HttpMethod.Post, "/api/admin/users", csrf,
            new { userName = "bob", email = "bob@example.com", displayName = "Bob", initialPassword = "Long-Enough-Pass-1" }));
        Assert.True(ok.StatusCode == HttpStatusCode.Created, await ok.Content.ReadAsStringAsync());

        var dup = await c.SendAsync(Req(HttpMethod.Post, "/api/admin/users", csrf,
            new { userName = "bob", email = "bob2@example.com", displayName = "Bob", initialPassword = "Long-Enough-Pass-1" }));
        Assert.Equal(HttpStatusCode.Conflict, dup.StatusCode);
        Assert.Equal("DUPLICATE", (await Json(dup)).GetProperty("code").GetString());

        var list = await Json(await c.GetAsync("/api/admin/users"));
        var bob = list.EnumerateArray().Single(x => x.GetProperty("userName").GetString() == "bob");
        Assert.True(bob.GetProperty("isActive").GetBoolean());
        Assert.False(bob.GetProperty("twoFactorEnabled").GetBoolean());
        Assert.Equal("User", bob.GetProperty("roles")[0].GetString());

        using var scope = f.Services.CreateScope();
        var u = await scope.ServiceProvider.GetRequiredService<UserManager<AppUser>>().FindByNameAsync("bob");
        Assert.True(u!.MustChangePassword);
    }

    [Fact]
    public async Task Post_WithoutCsrf_Returns400()
    {
        using var f = new TestFactory();
        var (c, _, _) = await SignInAsync(f, "boss", "Admin");
        var res = await c.PostAsJsonAsync("/api/admin/users", new { userName = "x" });
        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
    }

    [Fact]
    public async Task Deactivate_InvalidatesSession_BlocksLogin_CannotDeactivateSelf()
    {
        using var f = new TestFactory();
        var (admin, csrf, adminId) = await SignInAsync(f, "boss", "Admin");
        var (victim, _, victimId) = await SignInAsync(f, "carol", "User");
        Assert.Equal(HttpStatusCode.OK, (await victim.GetAsync("/api/me")).StatusCode);

        var self = await admin.SendAsync(Req(HttpMethod.Post, $"/api/admin/users/{adminId}/deactivate", csrf));
        Assert.Equal(HttpStatusCode.BadRequest, self.StatusCode);

        var res = await admin.SendAsync(Req(HttpMethod.Post, $"/api/admin/users/{victimId}/deactivate", csrf));
        Assert.Equal(HttpStatusCode.NoContent, res.StatusCode);

        using (var scope = f.Services.CreateScope())
        {
            var um = scope.ServiceProvider.GetRequiredService<UserManager<AppUser>>();
            var u = (await um.FindByIdAsync(victimId.ToString()))!;
            Assert.False(u.IsActive);
        }
        var relogin = await L.Login(L.NewClient(f), "carol", Pw);
        Assert.Contains("帳號或密碼錯誤", await L.Body(relogin));

        var act = await admin.SendAsync(Req(HttpMethod.Post, $"/api/admin/users/{victimId}/activate", csrf));
        Assert.Equal(HttpStatusCode.NoContent, act.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound,
            (await admin.SendAsync(Req(HttpMethod.Post, $"/api/admin/users/{Guid.NewGuid()}/activate", csrf))).StatusCode);
    }

    [Fact]
    public async Task ResetPassword_ReturnsTemp16_ForcesChange_OldPasswordFails()
    {
        using var f = new TestFactory();
        var (admin, csrf, _) = await SignInAsync(f, "boss", "Admin");
        var (_, _, id) = await SignInAsync(f, "dave", "User");

        var res = await admin.SendAsync(Req(HttpMethod.Post, $"/api/admin/users/{id}/reset-password", csrf));
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        var temp = (await Json(res)).GetProperty("temporaryPassword").GetString()!;
        Assert.Equal(16, temp.Length);

        using var scope = f.Services.CreateScope();
        var um = scope.ServiceProvider.GetRequiredService<UserManager<AppUser>>();
        var u = (await um.FindByIdAsync(id.ToString()))!;
        Assert.True(u.MustChangePassword);
        Assert.False(await um.CheckPasswordAsync(u, Pw));
        Assert.True(await um.CheckPasswordAsync(u, temp));
    }

    [Fact]
    public async Task Reset2fa_DisablesTotp_ClearsRecoveryCodes()
    {
        using var f = new TestFactory();
        var (admin, csrf, _) = await SignInAsync(f, "boss", "Admin");
        var (_, _, id) = await SignInAsync(f, "erin", "User");
        using (var scope = f.Services.CreateScope())
        {
            var um = scope.ServiceProvider.GetRequiredService<UserManager<AppUser>>();
            await um.GenerateNewTwoFactorRecoveryCodesAsync((await um.FindByIdAsync(id.ToString()))!, 10);
        }

        var res = await admin.SendAsync(Req(HttpMethod.Post, $"/api/admin/users/{id}/reset-2fa", csrf));
        Assert.Equal(HttpStatusCode.NoContent, res.StatusCode);

        using var s2 = f.Services.CreateScope();
        var um2 = s2.ServiceProvider.GetRequiredService<UserManager<AppUser>>();
        var u = (await um2.FindByIdAsync(id.ToString()))!;
        Assert.False(u.TwoFactorEnabled);
        Assert.Equal(0, await um2.CountRecoveryCodesAsync(u));
    }
}

public class AdminPageTests
{
    [Fact]
    public async Task AdminPage_AdminSees200_UserIsRejected_ScriptServedFromSite()
    {
        using var f = new TestFactory();
        var (admin, _, _) = await AdminApiTests.SignInAsync(f, "boss", "Admin");
        var page = await admin.GetAsync("/Admin/Users");
        Assert.Equal(HttpStatusCode.OK, page.StatusCode);
        var html = await page.Content.ReadAsStringAsync();
        Assert.Contains("/js/admin-users.js", html);
        Assert.DoesNotContain("http://", html);
        Assert.Equal(HttpStatusCode.OK, (await admin.GetAsync("/js/admin-users.js")).StatusCode);

        var (user, _, _) = await AdminApiTests.SignInAsync(f, "alice", "User");
        var denied = await user.GetAsync("/Admin/Users");
        Assert.NotEqual(HttpStatusCode.OK, denied.StatusCode);
    }
}
