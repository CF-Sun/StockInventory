using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Diagnostics;
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
    .MinimumLevel.Override("Microsoft.EntityFrameworkCore", Serilog.Events.LogEventLevel.Warning) // 不記錄每筆 SQL,避免洗掉重要訊息
    .MinimumLevel.Override("System.Net.Http.HttpClient", Serilog.Events.LogEventLevel.Warning)
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
builder.Services.Configure<StockInventory.Web.Vision.VisionOptions>(cfg.GetSection("Vision")); // Vision:ApiKey 只來自環境變數 Vision__ApiKey
builder.Services.Configure<LoggingDirOptions>(cfg.GetSection("Logging"));
builder.Services.Configure<DataProtectionDirOptions>(cfg.GetSection("DataProtection"));

// SEC-15:Data Protection 金鑰存在站台資料夾以外的固定路徑,重新發佈後 Cookie 與記住裝置仍有效
var dp = builder.Services.AddDataProtection().SetApplicationName("StockInventory");
if (cfg["DataProtection:KeyDirectory"] is { Length: > 0 } keyDir) dp.PersistKeysToFileSystem(new DirectoryInfo(keyDir));

// 連線字串只從環境變數/部署設定來,不放進 appsettings.json;缺少時 /health 會回報 db 失敗,不讓網站啟動失敗
builder.Services.AddDbContext<AppDbContext>(o =>
{
    // 開發用:Dev:UseInMemoryDatabase=true 時不連 SQL Server(資料只存在記憶體,重啟即消失)。正式環境不要開啟。
    if (cfg.GetValue<bool>("Dev:UseInMemoryDatabase")) o.UseInMemoryDatabase("StockInventoryDev");
    else o.UseSqlServer(cfg.GetConnectionString("Default") ?? "Server=;Database=StockInventory");
});
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton<StockInventory.Web.Hubs.ConnectionRegistry>();
builder.Services.AddSingleton<IActiveConnectionCounter>(sp => sp.GetRequiredService<StockInventory.Web.Hubs.ConnectionRegistry>());
builder.Services.AddSingleton<IQuoteSubscriber, StockInventory.Web.Hubs.ViewBroadcaster>();
builder.Services.AddSingleton<IAlertSender, SmtpAlertSender>();
builder.Services.AddSingleton<IQuoteSubscriber, StaleAlertSubscriber>();
builder.Services.AddSingleton<LoginFailureAlerts>();
if (cfg.GetValue("Quote:Enabled", true))
{
    // 真正的報價服務(T4.3)
    builder.Services.AddSingleton<QuoteCacheStore>();
    builder.Services.AddSingleton<IQuoteCache>(sp => sp.GetRequiredService<QuoteCacheStore>());
    builder.Services.AddHttpClient<IMisClient, MisClient>(c => c.DefaultRequestHeaders.UserAgent.ParseAdd("StockInventory/1.0"));
    builder.Services.AddSingleton<QuoteFetcher>();
    builder.Services.AddSingleton<IMarketStatusSource>(sp => sp.GetRequiredService<QuoteFetcher>());
    builder.Services.AddSingleton<IFetchRequester>(sp => sp.GetRequiredService<QuoteFetcher>());
    builder.Services.AddHostedService(sp => sp.GetRequiredService<QuoteFetcher>());
}
else
{
    // Quote:Enabled=false:使用假報價(§14),供開發與測試
    builder.Services.AddSingleton<IQuoteCache, FakeQuoteProvider>();
    builder.Services.AddSingleton<IMarketStatusSource, NullMarketStatusSource>();
    builder.Services.AddSingleton<IFetchRequester, NullFetchRequester>();
}
builder.Services.AddSignalR();
if (cfg.GetValue("Sync:Enabled", true))
{
    // 標的主檔與休市日同步(§7.7)
    builder.Services.AddHttpClient<IReferenceDataSource, HttpReferenceDataSource>(c =>
    {
        c.Timeout = TimeSpan.FromMinutes(2); // 上市 ISIN 清單約 9 MB
        c.DefaultRequestHeaders.UserAgent.ParseAdd("StockInventory/1.0");
    });
    builder.Services.AddHostedService<ReferenceDataSync>();
}
builder.Services.AddScoped<StockInventory.Web.Services.ViewService>();
builder.Services.AddScoped<StockInventory.Web.Services.HoldingService>();

