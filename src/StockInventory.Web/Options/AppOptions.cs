namespace StockInventory.Web.Options;

public sealed class LimitsOptions
{
    public int MaxPortfoliosPerUser { get; set; } = 20;
    public int MaxHoldingsPerPortfolio { get; set; } = 200;
}

public sealed class FeesOptions
{
    public decimal MinFee { get; set; } = 20m;
    public decimal StockTaxRate { get; set; } = 0.003m;
    public decimal EtfTaxRate { get; set; } = 0.001m;
}

public sealed class AuthOptions
{
    public int IdleMinutes { get; set; } = 30;
    public int TrustDeviceDays { get; set; } = 30;
    public int LockoutMaxFailures { get; set; } = 5;
    public int LockoutMinutes { get; set; } = 15;
    public int MinPasswordLength { get; set; } = 12;
}

public sealed class SeedOptions
{
    public string? AdminUserName { get; set; }
    public string? AdminEmail { get; set; }
    public string? AdminInitialPassword { get; set; } // 只能來自環境變數
}

public sealed class LoggingDirOptions
{
    public string? Directory { get; set; }
}

public sealed class DataProtectionDirOptions
{
    public string? KeyDirectory { get; set; }
}
