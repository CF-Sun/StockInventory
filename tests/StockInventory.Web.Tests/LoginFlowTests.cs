using System.Net;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;
using StockInventory.Data;
using Xunit;

namespace StockInventory.Web.Tests;

public class LoginFlowTests
{
    const string Pw = "Initial-Password-12";
    static readonly Dictionary<string, string?> Seed = new()
    {
        ["Seed:AdminUserName"] = "boss", ["Seed:AdminEmail"] = "boss@example.com", ["Seed:AdminInitialPassword"] = Pw,
    };

    static HttpClient NewClient(TestFactory f) =>
        f.CreateClient(new() { BaseAddress = new Uri("https://localhost"), AllowAutoRedirect = false });

    static async Task<HttpResponseMessage> Post(HttpClient c, string path, Dictionary<string, string> form, string? tokenPage = null)
    {
        var page = await c.GetStringAsync(tokenPage ?? path);
        var token = Regex.Match(page, "name=\"__RequestVerificationToken\"[^>]*value=\"([^\"]+)\"").Groups[1].Value;
        form["__RequestVerificationToken"] = token;
        return await c.PostAsync(path, new FormUrlEncodedContent(form));
    }

    static Task<HttpResponseMessage> Login(HttpClient c, string user, string pw) =>
        Post(c, "/Account/Login", new() { ["UserName"] = user, ["Password"] = pw });

    static async Task<string> Body(HttpResponseMessage r) => WebUtility.HtmlDecode(await r.Content.ReadAsStringAsync());

    static string Totp(string base32)
    {
        const string alpha = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";
        var bits = string.Concat(base32.Replace(" ", "").ToUpperInvariant().Select(ch => Convert.ToString(alpha.IndexOf(ch), 2).PadLeft(5, '0')));
        var key = Enumerable.Range(0, bits.Length / 8).Select(i => Convert.ToByte(bits.Substring(i * 8, 8), 2)).ToArray();
        var step = BitConverter.GetBytes(DateTimeOffset.UtcNow.ToUnixTimeSeconds() / 30).Reverse().ToArray();
        var h = HMACSHA1.HashData(key, step);
        var o = h[^1] & 0xF;
        var n = ((h[o] & 0x7F) << 24) | (h[o + 1] << 16) | (h[o + 2] << 8) | h[o + 3];
        return (n % 1_000_000).ToString("D6");
    }

    [Fact]
    public async Task WrongPassword_And_UnknownUser_ShowSameMessage()
    {
        using var f = new TestFactory(Seed);
        var c = NewClient(f);
        var a = await Body(await Login(c, "boss", "wrong-password-1"));
        var b = await Body(await Login(c, "nobody", "wrong-password-1"));
        Assert.Contains("帳號或密碼錯誤", a);
        Assert.Contains("帳號或密碼錯誤", b);
    }

    [Fact]
    public async Task InactiveUser_CannotLogin_SameMessage()
    {
        using var f = new TestFactory(Seed);
        var c = NewClient(f);
        using (var scope = f.Services.CreateScope())
        {
            var um = scope.ServiceProvider.GetRequiredService<UserManager<AppUser>>();
            var u = (await um.FindByNameAsync("boss"))!;
            u.IsActive = false;
            await um.UpdateAsync(u);
        }
        var res = await Login(c, "boss", Pw);
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        Assert.Contains("帳號或密碼錯誤", await Body(res));
    }

    [Fact] // IT-06(部分):失敗 5 次鎖定,鎖定後即使密碼正確也被拒且訊息相同
    public async Task FiveFailures_LockAccount()
    {
        using var f = new TestFactory(Seed);
        var c = NewClient(f);
        for (var i = 0; i < 5; i++) await Login(c, "boss", "wrong-password-1");
        var res = await Login(c, "boss", Pw);
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        Assert.Contains("帳號或密碼錯誤", await Body(res));
    }

