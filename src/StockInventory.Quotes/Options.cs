namespace StockInventory.Quotes;

public sealed class QuoteOptions
{
    public const int DefaultUnwatchedIntervalSeconds = 30;
    public const int MaxUnwatchedIntervalSeconds = 300;

    public int BatchSize { get; set; } = 30;
    public int MaxRequestsPer5s { get; set; } = 3;
    public int ActiveIntervalSeconds { get; set; } = 5;
    /// <summary>v1.5:盤中(Open)沒有連線時的抓取間隔;範圍 ActiveIntervalSeconds 至 <see cref="MaxUnwatchedIntervalSeconds"/>。</summary>
    public int UnwatchedIntervalSeconds { get; set; } = DefaultUnwatchedIntervalSeconds;
    /// <summary>只用於非開盤時段補抓快取缺價標的的節流(v1.5 起不再用於盤中,也不參與 stale 門檻)。</summary>
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

/// <summary>FR-26 當日走勢設定(SPEC §4 <c>Intraday:*</c>)。皆非機敏設定。</summary>
public sealed class IntradayOptions
{
    public bool Enabled { get; set; } = true;
    public TimeSpan RecordStartTime { get; set; } = new(9, 0, 0);
    public TimeSpan RecordEndTime { get; set; } = new(13, 30, 0);
    public int RetentionDays { get; set; } = 7;
    public int FlushSeconds { get; set; } = 60;
    public int MaxPoints { get; set; } = 90;
    public int GapBreakSeconds { get; set; } = 180;
    public int MaxRequestsPerMinute { get; set; } = 30;

    /// <summary>
    /// 檢查設定;不合理的值回報為設定錯誤並改用預設(啟動時記錄,不讓網站啟動失敗)。
    /// 回傳修正後的新物件;<paramref name="errors"/> 為每個被修正的設定鍵說明。
    /// </summary>
    public IntradayOptions Normalize(out IReadOnlyList<string> errors)
    {
        var d = new IntradayOptions();
        var list = new List<string>();
        var r = new IntradayOptions { Enabled = Enabled };

        r.RecordStartTime = RecordStartTime; r.RecordEndTime = RecordEndTime;
        if (RecordStartTime < TimeSpan.Zero || RecordEndTime >= TimeSpan.FromHours(24) - TimeSpan.FromMinutes(1)
            || RecordStartTime > RecordEndTime)
        {
            list.Add($"Intraday:RecordStartTime/RecordEndTime({RecordStartTime}/{RecordEndTime})不合理,改用 {d.RecordStartTime}/{d.RecordEndTime}");
            (r.RecordStartTime, r.RecordEndTime) = (d.RecordStartTime, d.RecordEndTime);
        }
        r.RetentionDays = Pick(RetentionDays, 1, 3650, d.RetentionDays, nameof(RetentionDays), list);
        r.FlushSeconds = Pick(FlushSeconds, 1, 3600, d.FlushSeconds, nameof(FlushSeconds), list);
        r.MaxPoints = Pick(MaxPoints, 2, 1000, d.MaxPoints, nameof(MaxPoints), list);
        r.GapBreakSeconds = Pick(GapBreakSeconds, 1, 86400, d.GapBreakSeconds, nameof(GapBreakSeconds), list);
        r.MaxRequestsPerMinute = Pick(MaxRequestsPerMinute, 1, 10000, d.MaxRequestsPerMinute, nameof(MaxRequestsPerMinute), list);
        errors = list;
        return r;
    }

    private static int Pick(int value, int min, int max, int fallback, string name, List<string> errors)
    {
        if (value >= min && value <= max) return value;
        errors.Add($"Intraday:{name}({value})超出範圍 {min} 至 {max},改用預設 {fallback}");
        return fallback;
    }
}
