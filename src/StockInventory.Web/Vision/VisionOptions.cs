namespace StockInventory.Web.Vision;

/// <summary>FR-25 視覺辨識設定。ApiKey 只能來自環境變數 Vision__ApiKey(SEC-21)。</summary>
public sealed class VisionOptions
{
    public string? ApiKey { get; set; }
    public string Endpoint { get; set; } = "https://api.anthropic.com/v1/messages";
    public string Model { get; set; } = "claude-sonnet-5-5";
    public int TimeoutSeconds { get; set; } = 30;
    public int MaxImageBytes { get; set; } = 5_242_880;
    public int MaxCallsPerUserPerHour { get; set; } = 10;
    public int MaxCallsPerDay { get; set; } = 200;

    /// <summary>僅供開發:true 時改用固定假資料的辨識器(不呼叫外部服務);預設 false,正式環境一律忽略。</summary>
    public bool UseFake { get; set; }

    public bool IsEnabled => UseFake || !string.IsNullOrWhiteSpace(ApiKey);
}
