using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using StockInventory.Data;

namespace StockInventory.Web.Tests;

/// <summary>以 InMemory 取代 SQL Server,供不需要關聯式行為的測試使用。</summary>
public sealed class TestFactory(Dictionary<string, string?>? settings = null) : WebApplicationFactory<Program>
{
    readonly string _db = Guid.NewGuid().ToString();

    protected override void ConfigureWebHost(IWebHostBuilder b)
    {
        b.UseSetting("Quote:Enabled", "false");
        b.UseSetting("Logging:Directory", Path.Combine(Path.GetTempPath(), "si-test-logs"));
        foreach (var kv in settings ?? []) b.UseSetting(kv.Key, kv.Value);
        b.ConfigureServices(sv =>
        {
            foreach (var d in sv.Where(d => d.ServiceType == typeof(DbContextOptions<AppDbContext>)
                                            || d.ServiceType == typeof(DbContextOptions)).ToList())
                sv.Remove(d);
            sv.AddDbContext<AppDbContext>(o => o.UseInMemoryDatabase(_db));
        });
    }
}
