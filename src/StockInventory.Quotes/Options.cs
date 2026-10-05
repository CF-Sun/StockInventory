namespace StockInventory.Quotes;

public sealed class QuoteOptions
{
    public int BatchSize { get; set; } = 30;
    public int MaxRequestsPer5s { get; set; } = 3;
    public int ActiveIntervalSeconds { get; set; } = 5;
    public int IdleIntervalSeconds { get; set; } = 300;
    public int TimeoutSeconds { get; set; } = 5;
    public int StaleSeconds { get; set; } = 180;
}

public sealed class MarketOptions
{
    public TimeSpan OpenTime { get; set; } = new(8, 30, 0);
    public TimeSpan CloseTime { get; set; } = new(14, 35, 0);
    public string TimeZoneId { get; set; } = "Taipei Standard Time";
}

public sealed class SmtpOptions
{
    public string? Host { get; set; }
    public int Port { get; set; } = 587;
    public string? UserName { get; set; }
    public string? From { get; set; }
    public string? Password { get; set; } // 只能來自環境變數
}

public sealed class AlertOptions
{
    public string[] To { get; set; } = [];
    public int CooldownMinutes { get; set; } = 30;
    public int LoginFailThreshold { get; set; } = 10;
    public int LoginFailWindowMinutes { get; set; } = 10;
}
