using StockInventory.Core;

namespace StockInventory.Web.Vision;

/// <summary>視覺模型輸出(已嚴格解析)。數字為圖上原樣數值,單位一律為股。</summary>
public sealed record RecognitionOutput(bool Recognized, IReadOnlyList<RecognizedRow> Rows);

/// <summary>視覺服務失敗(HTTP 錯誤、非合法 JSON、不符 schema 等)。訊息為固定字串,不得夾帶模型回應(SEC-20)。</summary>
public sealed class VisionUpstreamException(string message, int? statusCode = null) : Exception(message)
{
    public int? StatusCode { get; } = statusCode;
}

/// <summary>視覺服務逾時。</summary>
public sealed class VisionTimeoutException() : Exception("視覺服務逾時");

/// <summary>
/// 輸入為已驗證並重新編碼的圖片位元組與 MIME;輸出為模型輸出 DTO。
/// 實作須丟出 <see cref="VisionUpstreamException"/> 或 <see cref="VisionTimeoutException"/>,不得記錄圖片或模型回應。
/// </summary>
public interface IHoldingImageRecognizer
{
    Task<RecognitionOutput> RecognizeAsync(ReadOnlyMemory<byte> image, string mimeType, CancellationToken ct);
}
