using StockInventory.Web.Api;
using Microsoft.EntityFrameworkCore;
using Serilog;
using StockInventory.Data;
using StockInventory.Quotes;
using StockInventory.Web;
using StockInventory.Web.Options;
using StockInventory.Quotes;
using StockInventory.Web.Security;
using Microsoft.AspNetCore.Builder;

var builder = WebApplication.CreateBuilder(args);

// 日誌:每日分檔、保留 30 天、放站台以外(§0.7:不記錄密碼、TOTP、Cookie、持股成本與股數)
var logDir = builder.Configuration["Logging:Directory"] ?? Path.Combine(AppContext.BaseDirectory, "logs");
builder.Host.UseSerilog((_, lc) => lc
    .MinimumLevel.Information()
    .MinimumLevel.Override("Microsoft.AspNetCore", Serilog.Events.LogEventLevel.Warning)
    .WriteTo.File(Path.Combine(logDir, "stockinventory-.log"),
        rollingInterval: RollingInterval.Day, retainedFileCountLimit: 30));

var cfg = builder.Configuration;
builder.Services.Configure<LimitsOptions>(cfg.GetSection("Limits"));
builder.Services.Configure<QuoteOptions>(cfg.GetSection("Quote"));
builder.Services.Configure<MarketOptions>(cfg.GetSection("Market"));
builder.Services.Configure<FeesOptions>(cfg.GetSection("Fees"));
builder.Services.Configure<AuthOptions>(cfg.GetSection("Auth"));
builder.Services.Configure<SmtpOptions>(cfg.GetSection("Smtp"));
builder.Services.Configure<AlertOptions>(cfg.GetSection("Alert"));
builder.Services.Configure<SeedOptions>(cfg.GetSection("Seed"));
builder.Services.Configure<LoggingDirOptions>(cfg.GetSection("Logging"));
builder.Services.Configure<DataProtectionDirOptions>(cfg.GetSection("DataProtection"));

// 連線字串只從環境變數/部署設定來,不放進 appsettings.json;缺少時 /health 會回報 db 失敗,不讓網站啟動失敗
builder.Services.AddDbContext<AppDbContext>(o =>
    o.UseSqlServer(cfg.GetConnectionString("Default") ?? "Server=;Database=StockInventory"));
builder.Services.AddSingleton<IMarketStatusSource, NullMarketStatusSource>();
builder.Services.AddSingleton<IFetchRequester, NullFetchRequester>();
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton<IQuoteCache, FakeQuoteProvider>(); // P4 完成前使用假報價(§14)
builder.Services.AddScoped<StockInventory.Web.Services.ViewService>();
builder.Services.AddAppIdentity(cfg);
builder.Services.AddCloudflareForwarding(cfg);
builder.Services.AddAuthorization(o => o.AddPolicy("AdminOnly", p => p.RequireRole(Roles.Admin)));
builder.Services.AddRazorPages(o =>
{
    o.Conventions.AuthorizeFolder("/");
    o.Conventions.AuthorizeFolder("/Admin", "AdminOnly");
    foreach (var page in new[] { "/Account/Login", "/Account/LoginWith2fa", "/Account/LoginWithRecoveryCode", "/Account/Logout" })
        o.Conventions.AllowAnonymousToPage(page);
});
builder.Services.AddAntiforgery(o => { o.HeaderName = "X-CSRF-TOKEN"; o.Cookie.Name = "si.csrf"; o.Cookie.SecurePolicy = CookieSecurePolicy.Always; });
builder.Services.AddHsts(o => { o.MaxAge = TimeSpan.FromSeconds(15552000); o.IncludeSubDomains = false; o.Preload = false; });
builder.Services.AddHostedService<AdminSeeder>();
builder.Services.AddHostedService<SampleInstrumentSeeder>();

var app = builder.Build();

app.UseForwardedHeaders();
if (!app.Environment.IsDevelopment()) app.UseHsts();
app.UseSecurityHeaders();
app.UseAuthentication();
app.UseApiCsrf(); // 必須在 UseAuthentication 之後,權杖才會綁定正確的使用者
app.UseAuthorization();
app.UseOnboardingGate();

app.UseStaticFiles();
app.MapRazorPages();
app.MapAdminApi();
app.MapPortfolioApi();
app.MapViewApi();
app.MapSettingsApi();
app.MapExportApi();
app.MapGet("/api/me", (ICurrentUser u) => Results.Json(new { userId = u.UserId })).RequireAuthorization();
app.MapGet("/health", HealthEndpoint.HandleAsync).AllowAnonymous();

app.Run();

public partial class Program;