    [Fact] // SEC-02:未改密碼/未設 TOTP 不能進主畫面與 API
    public async Task Onboarding_Gate_Then_Full_Totp_Flow()
    {
        using var f = new TestFactory(Seed);
        var c = NewClient(f);

        var login = await Login(c, "boss", Pw);
        Assert.Equal(HttpStatusCode.Redirect, login.StatusCode);

        // 必須先改密碼
        var home = await c.GetAsync("/");
        Assert.Equal("/Account/Manage/Password", home.Headers.Location!.OriginalString);
        var api = await c.GetAsync("/api/me");
        Assert.Equal(HttpStatusCode.Forbidden, api.StatusCode);

        var bad = await Post(c, "/Account/Manage/Password", new() { ["CurrentPassword"] = Pw, ["NewPassword"] = "short", ["ConfirmPassword"] = "short" });
        Assert.Equal(HttpStatusCode.OK, bad.StatusCode);
        const string NewPw = "Brand-New-Password-34";
        var ok = await Post(c, "/Account/Manage/Password", new() { ["CurrentPassword"] = Pw, ["NewPassword"] = NewPw, ["ConfirmPassword"] = NewPw });
        Assert.Equal("/Account/Manage/2fa", ok.Headers.Location!.OriginalString);

        // 再設 TOTP
        Assert.Equal("/Account/Manage/2fa", (await c.GetAsync("/")).Headers.Location!.OriginalString);
        var page = await Body(await c.GetAsync("/Account/Manage/2fa"));
        Assert.Contains("<svg", page); // QR 由本站產生
        string key;
        using (var scope = f.Services.CreateScope())
        {
            var um = scope.ServiceProvider.GetRequiredService<UserManager<AppUser>>();
            key = (await um.GetAuthenticatorKeyAsync((await um.FindByNameAsync("boss"))!))!;
        }
        var wrong = await Post(c, "/Account/Manage/2fa", new() { ["Code"] = "000000" });
        Assert.Contains("驗證碼錯誤", await Body(wrong));
        var enable = await Post(c, "/Account/Manage/2fa", new() { ["Code"] = Totp(key) });
        var html = await Body(enable);
        var codes = Regex.Matches(html, "<li><code>([^<]+)</code></li>").Select(m => m.Groups[1].Value).ToList();
        Assert.Equal(10, codes.Count);

        // 現在可進主畫面與 API
        Assert.Equal(HttpStatusCode.OK, (await c.GetAsync("/")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await c.GetAsync("/api/me")).StatusCode);

        // 登出後,密碼 + TOTP 登入
        await Post(c, "/Account/Logout", new(), tokenPage: "/");
        Assert.Equal(HttpStatusCode.Redirect, (await c.GetAsync("/")).StatusCode);
        var l2 = await Login(c, "boss", NewPw);
        Assert.Contains("LoginWith2fa", l2.Headers.Location!.OriginalString);
        Assert.Equal(HttpStatusCode.Redirect, (await c.GetAsync("/")).StatusCode); // 尚未完成 TOTP
        var v = await Post(c, "/Account/LoginWith2fa", new() { ["Code"] = Totp(key) });
        Assert.Equal(HttpStatusCode.Redirect, v.StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await c.GetAsync("/")).StatusCode);

        // 還原碼登入(新 client),且同一組只能用一次
        var c2 = NewClient(f);
        await Login(c2, "boss", NewPw);
        var r = await Post(c2, "/Account/LoginWithRecoveryCode", new() { ["RecoveryCode"] = codes[0] });
        Assert.Equal(HttpStatusCode.Redirect, r.StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await c2.GetAsync("/")).StatusCode);
        var c3 = NewClient(f);
        await Login(c3, "boss", NewPw);
        var reuse = await Post(c3, "/Account/LoginWithRecoveryCode", new() { ["RecoveryCode"] = codes[0] });
        Assert.Contains("還原碼錯誤", await Body(reuse));
    }
}
