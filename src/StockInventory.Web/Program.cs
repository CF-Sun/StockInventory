using Microsoft.EntityFrameworkCore;
using Serilog;
using StockInventory.Data;
using StockInventory.Quotes;
using StockInventory.Web;
using StockInventory.Web.Options;

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

var app = builder.Build();

app.MapGet("/health", HealthEndpoint.HandleAsync).AllowAnonymous();

app.Run();

public partial class Program;
