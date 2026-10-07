using SkiaSharp;

namespace StockInventory.Web.Vision;

public enum ImageRejection { UnsupportedMedia, TooLarge }

public sealed class ImageRejectedException(ImageRejection reason) : Exception("圖片未通過驗證")
{
    public ImageRejection Reason { get; } = reason;
}

public sealed record SanitizedImage(byte[] Bytes, string MimeType);

/// <summary>
/// SEC-18、SEC-19:魔術位元組、實際解碼成功、尺寸與像素上限、單幀;全程記憶體,重新編碼為 JPEG 以去除 EXIF(含 GPS)與內嵌中繼資料。
/// 函式庫:SkiaSharp(MIT)。解碼前先只讀標頭檢查尺寸,防解壓炸彈。
/// </summary>
public static class ImageSanitizer
{
    public const int MaxLongEdge = 4096;
    public const long MaxPixels = 16_000_000;
    /// <summary>送出前縮小到此長邊(視覺 API 本來就會縮小過大圖片,縮小可降低傳輸量)。</summary>
    public const int SendLongEdge = 2048;
    public const int JpegQuality = 90;

    public static SanitizedImage Sanitize(ReadOnlySpan<byte> input)
    {
        if (!HasKnownMagic(input)) throw new ImageRejectedException(ImageRejection.UnsupportedMedia);
        try
        {
            using var data = SKData.CreateCopy(input);
            using var codec = SKCodec.Create(data);
            if (codec is null
                || codec.EncodedFormat is not (SKEncodedImageFormat.Jpeg or SKEncodedImageFormat.Png or SKEncodedImageFormat.Webp))
                throw new ImageRejectedException(ImageRejection.UnsupportedMedia);
            if (codec.FrameCount > 1) throw new ImageRejectedException(ImageRejection.UnsupportedMedia); // 動畫或多幀

            var info = codec.Info;
            if (info.Width <= 0 || info.Height <= 0) throw new ImageRejectedException(ImageRejection.UnsupportedMedia);
            if (Math.Max(info.Width, info.Height) > MaxLongEdge || (long)info.Width * info.Height > MaxPixels)
                throw new ImageRejectedException(ImageRejection.TooLarge);

            using var source = new SKBitmap(new SKImageInfo(info.Width, info.Height, SKColorType.Rgba8888, SKAlphaType.Premul));
            if (codec.GetPixels(source.Info, source.GetPixels()) != SKCodecResult.Success) // 截斷或損毀一律拒絕
                throw new ImageRejectedException(ImageRejection.UnsupportedMedia);

            var origin = codec.EncodedOrigin;
            var swap = origin is SKEncodedOrigin.LeftTop or SKEncodedOrigin.RightTop or SKEncodedOrigin.RightBottom or SKEncodedOrigin.LeftBottom;
            int ow = swap ? info.Height : info.Width, oh = swap ? info.Width : info.Height;
            var scale = Math.Max(ow, oh) > SendLongEdge ? (float)SendLongEdge / Math.Max(ow, oh) : 1f;
            var tw = Math.Max(1, (int)Math.Round(ow * scale));
            var th = Math.Max(1, (int)Math.Round(oh * scale));

            using var target = new SKBitmap(new SKImageInfo(tw, th, SKColorType.Rgba8888, SKAlphaType.Premul));
            using (var canvas = new SKCanvas(target))
            {
                canvas.Clear(SKColors.White); // 透明區域以白底合成
                canvas.Scale(scale, scale);
                var m = OrientationMatrix(origin, info.Width, info.Height);
                canvas.Concat(in m);
                using var image = SKImage.FromBitmap(source);
                canvas.DrawImage(image, 0, 0, new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.Linear));
            }

            using var outImage = SKImage.FromBitmap(target);
            using var encoded = outImage.Encode(SKEncodedImageFormat.Jpeg, JpegQuality); // 重新編碼:不帶任何中繼資料
            if (encoded is null) throw new ImageRejectedException(ImageRejection.UnsupportedMedia);
            return new SanitizedImage(encoded.ToArray(), "image/jpeg");
        }
        catch (ImageRejectedException) { throw; }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or OutOfMemoryException or IOException)
        {
            throw new ImageRejectedException(ImageRejection.UnsupportedMedia); // 不保留例外內容
        }
    }

    private static bool HasKnownMagic(ReadOnlySpan<byte> b) =>
        (b.Length >= 3 && b[0] == 0xFF && b[1] == 0xD8 && b[2] == 0xFF)
        || (b.Length >= 8 && b[..8].SequenceEqual(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }))
        || (b.Length >= 12 && b[..4].SequenceEqual("RIFF"u8) && b.Slice(8, 4).SequenceEqual("WEBP"u8));

    /// <summary>EXIF 方向 → 把原圖座標轉成「正向」座標的矩陣。</summary>
    private static SKMatrix OrientationMatrix(SKEncodedOrigin o, int w, int h) => o switch
    {
        SKEncodedOrigin.TopRight => new SKMatrix(-1, 0, w, 0, 1, 0, 0, 0, 1),
        SKEncodedOrigin.BottomRight => new SKMatrix(-1, 0, w, 0, -1, h, 0, 0, 1),
        SKEncodedOrigin.BottomLeft => new SKMatrix(1, 0, 0, 0, -1, h, 0, 0, 1),
        SKEncodedOrigin.LeftTop => new SKMatrix(0, 1, 0, 1, 0, 0, 0, 0, 1),
        SKEncodedOrigin.RightTop => new SKMatrix(0, -1, h, 1, 0, 0, 0, 0, 1),
        SKEncodedOrigin.RightBottom => new SKMatrix(0, -1, h, -1, 0, w, 0, 0, 1),
        SKEncodedOrigin.LeftBottom => new SKMatrix(0, 1, 0, -1, 0, w, 0, 0, 1),
        _ => SKMatrix.Identity,
    };
}
