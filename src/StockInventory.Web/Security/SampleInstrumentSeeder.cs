using Microsoft.EntityFrameworkCore;
using StockInventory.Core;
using StockInventory.Data;

namespace StockInventory.Web.Security;

/// <summary>
/// 開發用:在標的主檔同步(T2.1)完成前,設定 Dev:SeedSampleInstruments=true 時補上 0050、2330 兩檔標的,
/// 讓假報價(§14)可搭配測試。預設關閉,正式環境不要開啟。
/// </summary>
public sealed class SampleInstrumentSeeder(IServiceScopeFactory scopes, IConfiguration cfg, ILogger<SampleInstrumentSeeder> log)
    : IHostedService
{
    public async Task StartAsync(CancellationToken ct)
    {
        if (!cfg.GetValue<bool>("Dev:SeedSampleInstruments")) return;
        try
        {
            using var scope = scopes.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var now = DateTime.UtcNow;
            foreach (var (sym, name, kind) in new[] { ("0050", "元大台灣50", InstrumentKind.Etf), ("2330", "台積電", InstrumentKind.Stock) })
                if (!await db.Instruments.AnyAsync(i => i.Symbol == sym, ct))
                    db.Instruments.Add(new Instrument { Symbol = sym, Name = name, Market = Market.Twse, Kind = kind, UpdatedAtUtc = now });
            await db.SaveChangesAsync(ct);
            log.LogWarning("已啟用開發用範例標的(Dev:SeedSampleInstruments)");
        }
        catch (Exception ex) { log.LogError("範例標的建立失敗 ({Type})", ex.GetType().Name); }
    }

    public Task StopAsync(CancellationToken ct) => Task.CompletedTask;
}