// FR-25 截圖辨識:未設定 Vision:ApiKey 時功能停用(API-13 回 503),不影響網站啟動
if (cfg.GetValue<bool>("Vision:UseFake") && !builder.Environment.IsProduction())
    builder.Services.AddSingleton<StockInventory.Web.Vision.IHoldingImageRecognizer, StockInventory.Web.Vision.FakeHoldingImageRecognizer>();
else
{
    if (cfg.GetValue<bool>("Vision:UseFake")) Console.Error.WriteLine("正式環境忽略 Vision:UseFake");
    builder.Services.AddHttpClient<StockInventory.Web.Vision.IHoldingImageRecognizer, StockInventory.Web.Vision.AnthropicHoldingRecognizer>(
        (sp, c) => c.Timeout = TimeSpan.FromSeconds(Math.Max(1, cfg.GetValue("Vision:TimeoutSeconds", 30)) + 5));
}
StockInventory.Web.Vision.VisionRateLimiting.AddVisionRateLimiter(builder.Services, cfg);
builder.Services.AddAppIdentity(cfg);
builder.Services.AddCloudflareForwarding(cfg);
builder.Services.AddAuthorization(o => o.AddPolicy("AdminOnly", p => p.RequireRole(Roles.Admin)));
builder.Services.AddRazorPages(o =>
{
    o.Conventions.AuthorizeFolder("/");
    o.Conventions.AuthorizeFolder("/Admin", "AdminOnly");
    foreach (var page in new[] { "/Error", "/Account/Login", "/Account/LoginWith2fa", "/Account/LoginWithRecoveryCode", "/Account/Logout" })
        o.Conventions.AllowAnonymousToPage(page);
});
builder.Services.AddAntiforgery(o => { o.HeaderName = "X-CSRF-TOKEN"; o.Cookie.Name = "si.csrf"; o.Cookie.SecurePolicy = CookieSecurePolicy.Always; });
builder.Services.AddHsts(o => { o.MaxAge = TimeSpan.FromSeconds(15552000); o.IncludeSubDomains = false; o.Preload = false; });
builder.Services.AddHostedService<AdminSeeder>();
builder.Services.AddHostedService<SampleInstrumentSeeder>();

var app = builder.Build();

app.UseForwardedHeaders();
if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler(new ExceptionHandlerOptions { ExceptionHandler = ErrorHandling.HandleAsync }); // SEC-14
    app.UseHsts();
}
if (app.Environment.IsProduction() && string.IsNullOrWhiteSpace(cfg["DataProtection:KeyDirectory"]))
    app.Logger.LogWarning("正式環境未設定 DataProtection:KeyDirectory,重新發佈後登入 Cookie 將失效");
app.UseSecurityHeaders();
app.UseAuthentication();
app.UseApiCsrf(); // 必須在 UseAuthentication 之後,權杖才會綁定正確的使用者
app.UseAuthorization();
app.UseRateLimiter(); // FR-25 SEC-23:只作用於標記為辨識的端點
app.UseOnboardingGate();

app.UseStaticFiles();
app.MapRazorPages();
app.MapHub<StockInventory.Web.Hubs.ViewHub>("/hubs/view");
app.MapAdminApi();
app.MapPortfolioApi();
app.MapHoldingImportApi();
app.MapViewApi();
app.MapSettingsApi();
app.MapExportApi();
app.MapGet("/api/me", (ICurrentUser u) => Results.Json(new { userId = u.UserId })).RequireAuthorization();
app.MapGet("/health", HealthEndpoint.HandleAsync).AllowAnonymous();

app.Run();

public partial class Program;
