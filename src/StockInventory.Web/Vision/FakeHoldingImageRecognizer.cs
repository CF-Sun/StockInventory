using StockInventory.Core;

namespace StockInventory.Web.Vision;

/// <summary>開發用假辨識器(Vision:UseFake=true):回傳固定資料,涵蓋 OK、INVALID、NOT_FOUND、DUPLICATE_IN_IMAGE 等情境。</summary>
public sealed class FakeHoldingImageRecognizer : IHoldingImageRecognizer
{
    public Task<RecognitionOutput> RecognizeAsync(ReadOnlyMemory<byte> image, string mimeType, CancellationToken ct) =>
        Task.FromResult(new RecognitionOutput(true,
        [
            new RecognizedRow("2330", 1000, 520000),
            new RecognizedRow("0050", 2000, null),
            new RecognizedRow(null, 50, 3000),
            new RecognizedRow("00878", 5, 0),
            new RecognizedRow("1101", 1000, 40000),
            new RecognizedRow("1101", 2000, 80000),
        ]));
}
