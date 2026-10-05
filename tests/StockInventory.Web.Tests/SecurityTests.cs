using System.Net;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;
using StockInventory.Data;
using Xunit;

namespace StockInventory.Web.Tests;

public class SecurityTests
{
    static readonly Dictionary<string, string?> Seed = new()
    {
        ["Seed:AdminUserName"] = "boss",
        ["Seed:AdminEmail"] = "boss@example.com",
        ["Seed:AdminInitialPassword"] = "Initial-Password-12",
    };

    [Fact]
    public async Task Seeds_FirstAdmin_WithMustChangePassword()
    {
        using var f = new TestFactory(Seed);
        using var _ = f.CreateClient(); // 啟動主機,觸發 AdminSeeder
        using var scope = f.Services.CreateScope();
        var um = scope.ServiceProvider.GetRequiredService<UserManager<AppUser>>();
        var u = await um.FindByNameAsync("boss");
        Assert.NotNull(u);
        Assert.True(u!.MustChangePassword);
        Assert.True(u.IsActive);
        Assert.Contains("Admin", await um.GetRolesAsync(u));
        Assert.False(u.TwoFactorEnabled);
    }

    [Fact]
    public async Task NoSeed_WhenUsersExist_DoesNotAddSecondAdmin()
    {
        using var f = new TestFactory(Seed);
        using var _ = f.CreateClient();
        using var scope = f.Services.CreateScope();
        var seeder = scope.ServiceProvider.GetServices<Microsoft.Extensions.Hosting.IHostedService>()
            .OfType<Security.AdminSeeder>().Single();
        await seeder.SeedAsync();
        Assert.Single(scope.ServiceProvider.GetRequiredService<UserManager<AppUser>>().Users.ToList());
    }

    [Fact]
    public async Task PasswordPolicy_Min12_NoComplexity()
    {
        using var f = new TestFactory();
        using var _ = f.CreateClient();
        using var scope = f.Services.CreateScope();
        var um = scope.ServiceProvider.GetRequiredService<UserManager<AppUser>>();
        var u = new AppUser { UserName = "a", Email = "a@example.com", CreatedAtUtc = DateTime.UtcNow };
        Assert.False((await um.CreateAsync(u, "short-pass1")).Succeeded);          // 11 字元
        Assert.True((await um.CreateAsync(u, "alllowercase1")).Succeeded);         // 13 字元,無複雜度要求
        Assert.Equal(5, um.Options.Lockout.MaxFailedAccessAttempts);
        Assert.Equal(TimeSpan.FromMinutes(15), um.Options.Lockout.DefaultLockoutTimeSpan);
    }

    [Fact]
    public async Task SecurityHeaders_Present()
    {
        using var f = new TestFactory();
        var res = await f.CreateClient().GetAsync("/health");
        Assert.Equal("nosniff", res.Headers.GetValues("X-Content-Type-Options").Single());
        Assert.Equal("strict-origin-when-cross-origin", res.Headers.GetValues("Referrer-Policy").Single());
        Assert.Equal("default-src 'self'; frame-ancestors 'none'", res.Headers.GetValues("Content-Security-Policy").Single());
    }

    [Fact]
    public async Task Api_Unauthenticated_Returns401Problem_NotRedirect()
    {
        using var f = new TestFactory();
        var c = f.CreateClient(new() { AllowAutoRedirect = false });
        var res = await c.GetAsync("/api/me");
        Assert.Equal(HttpStatusCode.Unauthorized, res.StatusCode);
        Assert.Equal("application/problem+json", res.Content.Headers.ContentType!.MediaType);
        Assert.Contains("UNAUTHENTICATED", await res.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Api_Post_WithoutCsrfToken_Returns400()
    {
        using var f = new TestFactory();
        var res = await f.CreateClient(new() { BaseAddress = new Uri("https://localhost") }).PostAsync("/api/portfolios", new StringContent("{}"));
        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
        Assert.Contains("VALIDATION_FAILED", await res.Content.ReadAsStringAsync());
    }
}
