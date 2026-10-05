using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace StockInventory.Data;

/// <summary>僅供 dotnet ef 使用;連線字串只讀環境變數,缺少時用不連線的佔位值(產生 Migration 不需連線)。</summary>
public class DesignTimeFactory : IDesignTimeDbContextFactory<AppDbContext>
{
    public AppDbContext CreateDbContext(string[] args)
    {
        var cs = Environment.GetEnvironmentVariable("ConnectionStrings__Default")
                 ?? "Server=.;Database=StockInventory;Integrated Security=true;TrustServerCertificate=true";
        return new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseSqlServer(cs).Options);
    }
}
